using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Octopushy.Server.Data;

namespace Octopushy.Server.Orchestration;

/// <summary>Logs status changes, never the tool's outputs, diagnostic text or secret values.</summary>
public sealed class RunTransitionLog(ILogger<RunTransitionLog> logger) : SaveChangesInterceptor
{
    private readonly List<Transition> _pending = [];

    private void Capture(DbContext? db)
    {
        _pending.Clear();
        if (db is null)
            return;
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity is Run or Step or Attempt))
        {
            if (entry.State != EntityState.Added &&
                (entry.State != EntityState.Modified || !entry.Property("Status").IsModified))
                continue;
            var status = (string)entry.Property("Status").CurrentValue!;
            var old = entry.State == EntityState.Added ? "new" : (string)entry.Property("Status").OriginalValue!;
            if (old == status)
                continue;
            var transition = entry.Entity switch
            {
                Run r => new Transition("run", r.Id, r.Id, null, null, old, status,
                    entry.State == EntityState.Added ? r.Reason : Reason(status)),
                Step s => new Transition("step", s.Id, s.RunId, s.Id, null, old, status, Reason(status)),
                Attempt a => new Transition("attempt", a.Id, a.RunId, a.StepId, a.Id, old, status,
                    a.LateResult ? "late agent result" : Reason(status)),
                _ => throw new InvalidOperationException(),
            };
            _pending.Add(transition);
        }
    }

    private void Write()
    {
        foreach (var t in _pending)
            logger.LogInformation(
                "{Entity} {EntityId} transitioned from {OldStatus} to {NewStatus}; run {RunId}, step {StepId}, attempt {AttemptId}, reason {Reason}.",
                t.Entity, t.Id, t.Old, t.New, t.RunId, t.StepId, t.AttemptId, t.Reason);
        _pending.Clear();
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Write();
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Write();
        return ValueTask.FromResult(result);
    }

    private static string Reason(string status) => status switch
    {
        RunStatuses.Succeeded => "completed",
        RunStatuses.Failed => "execution failed",
        RunStatuses.Uncertain => "outcome not confirmed",
        RunStatuses.Cancelled => "cancelled",
        RunStatuses.AwaitingApproval => "approval required",
        AttemptStatuses.Assigned => "dispatched",
        RunStatuses.Running => "started",
        RunStatuses.Blocked => "prerequisites unavailable",
        StepStatuses.Skipped => "not required",
        _ => "ready",
    };

    private sealed record Transition(string Entity, Guid Id, Guid? RunId, Guid? StepId, Guid? AttemptId, string Old, string New, string Reason);
}
