using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Options;

namespace Octopushy.Server.Orchestration;

/// <summary>Deletes finished history daily, then sweeps files left behind by interrupted deletions.</summary>
public sealed class RunHistoryRetention(
    RunEngine engine,
    AttemptLogStore logs,
    ArtifactStore artifacts,
    IOptions<OctopushyOptions> options,
    TimeProvider clock,
    ILogger<RunHistoryRetention> logger) : BackgroundService
{
    public async Task PruneAsync(CancellationToken cancellationToken)
    {
        if (options.Value.RunRetentionDays < 1 || options.Value.RunRetentionKeep < 0)
            throw new InvalidOperationException("RunRetentionDays must be positive and RunRetentionKeep must not be negative; no history was deleted.");
        var cutoff = clock.GetUtcNow().AddDays(-options.Value.RunRetentionDays);
        var keep = options.Value.RunRetentionKeep;
        var removed = await engine.ExecuteAsync(async (db, _) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var ids = await db.Runs.Where(r =>
                    r.CreatedAt < cutoff && r.FinishedAt < cutoff &&
                    (r.Status == RunStatuses.Succeeded || r.Status == RunStatuses.Failed || r.Status == RunStatuses.Cancelled) &&
                    db.Runs.Where(newer => newer.ResourceId == r.ResourceId && newer.FinishedAt != null &&
                        (newer.CreatedAt > r.CreatedAt || newer.CreatedAt == r.CreatedAt && newer.Number > r.Number))
                        .Take(keep).Count() >= keep &&
                    !db.Changes.Any(c => c.RunIds.Contains(r.Id) || c.PreApprovalRunId == r.Id) &&
                    // Still read: configuration reads the latest provision, a guest's services the latest one that changed
                    // something (GuestWork.WaitingForAsync), and a sync proposes the desired work again unless a run stands for
                    // its occurrence. A provision goes only once a newer one has finished and changed something: one that
                    // is still waiting may yet be rejected, and the older one is what readiness falls back to.
                    (r.Operation != Operations.Provision || db.Runs.Any(newer => newer.ResourceId == r.ResourceId &&
                        newer.Operation == Operations.Provision && newer.CreatedAt > r.CreatedAt &&
                        (newer.Status == RunStatuses.Succeeded ||
                         (newer.Status == RunStatuses.Failed || newer.Status == RunStatuses.Cancelled) &&
                         db.Attempts.Any(a => a.RunId == newer.Id && a.Mutating && a.Status != AttemptStatuses.Queued &&
                             a.Status != AttemptStatuses.Cancelled)))) &&
                    !db.Resources.Any(res => res.Id == r.ResourceId && res.DesiredOccurrence != null && res.DesiredOccurrence == r.Occurrence) &&
                    !db.GuestSchedules.Any(s => s.LastRunId == r.Id) &&
                    !db.Steps.Any(s => s.RunId == r.Id && s.Kind == StepKinds.Approval &&
                        (s.Status == StepStatuses.Pending || s.Status == StepStatuses.Queued ||
                         s.Status == StepStatuses.AwaitingApproval || s.Status == StepStatuses.Blocked)))
                .Select(r => r.Id).ToArrayAsync(cancellationToken);
            var attempts = await db.Attempts.Where(a => a.RunId != null && ids.Contains(a.RunId.Value) ||
                    a.Step != null && ids.Contains(a.Step.RunId))
                .Select(a => a.Id).ToArrayAsync(cancellationToken);
            await db.Runs.Where(r => ids.Contains(r.Id)).ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (Runs: ids.Length, Attempts: attempts);
        }, cancellationToken);

        foreach (var id in removed.Attempts)
            await DeleteFilesAsync(id, cancellationToken);

        // Rows commit first. If the process died before deleting files, finish that work now.
        var stored = logs.StoredAttempts().Concat(artifacts.StoredAttempts()).Distinct().ToArray();
        var orphaned = await engine.ExecuteAsync(async (db, _) =>
        {
            var existing = await db.Attempts.Where(a => stored.Contains(a.Id)).Select(a => a.Id).ToArrayAsync(cancellationToken);
            return stored.Except(existing).ToArray();
        }, cancellationToken);
        foreach (var id in orphaned)
            await DeleteFilesAsync(id, cancellationToken);

        logger.LogInformation("Run history retention deleted {Runs} runs, files for {Attempts} attempts and {Orphans} orphaned file sets.",
            removed.Runs, removed.Attempts.Length, orphaned.Length);
    }

    private async Task DeleteFilesAsync(Guid id, CancellationToken cancellationToken)
    {
        await logs.DeleteAsync(id, cancellationToken);
        artifacts.Delete(id);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1), clock);
        do
        {
            try
            {
                await PruneAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogError(e, "Run history retention failed; the next pass will retry.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
