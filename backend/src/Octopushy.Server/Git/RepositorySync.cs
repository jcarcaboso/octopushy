using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;

namespace Octopushy.Server.Git;

/// <summary>
/// Polls the lab repository's branch and, for each new commit, reads its definitions and applies
/// them. Polling works with any Git server; a sync can also be requested at any time.
/// </summary>
public sealed class RepositorySync(
    IServiceScopeFactory scopes,
    RepositoryMirror mirror,
    RevisionCache revisions,
    RunEngine engine,
    TimeProvider clock,
    ILogger<RepositorySync> logger) : BackgroundService
{
    public const string CredentialKey = "system/git/credential";

    private readonly SemaphoreSlim _trigger = new(0, 1);

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var poll = TimeSpan.FromSeconds(60);
            try
            {
                poll = await SyncOnceAsync(stoppingToken);
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

    /// <summary>Fetches once and loads the head commit if it is new; returns the poll interval.</summary>
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
            var credential = await CredentialAsync(scope.ServiceProvider.GetRequiredService<VariableStore>(), settings, cancellationToken);
            var head = await mirror.FetchAsync(settings.Url, settings.Branch, credential, cancellationToken);
            var info = await mirror.CommitInfoAsync(head, cancellationToken);
            var repository = await revisions.GetAsync(head, cancellationToken);
            await engine.ExecuteAsync(async (db, services) =>
            {
                var current = await db.RepositorySettings.SingleAsync(s => s.Id == 1, cancellationToken);
                if (current.SourceId != settings.SourceId || current.Url != settings.Url || current.Branch != settings.Branch)
                    return false;
                var alreadyLoadedHead = current.HeadCommit == head;
                current.LastFetchAt = clock.GetUtcNow();
                current.LastFetchError = null;
                current.HeadCommit = head;
                // A return to an already loaded exact commit of this source reuses its authority.
                var loaded = await RepositoryTrust.CurrentRevisions(db).AnyAsync(r => r.Commit == head, cancellationToken);
                if (alreadyLoadedHead && loaded)
                {
                    await db.SaveChangesAsync(cancellationToken);
                    return false;
                }
                var revision = loaded
                    ? await RepositoryTrust.CurrentRevisions(db).Where(r => r.Commit == head).OrderByDescending(r => r.Sequence).FirstAsync(cancellationToken)
                    : new DefinitionRevision
                    {
                        Id = Guid.CreateVersion7(),
                        SourceId = settings.SourceId,
                        Commit = head,
                        Subject = info.Subject,
                        Author = info.Author,
                        CommittedAt = info.CommittedAt,
                        LoadedAt = clock.GetUtcNow(),
                        RepositoryValid = repository.RepositoryValid,
                        ErrorCount = repository.Issues.Count(i => i.Severity == IssueSeverity.Error),
                        WarningCount = repository.Issues.Count(i => i.Severity == IssueSeverity.Warning),
                        Issues = JsonSerializer.Serialize(repository.Issues, InventoryUpdater.IssueJson),
                    };
                if (!loaded) db.Revisions.Add(revision);
                await db.SaveChangesAsync(cancellationToken);
                await services.GetRequiredService<InventoryUpdater>().ApplyAsync(db, revision, repository, cancellationToken);
                return true;
            }, cancellationToken);
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

    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
        var trusted = await db.Revisions.AsNoTracking().Where(r => r.TrustedAt != null).Select(r => r.Commit).ToListAsync(cancellationToken);
        var recent = await db.Revisions.AsNoTracking().OrderByDescending(r => r.Sequence).Take(10).Select(r => r.Commit).ToListAsync(cancellationToken);
        var inUse = await db.Runs.AsNoTracking()
            .Where(r => r.Commit != null && r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled)
            .Select(r => r.Commit!)
            .ToListAsync(cancellationToken);
        mirror.Prune(recent.Concat(inUse).Concat(trusted).ToHashSet(StringComparer.Ordinal));
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
}
