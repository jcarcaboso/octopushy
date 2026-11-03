using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Store;
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
    RevisionCache revisions,
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
        var observedOnly = await SafetyBoundary.ObservedOnlyInstallationsAsync(db, cancellationToken);
        foreach (var run in runs)
        {
            // Refused, not held: a run in an installation Octopushy only observes never waits to go.
            if (await SafetyBoundary.ObservedOnlyRefusalAsync(db, run, RunContext.Parse(run.Context), cancellationToken, observedOnly) is { } refused)
                Finish(run, RunStatuses.Failed, refused);
            else if (await RepositoryTrust.BlockedReasonAsync(db, run, cancellationToken) is { } reason)
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
            if (step.Name == StepNames.PromoteKey)
                await PromoteRotatedKeyAsync(db, runId, user, note, cancellationToken);
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

            var context = RunContext.Parse(run.Context);
            if (!RestoringProtection(db, run, context))
                Finish(run, RunStatuses.Failed, $"Closed by {user} after an uncertain outcome. Plan again to see the current state." + ProtectionOutcome(run, context));
            await db.SaveChangesAsync(cancellationToken);
            signal.Wake();
        }, cancellationToken);

    /// <summary>Whether a run can still be replaced by a newer one: nothing has started changing infrastructure.</summary>
    public static bool CanSupersede(Run run) =>
        !RunStatuses.IsTerminal(run.Status)
        && run.Status != RunStatuses.Uncertain
        && !run.Steps.SelectMany(s => s.Attempts).Any(a => a.Mutating && !NeverSent(run, a));

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

        // A run that lifted protection and will not apply must not leave the VM unprotected.
        var context = RunContext.Parse(run.Context);
        if (run.Steps.All(s => s.Name != StepNames.Reprotect) && LiftedProtection(run))
        {
            var waiting = run.Steps.Single(s => s.Name == StepNames.Apply);
            waiting.Status = StepStatuses.Cancelled;
            waiting.Detail = reason;
            RestoringProtection(db, run, context);
        }
        else
            Finish(run, RunStatuses.Cancelled, reason + ProtectionOutcome(run, context));
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
        var context = RunContext.Parse(run.Context);
        // Asked before anything that would make it wait: such a run fails. See docs/safety-boundary.md.
        if (!inFlight && !reconcilingOutcome && await SafetyBoundary.ObservedOnlyRefusalAsync(db, run, context, cancellationToken) is { } observedOnly)
        {
            foreach (var attempt in run.Steps.SelectMany(s => s.Attempts).Where(a => a.Status == AttemptStatuses.Queued))
            {
                attempt.Status = AttemptStatuses.Cancelled;
                attempt.Error = observedOnly;
                attempt.FinishedAt = clock.GetUtcNow();
            }
            Finish(run, RunStatuses.Failed, observedOnly);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

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

        if (!inFlight && !reconcilingOutcome && CredentialBindings.Validate(context) is { Count: > 0 } invalid)
        {
            run.Status = RunStatuses.Blocked;
            run.Detail = $"Blocked credentials: {string.Join(", ", invalid)}.";
            foreach (var attempt in run.Steps.SelectMany(s => s.Attempts).Where(a => a.Status == AttemptStatuses.Queued))
                attempt.WaitingReason = run.Detail;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        foreach (var step in run.Steps.OrderBy(s => s.Ordinal))
        {
            if (StepStatuses.IsDone(step.Status))
            {
                continue;
            }

            switch (step.Status)
            {
                case StepStatuses.Pending or StepStatuses.Blocked when step.Kind == StepKinds.Approval:
                    if (context.Stages is not null ? DecidePipelineApproval(run, step, context)
                        : context.Ansible is not null ? DecideConfigureApproval(run, step, context)
                        : await DecideApprovalAsync(db, run, step, context, cancellationToken))
                    {
                        continue;
                    }

                    if (run.Status != RunStatuses.Failed)
                    {
                        run.Status = RunStatuses.AwaitingApproval;
                        run.Detail = step.Detail;
                    }
                    break;
                case StepStatuses.Pending or StepStatuses.Blocked:
                    var missing = await CredentialBindings.CheckAsync(db, run, context, cancellationToken, AssignmentBuilder.SecretsFor(step, context));
                    if (missing.Count > 0)
                    {
                        step.Status = StepStatuses.Blocked;
                        step.Detail = $"Blocked credentials: {string.Join(", ", missing)}. Review Variables.";
                        run.Status = RunStatuses.Blocked;
                        run.Detail = step.Detail;
                        break;
                    }

                    // One change to a guest at a time, its services included. What only looks, and what
                    // waits for a person, holds nobody back.
                    if (AssignmentBuilder.ActionFor(step.Name).Mutating && await ChangeHeldAsync(db, run, context, dispatching: false, cancellationToken) is { } held)
                    {
                        run.Status = RunStatuses.Running;
                        SetDetail(run, held);
                        break;
                    }

                    var createdAt = clock.GetUtcNow();
                    if (AssignmentBuilder.ActionFor(step.Name).Mutating && await StopIfLateAsync(db, run, createdAt, cancellationToken))
                        return;
                    CreateAttempt(db, run, step, context, createdAt: createdAt);
                    step.Status = StepStatuses.Queued;
                    step.Detail = null;
                    run.Status = RunStatuses.Running;
                    run.Detail = null;
                    signal.Wake();
                    break;
                case StepStatuses.Queued:
                    var unavailable = await CredentialBindings.CheckAsync(db, run, context, cancellationToken, AssignmentBuilder.SecretsFor(step, context));
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
                    if (step.Stage is not null)
                        Finish(run, RunStatuses.Failed, $"Stage {step.Stage} failed at {step.Name}: {step.Detail}");
                    else if (!RestoringProtection(db, run, context))
                        Finish(run, RunStatuses.Failed, $"The {step.Name} step failed: {step.Detail}" + ProtectionOutcome(run, context));
                    break;
                case StepStatuses.Uncertain:
                    run.Status = RunStatuses.Uncertain;
                    run.Detail = step.Detail;
                    break;
                case StepStatuses.Cancelled:
                    if (!RestoringProtection(db, run, context))
                        Finish(run, RunStatuses.Cancelled, (step.Detail ?? "Cancelled.") + ProtectionOutcome(run, context));
                    break;
            }

            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (context.Stages is not null)
        {
            await FinishPipelineAsync(db, run, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var noChanges = run.Steps.Any(s => s.Name == StepNames.Apply && s.Status == StepStatuses.Skipped);
        Finish(run, RunStatuses.Succeeded, context.Ansible is not null ? ConfiguredDetail(run)
            : noChanges ? "The plan has no changes." : null);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Applies the policy to the plan; returns true when the approval step is settled.</summary>
    private async Task<bool> DecideApprovalAsync(OctopushyDb db, Run run, Step step, RunContext context, CancellationToken cancellationToken)
    {
        var plan = run.Steps.Single(s => s.Name == StepNames.Plan);
        var outputs = plan.Outputs is null ? null : JsonNode.Parse(plan.Outputs);
        // The plan's effects refuse this already; asked again so no path offers it for approval.
        if (GuestVmPlan.ForeignChange(outputs, context) is { } foreign)
        {
            step.Status = StepStatuses.Failed;
            step.Detail = foreign;
            Finish(run, RunStatuses.Failed, foreign);
            return false;
        }

        var deletes = outputs?["counts"]?["delete"]?.GetValue<int>() ?? 0;
        // Output-only plans can still need privileged apply. Manual means manual for all.
        var resourceChanges = (outputs?["changes"] as JsonArray)?.Count ?? 0;
        var policy = context.Policy!;
        var privilegedRemains = run.Steps.Any(s => s.Kind == StepKinds.Agent && !StepStatuses.IsDone(s.Status)
            && AssignmentBuilder.TokenFor(s.Name) == SecretNames.ProxmoxPrivilegedToken);
        step.PlanSha256 = outputs?["planSha256"]?.GetValue<string>();
        if (privilegedRemains && string.IsNullOrWhiteSpace(step.PlanSha256))
        {
            step.Status = StepStatuses.Failed;
            step.Detail = "The plan has no saved plan file for privileged work. Inspect and replan with an agent that retains the plan.";
            Finish(run, RunStatuses.Failed, step.Detail);
            return false;
        }
        var manual = policy.Approval == "manual" && (resourceChanges > 0
            || outputs?["hasChanges"]?.GetValue<bool>() == true || privilegedRemains);
        var destructiveManual = deletes > 0 && policy.Destructive == "manual";
        // Lifting protection is what lets a provision plan destroy a VM, so a VM that may hold data
        // goes only with a person's approval, or where destructive was set to automatic on purpose.
        // A VM that never finished its first apply holds nothing yet, and rerunning recreates it.
        var removesVm = run.Operation == Operations.Provision && GuestVmPlan.RemovesVm(outputs);
        var recoversVm = removesVm && GuestVmPlan.RecreatesFailedVm(outputs, context)
            && await db.Resources.AnyAsync(r => r.Id == run.ResourceId
                && r.AppliedAt == null && r.VerifiedAt == null && r.BoundVmid == null, cancellationToken);
        // OpenTofu holds the key a machine was created with, which a rotation may have taken off it. A
        // replacement is a new machine, so it has to start with the key in use, and this plan does not.
        if (context.ManagedAccess && run.Operation == Operations.Provision
            && GuestVmPlan.MakesGuestMachine(outputs, context)
            && await GuestAccessCredentials.RebaseBootstrapAsync(db, run.ResourceId, cancellationToken))
        {
            step.Status = StepStatuses.Failed;
            step.Detail = "This plan makes a new machine, and it carried a key that has since been rotated. Plan again: the new plan carries the key in use.";
            Finish(run, RunStatuses.Failed, step.Detail);
            return false;
        }

        // A plan that cannot say which machine it makes might make this one, with a key nobody holds.
        if (context.ManagedAccess && run.Operation == Operations.Provision
            && GuestVmPlan.MakesUnnamedMachine(outputs) && await GuestAccessCredentials.BootstrapIsBehindAsync(db, run.ResourceId, cancellationToken))
        {
            step.Status = StepStatuses.Failed;
            step.Detail = "This plan makes a machine without saying its VMID and host, and this guest's key has been rotated. A new machine might not be reachable. Inspect it before going on.";
            Finish(run, RunStatuses.Failed, step.Detail);
            return false;
        }

        var vmHeld = removesVm && !recoversVm && !(policy.Destructive == "automatic" && policy.DestructiveExplicit);
        var vmNote = recoversVm ? $" The plan recreates VM {context.Proxmox.Vmid}, whose first apply did not finish; it was never verified."
            : removesVm ? " The plan destroys a VM and its disks."
            : "";
        step.ApprovalRequired = manual || destructiveManual || vmHeld;
        if (!step.ApprovalRequired)
        {
            step.Decision = "approved";
            step.DecidedBy = "policy";
            step.DecidedAt = clock.GetUtcNow();
            step.DecisionNote = resourceChanges == 0 && !privilegedRemains
                ? "The plan changes no resources, so it needs no approval."
                : (deletes > 0 || removesVm && !recoversVm
                    ? $"Approval and destructive are automatic (approval set by {policy.ApprovalSetBy}, destructive {policy.DestructiveSetBy})."
                    : $"Approval is automatic (set by {policy.ApprovalSetBy}).") + vmNote;
            step.Status = StepStatuses.Succeeded;
            step.StartedAt = step.FinishedAt = clock.GetUtcNow();
            return true;
        }

        step.Status = StepStatuses.AwaitingApproval;
        step.StartedAt ??= clock.GetUtcNow();
        step.Detail = vmHeld && !manual
            ? $"The plan destroys a VM and its disks. That waits for approval unless destructive is set to automatic explicitly (it is {policy.Destructive}, set by {policy.DestructiveSetBy})."
            : (destructiveManual && !manual
                ? $"The plan deletes {deletes} resource(s) and destructive is manual (set by {policy.DestructiveSetBy})."
                : $"Approval is manual (set by {policy.ApprovalSetBy}).") + vmNote;
        return false;
    }

    private void CreateAttempt(OctopushyDb db, Run run, Step step, RunContext context, Guid? preferredAgent = null, DateTimeOffset? createdAt = null)
    {
        var action = AssignmentBuilder.ActionFor(step.Name);
        if (preferredAgent is null && step.Name == StepNames.Apply)
        {
            // Keep the apply where the plan was prepared when that agent is available.
            preferredAgent = run.Steps.Single(s => s.Name == StepNames.Plan && s.Stage == step.Stage).Attempts
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
            CreatedAt = createdAt ?? clock.GetUtcNow(),
        };
        step.Attempts.Add(attempt);
        db.Attempts.Add(attempt);
    }

    /// <summary>Records what a finished step established about its resource.</summary>
    private async Task ApplyEffectsAsync(OctopushyDb db, Run run, Step step, JsonNode? outputs, CancellationToken cancellationToken)
    {
        if (step.Stage is not null)
        {
            await ApplyStageEffectsAsync(db, run, step, outputs, cancellationToken);
            return;
        }

        // Refused before anything else is recorded, and before any approval is offered: see docs/safety-boundary.md.
        if (step.Name == StepNames.Plan && GuestVmPlan.ForeignChange(outputs, RunContext.Parse(run.Context)) is { } foreign)
        {
            step.Status = StepStatuses.Failed;
            step.Detail = foreign;
            return;
        }

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
                    if (run.Operation == Operations.Provision)
                    {
                        resource.SyncedInputsHash = run.InputsHash;
                    }

                    foreach (var later in run.Steps.Where(s => s.Name is StepNames.Unprotect or StepNames.Apply or StepNames.Approval))
                    {
                        later.Status = StepStatuses.Skipped;
                        later.Detail = "The plan has no changes.";
                    }
                }

                if (run.Operation is Operations.Provision or Operations.Retire
                    && run.Steps.SingleOrDefault(s => s.Name == StepNames.Unprotect) is { Status: StepStatuses.Pending } unprotect)
                {
                    var planned = RunContext.Parse(run.Context);
                    if (!GuestVmPlan.RemovesGuestVm(outputs, planned))
                    {
                        unprotect.Status = StepStatuses.Skipped;
                        unprotect.Detail = "The plan does not replace or delete this guest's VM, so its protection stays as it is.";
                    }
                    else if (resource.ParentAddress is not { } installation || planned.Proxmox.Vmid is not { } vmid
                             || !await SafetyBoundary.OwnsAsync(db, resource, installation, planned.Proxmox.Node, vmid, cancellationToken))
                    {
                        // Only a VM the guest owns loses its protection: one it is bound to, or its own create that did not finish.
                        unprotect.Status = StepStatuses.Skipped;
                        unprotect.Detail = $"This guest does not own VM {planned.Proxmox.Vmid} on {planned.Proxmox.Node}, so its protection stays as it is.";
                    }
                }

                break;
            case StepNames.Apply when run.Operation == Operations.Provision:
                resource.AppliedInputsHash = run.InputsHash;
                resource.AppliedRevisionId = run.RevisionId;
                resource.AppliedAt = now;
                resource.SyncedInputsHash = run.InputsHash;
                resource.Outputs = outputs?["outputs"]?.ToJsonString();
                // Not bound here: a guest owns a VM once verify-guest confirms the create; see the verify step.
                if (resource.BoundVmid is null && SafetyBoundary.Reserves(resource, RunContext.Parse(run.Context))
                    && run.Steps.Single(s => s.Name == StepNames.Plan).Outputs is { } planText
                    && GuestVmPlan.MakesGuestMachine(JsonNode.Parse(planText), RunContext.Parse(run.Context)))
                    resource.ReservationAppliedAt = now;
                await ProposeConfigurationAgainAsync(db, run, cancellationToken);
                await ProposeServicesAgainAsync(db, run, cancellationToken);
                await RecordProvisionedKeyAsync(db, run, now, cancellationToken);
                break;
            case StepNames.Apply:
                resource.Outputs = null;
                break;
            case StepNames.Verify:
                // Ownership comes only from a confirmed creation: an apply of an approved plan of this guest created
                // its VMID on its node, and the VM verified now carries Octopushy's marks. Nothing else binds.
                if (resource.BoundVmid is null)
                {
                    var verifying = RunContext.Parse(run.Context);
                    if (!SafetyBoundary.Reserves(resource, verifying) || resource.ReservationAppliedAt is null)
                    {
                        step.Detail = $"VM {verifying.Proxmox.Vmid} on {verifying.Proxmox.Node} is not recorded as this guest's: "
                            + "no apply of an approved plan of this guest has created it.";
                    }
                    else if (SafetyBoundary.MarksMissing(outputs, verifying) is { } missing)
                    {
                        step.Status = StepStatuses.Failed;
                        step.Detail = missing;
                        break;
                    }
                    else
                    {
                        resource.BoundInstallation = resource.ParentAddress;
                        resource.BoundVmid = verifying.Proxmox.Vmid;
                        resource.BoundAt = now;
                    }
                }

                resource.VerifiedAt = now;
                break;
            case StepNames.VerifyAbsent:
                resource.RetiredAt = now;
                resource.VerifiedAt = now;
                resource.BoundVmid = null;
                resource.BoundInstallation = null;
                resource.ReservedVmid = null;
                resource.ReservedNode = null;
                resource.ReservedInstallation = null;
                resource.ReservedAt = null;
                resource.ReservationAppliedAt = null;
                await GuestAccessCredentials.RemoveAsync(db, run.ResourceId, cancellationToken);
                await RetireServicesAsync(db, run, now, cancellationToken);
                break;
            case StepNames.Playbook or StepNames.PlaybookApproved:
                await ApplyConfigureEffectsAsync(db, run, step, cancellationToken);
                break;
            case StepNames.VerifyCurrent or StepNames.AuthorizeKey or StepNames.VerifyKey or StepNames.ConfirmRefused:
                await ApplyRotationEffectsAsync(db, run, step, outputs, now, cancellationToken);
                break;
        }
    }

    /// <summary>
    /// Whether this provision run took protection off a VM and did not apply. A successful apply needs
    /// no help: the module recreates the VM with protection on. A lift that left no answer counts.
    /// </summary>
    private static bool LiftedProtection(Run run) =>
        run.Operation == Operations.Provision
        && run.Steps.All(s => s.Name != StepNames.Apply || s.Status != StepStatuses.Succeeded)
        && run.Steps.Where(s => s.Name == StepNames.Unprotect).SelectMany(s => s.Attempts).Any(a =>
            a.Status == AttemptStatuses.Uncertain
            || a.Status == AttemptStatuses.Succeeded && a.Outputs is { } outputs
                && JsonNode.Parse(outputs)?["previous"] is JsonValue previous && previous.TryGetValue<bool>(out var wasOn) && wasOn);

    /// <summary>
    /// Keeps a run that lifted protection and did not apply open until protection is back, on whatever
    /// VM holds the guest's VMID by then. Returns false once there is nothing left to wait for. The
    /// run stays unfinished meanwhile, so a rerun waits and cannot race this step.
    /// </summary>
    private bool RestoringProtection(OctopushyDb db, Run run, RunContext context)
    {
        var restore = run.Steps.SingleOrDefault(s => s.Name == StepNames.Reprotect);
        if (restore is null)
        {
            if (!LiftedProtection(run))
                return false;
            restore = new Step
            {
                Id = Guid.CreateVersion7(),
                RunId = run.Id,
                Ordinal = run.Steps.Max(s => s.Ordinal) + 1,
                Name = StepNames.Reprotect,
                Kind = StepKinds.Agent,
                Status = StepStatuses.Queued,
            };
            run.Steps.Add(restore);
            db.Steps.Add(restore);
            CreateAttempt(db, run, restore, context);
            signal.Wake();
        }

        if (restore.Status == StepStatuses.Uncertain)
        {
            run.Status = RunStatuses.Uncertain;
            run.Detail = restore.Detail;
            return true;
        }
        if (restore.Status is not (StepStatuses.Queued or StepStatuses.Running))
            return false;
        run.Status = RunStatuses.Running;
        run.Detail = $"Restoring the protection of VM {context.Proxmox.Vmid}: the plan was not applied in full.";
        return true;
    }

    /// <summary>What became of the protection this run lifted, for the end of the run's detail.</summary>
    private static string ProtectionOutcome(Run run, RunContext context)
    {
        var vmid = context.Proxmox.Vmid;
        return run.Steps.SingleOrDefault(s => s.Name == StepNames.Reprotect) switch
        {
            null => "",
            { Status: StepStatuses.Succeeded, Outputs: var outputs } when outputs is not null && JsonNode.Parse(outputs)?["absent"] is JsonValue absent
                && absent.TryGetValue<bool>(out var gone) && gone => $" VM {vmid} no longer exists, so there was no protection to restore. Plan again to create it.",
            { Status: StepStatuses.Succeeded } => $" Protection of VM {vmid} is back on. Plan again to retry.",
            _ => $" Protection of VM {vmid} could not be restored and may still be off. Plan again to retry, or turn it on in Proxmox.",
        };
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
