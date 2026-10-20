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
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
        var settings = await db.RepositorySettings.Where(r => r.Id == 1).FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = new RepositorySettings { UpdatedAt = clock.GetUtcNow() };
            db.RepositorySettings.Add(settings);
            await db.SaveChangesAsync(cancellationToken);
        }

        var poll = TimeSpan.FromSeconds(Math.Max(10, settings.PollSeconds));
        if (settings.Url is null)
        {
            return poll;
        }

        string head;
        try
        {
            var credential = await CredentialAsync(scope.ServiceProvider.GetRequiredService<VariableStore>(), settings, cancellationToken);
            head = await mirror.FetchAsync(settings.Url, settings.Branch, credential, cancellationToken);
            settings.LastFetchError = null;
            settings.HeadCommit = head;
        }
        catch (GitException e)
        {
            settings.LastFetchError = e.Message;
            logger.LogWarning("Fetching the lab repository failed: {Error}", e.Message);
            return poll;
        }
        finally
        {
            settings.LastFetchAt = clock.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }

        var latest = await db.Revisions.AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
        if (latest?.Commit != head)
        {
            await LoadRevisionAsync(head, cancellationToken);
        }

        return poll;
    }

    private async Task LoadRevisionAsync(string commit, CancellationToken cancellationToken)
    {
        var info = await mirror.CommitInfoAsync(commit, cancellationToken);
        var repository = await revisions.GetAsync(commit, cancellationToken);
        var revision = new DefinitionRevision
        {
            Id = Guid.CreateVersion7(),
            Commit = commit,
            Subject = info.Subject,
            Author = info.Author,
            CommittedAt = info.CommittedAt,
            LoadedAt = clock.GetUtcNow(),
            RepositoryValid = repository.RepositoryValid,
            ErrorCount = repository.Issues.Count(i => i.Severity == IssueSeverity.Error),
            WarningCount = repository.Issues.Count(i => i.Severity == IssueSeverity.Warning),
            Issues = JsonSerializer.Serialize(repository.Issues, InventoryUpdater.IssueJson),
        };

        await engine.ExecuteAsync(async (db, services) =>
        {
            db.Revisions.Add(revision);
            await db.SaveChangesAsync(cancellationToken);
            await services.GetRequiredService<InventoryUpdater>().ApplyAsync(db, revision, repository, cancellationToken);
            return true;
        }, cancellationToken);
        logger.LogInformation("Loaded revision {Commit}: {Errors} errors, {Warnings} warnings.", commit[..10], revision.ErrorCount, revision.WarningCount);
        await PruneAsync(cancellationToken);
    }

    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
        var recent = await db.Revisions.AsNoTracking().OrderByDescending(r => r.Sequence).Take(10).Select(r => r.Commit).ToListAsync(cancellationToken);
        var inUse = await db.Runs.AsNoTracking()
            .Where(r => r.Commit != null && r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled)
            .Select(r => r.Commit!)
            .ToListAsync(cancellationToken);
        mirror.Prune(recent.Concat(inUse).ToHashSet(StringComparer.Ordinal));
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
