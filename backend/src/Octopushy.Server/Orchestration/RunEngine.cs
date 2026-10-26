using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Agent.V1;
using Octopushy.Definitions;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Options;

namespace Octopushy.Server.Orchestration;

public sealed class RunException(string message) : Exception(message);

/// <summary>Wakes the orchestrator loop when something changed.</summary>
public sealed class OrchestratorSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Wake()
    {
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _signal.WaitAsync(timeout, cancellationToken);
}

/// <summary>
/// Moves runs through their steps. Everything that changes runs, steps, or attempts goes
/// through this class, one change at a time, so the order of events is never ambiguous.
/// </summary>
public sealed partial class RunEngine(
    IServiceScopeFactory scopes,
    AgentConnections connections,
    AttemptLogStore logs,
    ArtifactStore artifacts,
    OrchestratorSignal signal,
    IOptions<OctopushyOptions> options,
    TimeProvider clock,
    ILogger<RunEngine> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>One pass of the loop: expire leases, advance runs, assign queued attempts.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        await WithDbAsync(async db =>
        {
            await ExpireLeasesAsync(db, cancellationToken);
            var active = await db.Runs
                .Where(r => r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed
                            && r.Status != RunStatuses.Cancelled && r.Status != RunStatuses.Uncertain)
                .OrderBy(r => r.CreatedAt)
                .Select(r => r.Id)
                .ToListAsync(cancellationToken);
            foreach (var id in active)
            {
                await AdvanceAsync(db, id, cancellationToken);
            }
        }, cancellationToken);
        await DispatchAsync(cancellationToken);
    }

    /// <summary>Runs work that changes runs, such as applying a revision, one change at a time with the engine.</summary>
    public Task<T> ExecuteAsync<T>(Func<OctopushyDb, IServiceProvider, Task<T>> action, CancellationToken cancellationToken) =>
        WithDbAsync(action, cancellationToken);

    /// <summary>Adds runs built elsewhere and starts them.</summary>
    public async Task AddRunsAsync(OctopushyDb db, IEnumerable<Run> runs, CancellationToken cancellationToken)
    {
        foreach (var run in runs)
        {
            if (await RepositoryTrust.BlockedReasonAsync(db, run, cancellationToken) is { } reason)
            {
                run.Status = RunStatuses.Blocked;
                run.Detail = reason;
            }
            db.Runs.Add(run);
        }
        await db.SaveChangesAsync(cancellationToken);
        signal.Wake();
    }

    public Task ApproveAsync(Guid runId, Guid stepId, string planSha256, string user, string? note, CancellationToken cancellationToken) =>
        WithDbAsync(async db =>
        {
            var step = await AwaitingApprovalAsync(db, runId, stepId, cancellationToken);
            if (!string.Equals(step.PlanSha256, planSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new RunException("The plan changed since it was shown; review the current plan.");
            }

            step.Decision = "approved";
            step.DecidedBy = user;
            step.DecidedAt = clock.GetUtcNow();
            step.DecisionNote = note;
            step.Status = StepStatuses.Succeeded;
            step.FinishedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            await AdvanceAsync(db, runId, cancellationToken);
        }, cancellationToken);

    public Task RejectAsync(Guid runId, Guid stepId, string user, string? note, CancellationToken cancellationToken) =>
        WithDbAsync(async db =>
        {
            var step = await AwaitingApprovalAsync(db, runId, stepId, cancellationToken);
            step.Decision = "rejected";
            step.DecidedBy = user;
            step.DecidedAt = clock.GetUtcNow();
            step.DecisionNote = note;
            step.Status = StepStatuses.Cancelled;
            step.Detail = "Rejected.";
            step.FinishedAt = clock.GetUtcNow();
            var run = await LoadRunAsync(db, runId, cancellationToken);
            Finish(run, RunStatuses.Cancelled, $"Rejected by {user}.");
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>Cancels a run that has not started changing anything; an apply in progress is never interrupted.</summary>
    public Task CancelAsync(Guid runId, string reason, CancellationToken cancellationToken) =>
        WithDbAsync(db => CancelInternalAsync(db, runId, reason, cancellationToken), cancellationToken);

    /// <summary>Retry only a known failed guest step, preserving the approved infrastructure work.</summary>
    public Task RetryFilesystemAsync(Guid runId, CancellationToken cancellationToken) =>
        WithDbAsync(async db =>
        {
            var run = await LoadRunAsync(db, runId, cancellationToken);
            var failed = run.Steps.OrderBy(s => s.Ordinal).FirstOrDefault(s => s.Status == StepStatuses.Failed);
            if (run.Status != RunStatuses.Failed || failed?.Name is not (StepNames.GrowRoot or StepNames.VerifyRoot)
                // Only a mutating attempt can leave an unknown change behind; a read-only one that lost its agent changed nothing.
                || run.Steps.SelectMany(s => s.Attempts).Any(a => a.Status == AttemptStatuses.Uncertain && a.Mutating))
            {
                throw new RunException("Only a known failed guest filesystem step can be retried. Inspect uncertain outcomes and plan again.");
            }

            if (await RepositoryTrust.BlockedReasonAsync(db, run, cancellationToken) is { } trustReason)
                throw new RunException(trustReason);

            var resource = await db.Resources.FirstAsync(r => r.Id == run.ResourceId, cancellationToken);
            if (!resource.Valid || resource.MissingSince is not null || resource.RetiredAt is not null
                || resource.DesiredInputsHash != run.InputsHash
                || await db.Runs.AnyAsync(r => r.ResourceId == run.ResourceId && r.Id != run.Id && r.CreatedAt > run.CreatedAt, cancellationToken))
            {
                throw new RunException("This run is no longer current. Plan the current desired configuration instead.");
            }

            foreach (var step in run.Steps.Where(s => s.Ordinal >= failed.Ordinal))
            {
                step.Status = StepStatuses.Pending;
                step.Detail = null;
                step.FinishedAt = null;
                step.StartedAt = null;
                step.Outputs = null;
            }
            run.Status = RunStatuses.Running;
            run.FinishedAt = null;
            run.Detail = null;
            await db.SaveChangesAsync(cancellationToken);
            signal.Wake();
        }, cancellationToken);

    /// <summary>Closes a run whose outcome is uncertain, after the user has looked at it.</summary>
    public Task CloseUncertainAsync(Guid runId, string user, CancellationToken cancellationToken) =>
        WithDbAsync(async db =>
        {
            var run = await LoadRunAsync(db, runId, cancellationToken);
            if (run.Status != RunStatuses.Uncertain)
            {
                throw new RunException("Only a run with an uncertain outcome can be closed.");
            }

            foreach (var step in run.Steps.Where(s => s.Status == StepStatuses.Uncertain))
            {
                step.Status = StepStatuses.Failed;
                step.Detail = (step.Detail is null ? "" : step.Detail + " ") + $"Closed by {user}.";
            }

            Finish(run, RunStatuses.Failed, $"Closed by {user} after an uncertain outcome. Plan again to see the current state.");
            await db.SaveChangesAsync(cancellationToken);
            signal.Wake();
        }, cancellationToken);

    /// <summary>Whether a run can still be replaced by a newer one: nothing has started changing infrastructure.</summary>
    public static bool CanSupersede(Run run) =>
        !RunStatuses.IsTerminal(run.Status)
        && run.Status != RunStatuses.Uncertain
        && !run.Steps.SelectMany(s => s.Attempts).Any(a => a.Mutating);

    internal async Task CancelInternalAsync(OctopushyDb db, Guid runId, string reason, CancellationToken cancellationToken)
    {
        var run = await LoadRunAsync(db, runId, cancellationToken);
        if (RunStatuses.IsTerminal(run.Status))
        {
            throw new RunException("The run has already finished.");
        }

        var attempts = run.Steps.SelectMany(s => s.Attempts).ToList();
        if (attempts.Any(a => a.Mutating && AttemptStatuses.IsActive(a.Status)))
        {
            throw new RunException("A change is being applied; it cannot be interrupted. Wait for it to finish.");
        }

        if (run.Status == RunStatuses.Uncertain || attempts.Any(a => a.Mutating && a.Status == AttemptStatuses.Uncertain))
        {
            throw new RunException("The run's outcome is uncertain; close it instead.");
        }

        foreach (var attempt in attempts.Where(a => !AttemptStatuses.IsTerminal(a.Status)))
        {
            if (AttemptStatuses.IsActive(attempt.Status) && attempt.AgentId is { } agentId && connections.Get(agentId) is { } session)
            {
                await session.SendAsync(new ServerMessage { Cancel = new CancelAttempt { AttemptId = attempt.Id.ToString() } }, cancellationToken);
            }

            attempt.Status = AttemptStatuses.Cancelled;
            attempt.FinishedAt = clock.GetUtcNow();
            attempt.LeaseExpiresAt = null;
        }

        Finish(run, RunStatuses.Cancelled, reason);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task AdvanceAsync(OctopushyDb db, Guid runId, CancellationToken cancellationToken)
    {
        var run = await LoadRunAsync(db, runId, cancellationToken);
        if (RunStatuses.IsTerminal(run.Status) || run.Status == RunStatuses.Uncertain)
        {
            return;
        }

        // Dispatched work may already have changed infrastructure. Keep accepting its
        // evidence; only prevent the next advancement/release, never cancel or replay it.
        var inFlight = run.Steps.SelectMany(s => s.Attempts)
            .Any(a => a.Status is AttemptStatuses.Assigned or AttemptStatuses.Running);
        var next = run.Steps.OrderBy(s => s.Ordinal).FirstOrDefault(s => !StepStatuses.IsDone(s.Status));
        // Outcome reconciliation is not a new operation. In particular, never hide an
        // uncertain mutation behind a trust wait: it must retain its serialization lock
        // and the explicit close workflow. Known failures/completion also settle normally.
        var reconcilingOutcome = next is null || next.Status is StepStatuses.Uncertain or StepStatuses.Failed or StepStatuses.Cancelled;
        if (!inFlight && !reconcilingOutcome && await RepositoryTrust.BlockedReasonAsync(db, run, cancellationToken) is { } trustReason)
        {
            run.Status = RunStatuses.Blocked;
            run.Detail = trustReason;
            foreach (var attempt in run.Steps.SelectMany(s => s.Attempts).Where(a => a.Status == AttemptStatuses.Queued))
                attempt.WaitingReason = trustReason;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (run.Status == RunStatuses.Queued || run.Status == RunStatuses.Blocked && run.StartedAt is null)
        {
            var eligible = RepositoryTrust.EligibleRevisions(db);
            var earlier = await db.Runs.AsNoTracking()
                .Where(r => r.ResourceId == run.ResourceId && r.Id != run.Id && r.CreatedAt < run.CreatedAt
                            && r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled
                            && (eligible.Any(v => v.Id == r.RevisionId && v.Commit == r.Commit)
                                || db.Attempts.Any(a => a.RunId == r.Id && (a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running || a.Mutating && a.Status == AttemptStatuses.Uncertain))))
                .OrderBy(r => r.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (earlier is not null)
            {
                SetDetail(run, $"Waiting for run #{earlier.Number} on the same resource.");
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            run.Status = RunStatuses.Running;
            run.StartedAt = clock.GetUtcNow();
            run.Detail = null;
        }

        var context = RunContext.Parse(run.Context);
        foreach (var step in run.Steps.OrderBy(s => s.Ordinal))
        {
            if (StepStatuses.IsDone(step.Status))
            {
                continue;
            }

            switch (step.Status)
            {
                case StepStatuses.Pending or StepStatuses.Blocked when step.Kind == StepKinds.Approval:
                    if (DecideApproval(run, step, context))
                    {
                        continue;
                    }

                    run.Status = RunStatuses.AwaitingApproval;
                    run.Detail = step.Detail;
                    break;
                case StepStatuses.Pending or StepStatuses.Blocked:
                    var missing = await CredentialBindings.CheckAsync(db, run, context, cancellationToken, AssignmentBuilder.SecretsFor(step.Name, context));
                    if (missing.Count > 0)
                    {
                        step.Status = StepStatuses.Blocked;
                        step.Detail = $"Blocked credentials: {string.Join(", ", missing)}. Review Variables.";
                        run.Status = RunStatuses.Blocked;
                        run.Detail = step.Detail;
                        break;
                    }

                    CreateAttempt(db, run, step, context);
                    step.Status = StepStatuses.Queued;
                    step.Detail = null;
                    run.Status = RunStatuses.Running;
                    run.Detail = null;
                    signal.Wake();
                    break;
                case StepStatuses.Queued:
                    var unavailable = await CredentialBindings.CheckAsync(db, run, context, cancellationToken, AssignmentBuilder.SecretsFor(step.Name, context));
                    run.Status = unavailable.Count == 0 ? RunStatuses.Running : RunStatuses.Blocked;
                    run.Detail = unavailable.Count == 0 ? null : $"Blocked credentials: {string.Join(", ", unavailable)}.";
                    break;
                case StepStatuses.Running:
                    run.Status = RunStatuses.Running;
                    break;
                case StepStatuses.AwaitingApproval:
                    run.Status = RunStatuses.AwaitingApproval;
                    run.Detail = step.Detail;
                    break;
                case StepStatuses.Failed:
                    Finish(run, RunStatuses.Failed, $"The {step.Name} step failed: {step.Detail}");
                    break;
                case StepStatuses.Uncertain:
                    run.Status = RunStatuses.Uncertain;
                    run.Detail = step.Detail;
                    break;
                case StepStatuses.Cancelled:
                    Finish(run, RunStatuses.Cancelled, step.Detail ?? "Cancelled.");
                    break;
            }

            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var noChanges = run.Steps.Any(s => s.Name == StepNames.Apply && s.Status == StepStatuses.Skipped);
        Finish(run, RunStatuses.Succeeded, noChanges && run.Operation != Operations.Observe
            ? context.DiskReporter is null ? "The plan has no changes." : "Infrastructure unchanged; disk reporter setup completed."
            : null);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Applies the policy to the plan; returns true when the approval step is settled.</summary>
    private bool DecideApproval(Run run, Step step, RunContext context)
    {
        var plan = run.Steps.Single(s => s.Name == StepNames.Plan);
        var outputs = plan.Outputs is null ? null : JsonNode.Parse(plan.Outputs);
        var deletes = outputs?["counts"]?["delete"]?.GetValue<int>() ?? 0;
        // A plan can report changes without changing any resource, for example only outputs,
        // or a destroy plan over a state that holds nothing. There is nothing to approve then.
        var resourceChanges = (outputs?["changes"] as JsonArray)?.Count ?? 0;
        var policy = context.Policy!;
        var manual = policy.Approval == "manual" && (resourceChanges > 0 || context.DiskReporter is not null);
        var destructiveManual = deletes > 0 && policy.Destructive == "manual";
        step.ApprovalRequired = manual || destructiveManual;
        step.PlanSha256 = outputs?["planSha256"]?.GetValue<string>();
        if (step.PlanSha256 is null && context.DiskReporter is { } reporter)
        {
            // No infrastructure plan file needs applying. Bind approval to the
            // immutable setup specification instead, including the pinned binary.
            step.PlanSha256 = Canonical.Hash(JsonSerializer.SerializeToNode(reporter, RunContext.Json)!);
        }
        if (!step.ApprovalRequired)
        {
            step.Decision = "approved";
            step.DecidedBy = "policy";
            step.DecidedAt = clock.GetUtcNow();
            step.DecisionNote = resourceChanges == 0 && context.DiskReporter is null
                ? "The plan changes no resources, so it needs no approval."
                : deletes > 0
                    ? $"Approval and destructive are automatic (approval set by {policy.ApprovalSetBy}, destructive {policy.DestructiveSetBy})."
                    : $"Approval is automatic (set by {policy.ApprovalSetBy}).";
            step.Status = StepStatuses.Succeeded;
            step.StartedAt = step.FinishedAt = clock.GetUtcNow();
            return true;
        }

        step.Status = StepStatuses.AwaitingApproval;
        step.StartedAt ??= clock.GetUtcNow();
        step.Detail = destructiveManual && !manual
            ? $"The plan deletes {deletes} resource(s) and destructive is manual (set by {policy.DestructiveSetBy})."
            : $"Approval is manual (set by {policy.ApprovalSetBy}).";
        if (context.DiskReporter is not null)
        {
            step.Detail += " This run also installs the disk reporter inside the VM after verification.";
        }
        return false;
    }

    private void CreateAttempt(OctopushyDb db, Run run, Step step, RunContext context, Guid? preferredAgent = null)
    {
        var action = AssignmentBuilder.ActionFor(step.Name);
        if (preferredAgent is null && step.Name == StepNames.Apply)
        {
            // Keep the apply where the plan was prepared when that agent is available.
            preferredAgent = run.Steps.Single(s => s.Name == StepNames.Plan).Attempts
                .Where(a => a.Status == AttemptStatuses.Succeeded)
                .MaxBy(a => a.Number)?.AgentId;
        }

        var attempt = new Attempt
        {
            Id = Guid.CreateVersion7(),
            StepId = step.Id,
            RunId = run.Id,
            Number = step.Attempts.Count + 1,
            Executor = action.Executor,
            Action = action.Action,
            Mutating = action.Mutating,
            RequiredLabels = context.AgentLabels.ToArray(),
            HostKey = context.HostKey,
            HostLimit = context.HostLimit,
            PreferredAgentId = preferredAgent,
            CreatedAt = clock.GetUtcNow(),
        };
        step.Attempts.Add(attempt);
        db.Attempts.Add(attempt);
    }

    /// <summary>Records what a finished step established about its resource.</summary>
    private async Task ApplyEffectsAsync(OctopushyDb db, Run run, Step step, JsonNode? outputs, CancellationToken cancellationToken)
    {
        var resource = await db.Resources.FirstAsync(r => r.Id == run.ResourceId, cancellationToken);
        var now = clock.GetUtcNow();
        switch (step.Name)
        {
            case StepNames.Plan:
                resource.CheckedInputsHash = run.InputsHash;
                resource.CheckedRevisionId = run.RevisionId;
                resource.CheckedAt = now;
                if (outputs?["hasChanges"]?.GetValue<bool>() == false)
                {
                    if (run.Operation == Operations.Provision && RunContext.Parse(run.Context).RootDiskBytes is null)
                    {
                        resource.SyncedInputsHash = run.InputsHash;
                    }

                    var reporterSetup = RunContext.Parse(run.Context).DiskReporter is not null;
                    foreach (var later in run.Steps.Where(s =>
                        s.Name is StepNames.Unprotect or StepNames.Apply || s.Name == StepNames.Approval && !reporterSetup))
                    {
                        later.Status = StepStatuses.Skipped;
                        later.Detail = "The plan has no changes.";
                    }
                }

                break;
            case StepNames.Apply when run.Operation == Operations.Provision:
                var context = RunContext.Parse(run.Context);
                resource.AppliedInputsHash = run.InputsHash;
                resource.AppliedRevisionId = run.RevisionId;
                resource.AppliedAt = now;
                if (context.RootDiskBytes is null)
                    resource.SyncedInputsHash = run.InputsHash;
                resource.Outputs = outputs?["outputs"]?.ToJsonString();
                if (resource.BoundVmid is null)
                {
                    resource.BoundInstallation = resource.ParentAddress;
                    resource.BoundVmid = context.Proxmox.Vmid;
                    resource.BoundAt = now;
                }

                break;
            case StepNames.Apply:
                resource.Outputs = null;
                break;
            case StepNames.Verify:
                resource.VerifiedAt = now;
                break;
            case StepNames.VerifyRoot:
                resource.SyncedInputsHash = run.InputsHash;
                resource.VerifiedAt = now;
                break;
            case StepNames.VerifyAbsent:
                resource.RetiredAt = now;
                resource.VerifiedAt = now;
                resource.BoundVmid = null;
                resource.BoundInstallation = null;
                break;
            case StepNames.Observe:
                db.Observations.Add(new Observation
                {
                    Id = Guid.CreateVersion7(),
                    ResourceId = resource.Id,
                    AttemptId = step.Attempts.MaxBy(a => a.Number)?.Id,
                    ObservedAt = now,
                    Data = outputs?.ToJsonString() ?? "{}",
                });
                var old = await db.Observations
                    .Where(o => o.ResourceId == resource.Id)
                    .OrderByDescending(o => o.ObservedAt)
                    .Skip(50)
                    .Select(o => o.Id)
                    .ToListAsync(cancellationToken);
                await db.Observations.Where(o => old.Contains(o.Id)).ExecuteDeleteAsync(cancellationToken);
                break;
        }
    }

    private void Finish(Run run, string status, string? detail)
    {
        run.Status = status;
        run.Detail = detail;
        run.FinishedAt = clock.GetUtcNow();
        foreach (var step in run.Steps.Where(s => !StepStatuses.IsDone(s.Status) && s.Status is not (StepStatuses.Failed or StepStatuses.Uncertain)))
        {
            step.Status = status == RunStatuses.Cancelled && step.Status != StepStatuses.Pending ? StepStatuses.Cancelled : StepStatuses.Skipped;
            step.Detail ??= status == RunStatuses.Cancelled ? "The run was cancelled." : "An earlier step did not succeed.";
        }
    }

    private static void SetDetail(Run run, string detail)
    {
        if (run.Detail != detail)
        {
            run.Detail = detail;
        }
    }

    private static async Task<Step> AwaitingApprovalAsync(OctopushyDb db, Guid runId, Guid stepId, CancellationToken cancellationToken)
    {
        var step = await db.Steps.FirstOrDefaultAsync(s => s.Id == stepId && s.RunId == runId, cancellationToken)
            ?? throw new RunException("There is no such step.");
        if (step.Status != StepStatuses.AwaitingApproval)
        {
            throw new RunException("The step is not waiting for approval.");
        }

        return step;
    }

    private static async Task<Run> LoadRunAsync(OctopushyDb db, Guid runId, CancellationToken cancellationToken) =>
        await db.Runs.Include(r => r.Steps).ThenInclude(s => s.Attempts).AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken)
        ?? throw new RunException("There is no such run.");

    private async Task WithDbAsync(Func<OctopushyDb, Task> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await action(scope.ServiceProvider.GetRequiredService<OctopushyDb>());
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> WithDbAsync<T>(Func<OctopushyDb, IServiceProvider, Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<OctopushyDb>(), scope.ServiceProvider);
        }
        finally
        {
            _gate.Release();
        }
    }
}
