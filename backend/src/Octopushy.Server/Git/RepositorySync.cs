using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Server.Changes;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;

namespace Octopushy.Server.Git;

/// <summary>
/// Polls the lab repository's branch and, when its <c>octopushy.yaml</c> names one, the workflows repository's tags. Each new
/// revision, a new commit or new releases that change what a reference resolves to, is read and applied. Polling works with
/// any Git server; a sync can also be requested at any time.
/// </summary>
public sealed class RepositorySync(
    IServiceScopeFactory scopes,
    RepositoryMirror mirror,
    WorkflowsMirrors workflowMirrors,
    RevisionCache revisions,
    RunEngine engine,
    IOptions<OctopushyOptions> options,
    TimeProvider clock,
    ILogger<RepositorySync> logger) : BackgroundService
{
    public const string CredentialKey = "system/git/credential";

    private readonly SemaphoreSlim _trigger = new(0, 1);

    // One sync at a time: two that overlap could record an older head after a newer one.
    private readonly SemaphoreSlim _running = new(1, 1);

    /// <summary>Asks for a fetch now instead of at the next poll.</summary>
    public void Trigger()
    {
        if (_trigger.CurrentCount == 0)
        {
            try
            {
                _trigger.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    /// <summary>
    /// Syncs now and returns once the head is read and applied, as <b>Sync now</b> does but without waiting for the background
    /// loop: a change from the UI follows its commit into a revision at once. It never runs beside the loop's own sync.
    /// </summary>
    public async Task SyncNowAsync(CancellationToken cancellationToken)
    {
        await _running.WaitAsync(cancellationToken);
        try
        {
            await SyncOnceAsync(cancellationToken);
        }
        finally
        {
            _running.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // At startup, before the first fetch: builds that the revision already applied needs, which a failed fetch would delay.
        try
        {
            await engine.QueueImageBuildsAsync(stoppingToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Queueing image builds at startup failed.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var poll = TimeSpan.FromSeconds(60);
            try
            {
                await _running.WaitAsync(stoppingToken);
                try
                {
                    poll = await SyncOnceAsync(stoppingToken);
                }
                finally
                {
                    _running.Release();
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Reading the lab repository failed.");
            }

            try
            {
                await _trigger.WaitAsync(poll, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Fetches once and loads the head revision if it is new; returns the poll interval.</summary>
    public async Task<TimeSpan> SyncOnceAsync(CancellationToken cancellationToken)
    {
        // Fetch and parse outside the controller gate. Only the captured source may persist
        // their result: a settings change during either phase discards the stale discovery.
        var settings = await engine.ExecuteAsync(async (db, _) =>
        {
            var current = await db.RepositorySettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, cancellationToken);
            if (current is not null) return current;
            current = new RepositorySettings { UpdatedAt = clock.GetUtcNow() };
            db.RepositorySettings.Add(current);
            await db.SaveChangesAsync(cancellationToken);
            return current;
        }, cancellationToken);
        var poll = TimeSpan.FromSeconds(Math.Max(10, settings.PollSeconds));
        if (settings.Url is null) return poll;

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<VariableStore>();
            var credential = await CredentialAsync(store, settings, cancellationToken);
            // Changes whose push is not confirmed, or whose commit is being followed, picked before the fetch: everything they
            // hold was pushed before it began, so the head it returns can judge them.
            var watched = await ChangeReconciler.SelectAsync(engine, options.Value.Changes.ReconcileBatch, cancellationToken);
            var head = await mirror.FetchAsync(settings.Url, settings.Branch, credential, cancellationToken);
            await ChangeReconciler.ReconcileAsync(engine, mirror, settings.Branch, watched, head, clock, cancellationToken);
            var info = await mirror.CommitInfoAsync(head, cancellationToken);
            // The workflows repository is the one this commit names, fetched on the same poll as the branch.
            var workflows = WorkflowsSetting.Read(await mirror.SnapshotAsync(head, cancellationToken));
            if (workflows.Repository is { } workflowsUrl)
                await FetchWorkflowsAsync(store, settings, workflowsUrl, workflows.Credential, cancellationToken);
            var resolved = await revisions.ResolveAsync(head, ReleasesAsync, cancellationToken);
            var repository = resolved.Repository;
            await engine.ExecuteAsync(async (db, services) =>
            {
                // The head moving to a revision, and that revision's inventory (desired inputs and proposed runs), are one
                // transaction: a crash between them used to leave a head whose runs were never proposed, and the next sync skipped it.
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var current = await db.RepositorySettings.SingleAsync(s => s.Id == 1, cancellationToken);
                if (current.SourceId != settings.SourceId || current.Url != settings.Url || current.Branch != settings.Branch)
                    return false;
                current.LastFetchAt = clock.GetUtcNow();
                current.LastFetchError = null;
                current.HeadCommit = head;
                current.WorkflowsRepository = resolved.WorkflowsRepository;
                if (resolved.WorkflowsRepository is null)
                    (current.WorkflowsLastFetchAt, current.WorkflowsLastFetchError) = (null, null);
                // A revision is its commit and what its references resolved to. Returning to one already loaded from
                // this source, such as a branch rolled back, reuses it.
                var loaded = await RepositoryTrust.CurrentRevisions(db)
                    .Where(r => r.Commit == head && r.WorkflowsDigest == resolved.Digest)
                    .OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
                if (loaded is not null && current.HeadRevisionId == loaded.Id && loaded.InventoryAppliedAt is not null)
                {
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return false;
                }
                var revision = loaded ?? new DefinitionRevision
                {
                    Id = Guid.CreateVersion7(),
                    SourceId = settings.SourceId,
                    Commit = head,
                    WorkflowsRepository = resolved.WorkflowsRepository,
                    WorkflowsDigest = resolved.Digest,
                    Resolutions = resolved.ResolutionsJson,
                    Subject = info.Subject,
                    Author = info.Author,
                    CommittedAt = info.CommittedAt,
                    LoadedAt = clock.GetUtcNow(),
                    RepositoryValid = repository.RepositoryValid,
                    ErrorCount = repository.Issues.Count(i => i.Severity == IssueSeverity.Error),
                    WarningCount = repository.Issues.Count(i => i.Severity == IssueSeverity.Warning),
                    Issues = JsonSerializer.Serialize(repository.Issues, InventoryUpdater.IssueJson),
                };
                if (loaded is null) db.Revisions.Add(revision);
                current.HeadRevisionId = revision.Id;
                await db.SaveChangesAsync(cancellationToken);
                await services.GetRequiredService<InventoryUpdater>().ApplyAsync(db, revision, repository, cancellationToken);
                revision.InventoryAppliedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return true;
            }, cancellationToken);
            // What deploys generated and nothing at this head uses any more: on every sync, so a removal that failed is retried.
            // Against the head this sync has just fetched, every definition file read as plain YAML, valid or not.
            if (GeneratedValues.ReferencedKeys(repository.Root) is { } referenced)
            {
                await engine.ExecuteAsync(async (db, _) =>
                {
                    await GeneratedValues.CleanUpAsync(db, mirror, referenced, logger, clock.GetUtcNow());
                    return 0;
                }, cancellationToken);
            }
            // The images built templates need, and retention of those nothing needs any more. Its trouble is not the sync's.
            try
            {
                await engine.QueueImageBuildsAsync(cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Queueing image builds, or retention of images, failed.");
            }
            await PruneAsync(cancellationToken);
        }
        catch (GitException e)
        {
            await engine.ExecuteAsync(async (db, _) =>
            {
                var current = await db.RepositorySettings.SingleAsync(s => s.Id == 1, cancellationToken);
                if (current.SourceId == settings.SourceId && current.Url == settings.Url && current.Branch == settings.Branch)
                {
                    current.LastFetchAt = clock.GetUtcNow();
                    current.LastFetchError = e.Message;
                    await db.SaveChangesAsync(cancellationToken);
                }
                return false;
            }, cancellationToken);
            logger.LogWarning("Fetching the lab repository failed: {Error}", e.Message);
        }
        return poll;
    }

    /// <summary>
    /// Fetches the workflows repository's branches and tags and records the releases they show. A failed fetch is recorded
    /// and does not stop the sync: the releases already recorded still resolve, from the mirror.
    /// </summary>
    private async Task FetchWorkflowsAsync(VariableStore store, RepositorySettings settings, string url, StoreReference? key, CancellationToken cancellationToken)
    {
        var workflows = workflowMirrors.For(url);
        IReadOnlyList<TagTarget>? tags = null;
        string? error = null;
        try
        {
            tags = await workflows.FetchTagsAsync(url, await WorkflowsCredentialAsync(store, settings, url, key, cancellationToken), cancellationToken);
        }
        catch (GitException e)
        {
            error = e.Message;
            logger.LogWarning("Fetching the workflows repository {Repository} failed: {Error}", url, e.Message);
        }

        await engine.ExecuteAsync(async (db, _) =>
        {
            if (tags is not null)
                await WorkflowReleaseBook.RecordAsync(db, url, workflows, tags, clock.GetUtcNow(), cancellationToken);
            var current = await db.RepositorySettings.SingleAsync(s => s.Id == 1, cancellationToken);
            if (current.SourceId == settings.SourceId)
            {
                current.WorkflowsRepository = url;
                current.WorkflowsLastFetchAt = clock.GetUtcNow();
                current.WorkflowsLastFetchError = error;
            }
            await db.SaveChangesAsync(cancellationToken);
            return 0;
        }, cancellationToken);
    }

    private async Task<IReadOnlyList<WorkflowRelease>> ReleasesAsync(string repository, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await WorkflowReleaseBook.LoadAsync(scope.ServiceProvider.GetRequiredService<OctopushyDb>(), repository,
            workflowMirrors.For(repository), cancellationToken);
    }

    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
        // Scheduled observations use the latest valid revision, which may be older than the recent ones.
        var valid = await RepositoryTrust.EligibleRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).Take(1).ToListAsync(cancellationToken);
        var recent = await db.Revisions.AsNoTracking().OrderByDescending(r => r.Sequence).Take(10).ToListAsync(cancellationToken);
        var inUse = await db.Runs.AsNoTracking()
            .Where(r => r.Commit != null && r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled)
            .Select(r => new { Commit = r.Commit!, r.Context })
            .ToListAsync(cancellationToken);

        var commits = new HashSet<string>(StringComparer.Ordinal);
        var bundles = new HashSet<string>(StringComparer.Ordinal);
        var workflowCommits = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Keep(string commit, string? repository, string? workflowCommit)
        {
            commits.Add(commit);
            if (repository is null || workflowCommit is null)
            {
                bundles.Add(RepositoryMirror.BundleKey(commit, null));
                return;
            }

            bundles.Add(RepositoryMirror.BundleKey(commit, workflowCommit));
            if (!workflowCommits.TryGetValue(repository, out var kept))
                workflowCommits[repository] = kept = new HashSet<string>(StringComparer.Ordinal);
            kept.Add(workflowCommit);
        }

        foreach (var revision in valid.Concat(recent))
        {
            Keep(revision.Commit, null, null);
            foreach (var resolved in RevisionCache.Resolutions(revision.Resolutions).Where(r => r.Commit is not null))
                Keep(revision.Commit, revision.WorkflowsRepository, resolved.Commit);
        }

        foreach (var run in inUse)
        {
            var workflow = RunContext.Parse(run.Context).Workflow;
            Keep(run.Commit, workflow?.Repository, workflow?.Repository is null ? null : workflow.Commit);
        }

        // An image build waiting for an agent, or under way, takes its revision's files.
        foreach (var build in await db.ImageBuilds.AsNoTracking()
                     .Where(b => b.Status == ImageBuildStatuses.Queued || b.Status == ImageBuildStatuses.Building).ToListAsync(cancellationToken))
            Keep(build.RevisionCommit, build.WorkflowRepository, build.WorkflowRepository is null ? null : build.WorkflowCommit);

        mirror.Prune(commits, bundles);
        foreach (var (repository, kept) in workflowCommits)
            workflowMirrors.For(repository).Prune(kept);
    }

    private static async Task<GitCredential> CredentialAsync(VariableStore store, RepositorySettings settings, CancellationToken cancellationToken)
    {
        if (settings.CredentialKind == "none")
        {
            return GitCredential.None;
        }

        var secret = await store.GetAsync(CredentialKey, cancellationToken)
            ?? throw new GitException("The repository credential has not been set.");
        return new GitCredential(settings.CredentialKind, settings.CredentialUsername, secret.Value);
    }

    /// <summary>
    /// How to fetch the workflows repository: with the key <c>octopushy.yaml</c> names, as a token over HTTPS or a private
    /// key over SSH; otherwise with the lab repository's credential when both are reached the same way; otherwise with none.
    /// </summary>
    internal static async Task<GitCredential> WorkflowsCredentialAsync(VariableStore store, RepositorySettings settings, string url, StoreReference? key,
        CancellationToken cancellationToken)
    {
        var scheme = GitUrls.Scheme(url);
        // The address and the key are whatever the newest commit says, read before that commit is validated. So nothing goes
        // out in the clear, no key of the server's own is given, and the lab repository's token goes only to its own host.
        var cleartext = url.StartsWith("http://", StringComparison.Ordinal);
        if (key is not null)
        {
            var kind = scheme switch { GitUrls.Https => "token", GitUrls.Ssh => "ssh", _ => null };
            if (kind is null)
                return GitCredential.None;
            if (cleartext)
                throw new GitException("The workflows repository's credential is not sent over http://. Name it by https:// or SSH.");
            if (key.Key.StartsWith(LabRepositoryLoader.SystemScope, StringComparison.Ordinal))
                throw new GitException($"The workflows repository's credential, {key.Key}, is in the system scope, which definitions cannot use.");
            var secret = key.Version is { } version
                ? await store.GetVersionInternalAsync(key.Key, version, cancellationToken)
                : (await store.GetAsync(key.Key, cancellationToken))?.Value;
            return new GitCredential(kind, settings.CredentialKind == "token" ? settings.CredentialUsername : null,
                secret ?? throw new GitException($"The workflows repository's credential, {key.Key}, has not been set."));
        }

        return settings.Url is { } configUrl && scheme is GitUrls.Https or GitUrls.Ssh && GitUrls.Scheme(configUrl) == scheme
            // A token is handed to whoever asks for it; a key only signs, so another host learns nothing from it.
            && (scheme == GitUrls.Ssh || !cleartext && SameHost(configUrl, url))
            ? await CredentialAsync(store, settings, cancellationToken)
            : GitCredential.None;
    }

    /// <summary>Whether two https:// URLs name the same host and port.</summary>
    private static bool SameHost(string first, string second) =>
        Uri.TryCreate(first, UriKind.Absolute, out var a) && Uri.TryCreate(second, UriKind.Absolute, out var b)
        && a.Scheme == b.Scheme && a.Port == b.Port && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase);
}
