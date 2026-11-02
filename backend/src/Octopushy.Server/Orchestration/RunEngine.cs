using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Agent.V1;
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
            else
                await BindManagedWorkerAsync(db, run, cancellationToken);
            db.Runs.Add(run);
        }
        await db.SaveChangesAsync(cancellationToken);
        signal.Wake();
    }

    public Task ApproveAsync(Guid runId, Guid stepId, string planSha256, string user, string? note, CancellationToken cancellationToken, string? workerUpdateSha256 = null) =>
        WithDbAsync(async db =>
        {
            var step = await AwaitingApprovalAsync(db, runId, stepId, cancellationToken);
            if (!string.Equals(step.PlanSha256, planSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new RunException("The plan changed since it was shown; review the current plan.");
            }
            var run = await db.Runs.SingleAsync(r => r.Id == runId, cancellationToken);
            var update = RunContext.Parse(run.Context).WorkerUpdate;
            if (update is not null && !string.Equals(update.ArchiveSha256, workerUpdateSha256, StringComparison.OrdinalIgnoreCase))
                throw new RunException("Explicit approval of the displayed worker OS closure fingerprint is required.");

            step.Decision = "approved";
            step.WorkerUpdateSha256 = update?.ArchiveSha256;
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
            if (run.Status != RunStatuses.Failed || failed?.Name is not (StepNames.Verify or StepNames.GrowRoot or StepNames.VerifyRoot or StepNames.PrepareWorker or StepNames.InstallWorker or StepNames.WaitWorker)
                // Only a mutating attempt can leave an unknown change behind; a read-only one that lost its agent changed nothing.
                || run.Steps.SelectMany(s => s.Attempts).Any(a => a.Status == AttemptStatuses.Uncertain && a.Mutating))
            {
                throw new RunException("Only a known failed guest verification, filesystem or managed bootstrap step can be retried. Inspect uncertain outcomes and plan again.");
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

        // A run that lifted protection and will not apply must not leave the VM or container unprotected.
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
        if (!inFlight && !reconcilingOutcome && await RepositoryTrust.BlockedReasonAsync(db, run, cancellationToken) is { } trustReason)
        {
            run.Status = RunStatuses.Blocked;
            run.Detail = trustReason;
            foreach (var attempt in run.Steps.SelectMany(s => s.Attempts).Where(a => a.Status == AttemptStatuses.Queued))
                attempt.WaitingReason = trustReason;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (!inFlight && !reconcilingOutcome && await ClusterPlan.BlockedReasonAsync(db, run, cancellationToken) is { } memberReason)
        {
            run.Status = RunStatuses.Blocked;
            run.Detail = memberReason;
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
        if (!inFlight && !reconcilingOutcome)
        {
            try { await BindManagedWorkerAsync(db, run, cancellationToken); }
            catch (RunException e)
            {
                Finish(run, RunStatuses.Failed, e.Message);
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
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

            if (step.Name == StepNames.WaitWorker)
            {
                if (await WorkerReadyAsync(db, run, step, cancellationToken)) continue;
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            if (step.Name == StepNames.DrainWorker)
            {
                if (await DrainWorkerAsync(db, run, step, cancellationToken)) continue;
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            if (step.Name == StepNames.RevokeWorker)
            {
                if (await RevokeWorkerAsync(db, run, step, cancellationToken)) continue;
                Finish(run, RunStatuses.Failed, step.Detail);
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            if (step.Name == StepNames.WaitUpdatedWorker)
            {
                var update = context.WorkerUpdate ?? throw new RunException("Missing worker update context.");
                var switchedAt = run.Steps.Single(s => s.Name == StepNames.UpdateWorkerOs).FinishedAt;
                var binding = await db.ManagedWorkers.SingleAsync(w => w.ResourceId == run.ResourceId, cancellationToken);
                if (binding.AgentId != update.AgentId || !await db.Agents.AnyAsync(a => a.Id == update.AgentId && !a.Disabled && a.Draining, cancellationToken))
                {
                    step.Status = StepStatuses.Failed;
                    step.FinishedAt = clock.GetUtcNow();
                    step.Detail = "The original worker enrollment or its drain changed. Inspect the OS update; no replacement enrollment will be issued.";
                    Finish(run, RunStatuses.Failed, step.Detail);
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }
                if (binding.AgentId == update.AgentId && switchedAt is not null && binding.AuthenticatedAt >= switchedAt
                    && connections.Get(update.AgentId) is { } updated && updated.Executors.Contains("opentofu") && updated.Executors.Contains("proxmox"))
                {
                    binding.VerifiedOsImageSha256 = update.ImageSha256;
                    binding.VerifiedOsUpdateRunId = run.Id;
                    step.Status = StepStatuses.Succeeded;
                    step.FinishedAt = clock.GetUtcNow();
                    step.Detail = "The original worker authenticated again after the OS update. It remains drained until explicitly resumed.";
                    continue;
                }
                step.Status = StepStatuses.Running;
                step.StartedAt ??= clock.GetUtcNow();
                step.Detail = run.Detail = "Waiting for the same worker identity to authenticate after the verified OS update.";
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            switch (step.Status)
            {
                case StepStatuses.Pending or StepStatuses.Blocked when step.Kind == StepKinds.Approval:
                    if (await DecideApprovalAsync(db, run, step, context, cancellationToken))
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
                    var missing = await CredentialBindings.CheckAsync(db, run, context, cancellationToken, AssignmentBuilder.SecretsFor(step.Name, context));
                    if (missing.Count > 0)
                    {
                        step.Status = StepStatuses.Blocked;
                        step.Detail = $"Blocked credentials: {string.Join(", ", missing)}. Review Variables.";
                        run.Status = RunStatuses.Blocked;
                        run.Detail = step.Detail;
                        break;
                    }

                    if (step.Name == StepNames.Apply && context.Cluster is { } members)
                        await ClusterPlan.BindMembersAsync(db, run, members, clock.GetUtcNow(), cancellationToken);
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
                    // What a member reports is not a failed deployment: the change went through.
                    if (!RestoringProtection(db, run, context))
                        Finish(run, RunStatuses.Failed, (step.Name == StepNames.VerifyCluster ? $"Verification failed: {step.Detail}" : $"The {step.Name} step failed: {step.Detail}") + ProtectionOutcome(run, context));
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

        var noChanges = run.Steps.Any(s => s.Name == StepNames.Apply && s.Status == StepStatuses.Skipped);
        Finish(run, RunStatuses.Succeeded, context.WorkerUpdate is not null
            ? "Worker OS update verified with the original identity and state filesystem. The worker remains drained; resume scheduling explicitly."
            : noChanges
            ? context.DiskReporter is null ? "The plan has no changes." : "Infrastructure unchanged; disk reporter setup completed."
            : null);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Applies the policy to the plan; returns true when the approval step is settled.</summary>
    private async Task<bool> DecideApprovalAsync(OctopushyDb db, Run run, Step step, RunContext context, CancellationToken cancellationToken)
    {
        var plan = run.Steps.Single(s => s.Name == StepNames.Plan);
        var outputs = plan.Outputs is null ? null : JsonNode.Parse(plan.Outputs);
        var deletes = outputs?["counts"]?["delete"]?.GetValue<int>() ?? 0;
        // Output-only plans can still need privileged apply, and a no-op rootfs
        // run or reporter setup still has privileged guest-agent work. Manual means manual for all.
        var resourceChanges = (outputs?["changes"] as JsonArray)?.Count ?? 0;
        var policy = context.Policy!;
        if (context.WorkerUpdate is not null && !ManagedWorkerSafety.UnchangedPlan(outputs))
        {
            step.Status = StepStatuses.Failed;
            step.Detail = "Worker OS updates require an unchanged infrastructure plan. Reconcile infrastructure separately; this path never recreates a VM.";
            Finish(run, RunStatuses.Failed, step.Detail);
            return false;
        }
        if (context.ManagedWorker is not null && ManagedWorkerSafety.DestructivePlan(outputs) && !ManagedWorkerSafety.IsWorkerRemoval(run, context))
        {
            step.Status = StepStatuses.Failed;
            step.Detail = "Managed worker deletion/replacement is not supported; preserve the state disk and binding.";
            Finish(run, RunStatuses.Failed, step.Detail);
            return false;
        }
        var privilegedRemains = run.Steps.Any(s => s.Kind == StepKinds.Agent && !StepStatuses.IsDone(s.Status)
            && AssignmentBuilder.TokenFor(s.Name) == SecretNames.ProxmoxPrivilegedToken);
        step.PlanSha256 = outputs?["planSha256"]?.GetValue<string>();
        if (privilegedRemains && string.IsNullOrWhiteSpace(step.PlanSha256))
        {
            step.Status = StepStatuses.Failed;
            step.Detail = "The plan has no saved plan file for privileged work. Inspect and replan with a worker that retains the plan.";
            Finish(run, RunStatuses.Failed, step.Detail);
            return false;
        }
        var manual = (context.ManagedWorker is not null || policy.Approval == "manual") && (resourceChanges > 0
            || outputs?["hasChanges"]?.GetValue<bool>() == true || privilegedRemains);
        // A replacement in the plan, but it deletes what someone added to a live server.
        var prunes = run.Operation == Operations.Cluster && ClusterPlan.PrunesEntries(outputs, InspectionOf(run));
        var destructiveManual = (deletes > 0 || prunes) && policy.Destructive == "manual";
        // Lifting protection is what lets a provision plan destroy a VM or a container, so one that may
        // hold data goes only with a person's approval, or where destructive was set to automatic on purpose.
        // A guest that never finished its first apply holds nothing yet, and rerunning recreates it.
        var removesVm = run.Operation == Operations.Provision && GuestVmPlan.RemovesVm(outputs);
        var recoversVm = removesVm && GuestVmPlan.RecreatesFailedVm(outputs, context)
            && await db.Resources.AnyAsync(r => r.Id == run.ResourceId
                && r.AppliedAt == null && r.VerifiedAt == null && r.BoundVmid == null, cancellationToken);
        var vmHeld = removesVm && !recoversVm && !(policy.Destructive == "automatic" && policy.DestructiveExplicit);
        var noun = GuestVmPlan.Noun(context);
        var vmNote = recoversVm ? $" The plan recreates {noun} {context.Proxmox.Vmid}, whose first apply did not finish; it was never verified."
            : removesVm ? $" The plan destroys a {noun} and its disks."
            : "";
        step.ApprovalRequired = manual || destructiveManual || vmHeld;
        if (!step.ApprovalRequired)
        {
            step.Decision = "approved";
            step.DecidedBy = "policy";
            step.DecidedAt = clock.GetUtcNow();
            step.DecisionNote = resourceChanges == 0 && !privilegedRemains
                ? "The plan changes no resources, so it needs no approval."
                : (deletes > 0 || prunes || removesVm && !recoversVm
                    ? $"Approval and destructive are automatic (approval set by {policy.ApprovalSetBy}, destructive {policy.DestructiveSetBy})."
                    : $"Approval is automatic (set by {policy.ApprovalSetBy}).") + vmNote;
            step.Status = StepStatuses.Succeeded;
            step.StartedAt = step.FinishedAt = clock.GetUtcNow();
            return true;
        }

        step.Status = StepStatuses.AwaitingApproval;
        step.StartedAt ??= clock.GetUtcNow();
        step.Detail = vmHeld && !manual
            ? $"The plan destroys a {noun} and its disks. That waits for approval unless destructive is set to automatic explicitly (it is {policy.Destructive}, set by {policy.DestructiveSetBy})."
            : (destructiveManual && !manual
                ? deletes > 0
                    ? $"The plan deletes {deletes} resource(s) and destructive is manual (set by {policy.DestructiveSetBy})."
                    : $"The plan removes entries from the server and destructive is manual (set by {policy.DestructiveSetBy})."
                : $"Approval is manual (set by {policy.ApprovalSetBy}).") + vmNote;
        if (prunes)
            step.Detail += $" It removes what nobody declared: {string.Join(", ", ClusterPlan.UndeclaredEntriesIn(InspectionOf(run)))}.";
        if (context.DiskReporter is not null)
        {
            step.Detail += " This run also installs the disk reporter inside the VM after verification.";
        }
        if (context.WorkerUpdate is { } workerUpdate)
            step.Detail += $" This also switches the existing worker OS to {workerUpdate.SystemPath}; explicitly approve closure SHA-256 {workerUpdate.ArchiveSha256}.";
        return false;
    }

    /// <summary>What the cluster run's inspection found, if it has run.</summary>
    private static JsonNode? InspectionOf(Run run) =>
        run.Steps.SingleOrDefault(s => s.Name == StepNames.InspectCluster)?.Outputs is { } text ? JsonNode.Parse(text) : null;

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
                    if (run.Operation == Operations.Cluster
                        || run.Operation == Operations.Provision && RunContext.Parse(run.Context).RootDiskBytes is null)
                    {
                        resource.SyncedInputsHash = run.InputsHash;
                    }

                    var planContext = RunContext.Parse(run.Context);
                    var needsGuestApproval = run.Operation == Operations.Provision
                        && (planContext.RootDiskBytes is not null || planContext.ManagedWorker is not null || planContext.DiskReporter is not null);
                    foreach (var later in run.Steps.Where(s => s.Name is StepNames.Unprotect or StepNames.Apply
                        or StepNames.DetachMembers or StepNames.ReconcileCluster
                        || s.Name == StepNames.Approval && !needsGuestApproval))
                    {
                        later.Status = StepStatuses.Skipped;
                        later.Detail = "The plan has no changes.";
                    }
                }
                else if (run.Operation == Operations.Cluster)
                {
                    if ((await ClusterPlan.RemovalsAsync(db, run, outputs, cancellationToken)).Count == 0)
                    {
                        var detach = run.Steps.Single(s => s.Name == StepNames.DetachMembers);
                        detach.Status = StepStatuses.Skipped;
                        detach.Detail = "The plan removes no member.";
                    }

                    if (ClusterPlan.Joins(outputs).Count == 0 && !ClusterPlan.PrunesEntries(outputs, InspectionOf(run)))
                    {
                        var reconcile = run.Steps.Single(s => s.Name == StepNames.ReconcileCluster);
                        reconcile.Status = StepStatuses.Skipped;
                        reconcile.Detail = "The plan changes no member.";
                    }
                }

                // What says the lists were downloaded is kept here, not in the plan, so that a
                // download that failed is tried by the next run even if nothing else changes.
                if (run.Operation == Operations.Cluster && ClusterPlan.ListsDownloaded(resource, RunContext.Parse(run.Context).Cluster!))
                {
                    var refresh = run.Steps.Single(s => s.Name == StepNames.RefreshBlockLists);
                    refresh.Status = StepStatuses.Skipped;
                    refresh.Detail = "The declared lists are downloaded.";
                }

                if (run.Operation == Operations.Provision
                    && run.Steps.SingleOrDefault(s => s.Name == StepNames.Unprotect) is { Status: StepStatuses.Pending } unprotect
                    && !GuestVmPlan.RemovesGuestVm(outputs, RunContext.Parse(run.Context)))
                {
                    unprotect.Status = StepStatuses.Skipped;
                    unprotect.Detail = $"The plan does not replace or delete this guest's {GuestVmPlan.Noun(RunContext.Parse(run.Context))}, so its protection stays as it is.";
                }

                break;
            case StepNames.InspectCluster or StepNames.ReconcileCluster:
                await ClusterPlan.RecordPeersAsync(db, run, outputs, cancellationToken);
                MergeCertificates(resource, outputs, RunContext.Parse(run.Context).Cluster?.Endpoints ?? []);
                break;
            case StepNames.DetachMembers:
                await ClusterPlan.RecordDetachedAsync(db, run, outputs, now, cancellationToken);
                break;
            case StepNames.RefreshBlockLists:
                resource.BlockListsRefreshed = RunContext.Parse(run.Context).Cluster!.BlockListsHash;
                break;
            case StepNames.Apply when run.Operation == Operations.Cluster:
                var cluster = RunContext.Parse(run.Context).Cluster!;
                resource.AppliedInputsHash = run.InputsHash;
                resource.AppliedRevisionId = run.RevisionId;
                resource.AppliedAt = now;
                resource.SyncedInputsHash = run.InputsHash;
                resource.Outputs = outputs?["outputs"]?.ToJsonString();
                resource.BoundPrimary ??= cluster.Primary;
                resource.BoundClusterDomain ??= cluster.ClusterDomain;
                resource.BoundAt ??= now;
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
            case StepNames.VerifyCluster:
                resource.VerifiedAt = now;
                // This is a newer check than the one that failed.
                resource.ObserveError = null;
                resource.ObserveErrorAt = null;
                break;
            case StepNames.VerifyRoot:
                if (RunContext.Parse(run.Context).ManagedWorker is null || resource.ManagedWorker?.ReadyAt is not null)
                    resource.SyncedInputsHash = run.InputsHash;
                resource.VerifiedAt = now;
                break;
            case StepNames.VerifyAbsent:
                resource.RetiredAt = now;
                resource.VerifiedAt = now;
                resource.BoundVmid = null;
                resource.BoundInstallation = null;
                break;
        }
    }

    /// <summary>
    /// Whether this provision run took protection off a VM or container and did not apply. A successful apply needs
    /// no help: the module recreates it with protection on. A lift that left no answer counts.
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
    /// guest holds the VMID by then. Returns false once there is nothing left to wait for. The
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
        run.Detail = $"Restoring the protection of {GuestVmPlan.Noun(context)} {context.Proxmox.Vmid}: the plan was not applied in full.";
        return true;
    }

    /// <summary>What became of the protection this run lifted, for the end of the run's detail.</summary>
    private static string ProtectionOutcome(Run run, RunContext context)
    {
        var vmid = context.Proxmox.Vmid;
        var noun = GuestVmPlan.Noun(context);
        return run.Steps.SingleOrDefault(s => s.Name == StepNames.Reprotect) switch
        {
            null => "",
            { Status: StepStatuses.Succeeded, Outputs: var outputs } when outputs is not null && JsonNode.Parse(outputs)?["absent"] is JsonValue absent
                && absent.TryGetValue<bool>(out var gone) && gone => $" {char.ToUpperInvariant(noun[0])}{noun[1..]} {vmid} no longer exists, so there was no protection to restore. Plan again to create it.",
            { Status: StepStatuses.Succeeded } => $" Protection of {noun} {vmid} is back on. Plan again to retry.",
            _ => $" Protection of {noun} {vmid} could not be restored and may still be off. Plan again to retry, or turn it on in Proxmox.",
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
