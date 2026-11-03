using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
using Octopushy.Server.Git;

namespace Octopushy.Server.Orchestration;

/// <summary>What is particular to a pipeline run: one approval of every stage, then each applied in order, on a guest shared with other work.</summary>
public sealed partial class RunEngine
{
    private static readonly HashSet<string> Applying = [StepNames.Apply, StepNames.Playbook];

    /// <summary>
    /// Writes the document that covers every stage and applies the policy to it. Destructive here means
    /// what it means elsewhere: a recipe that declares its action destructive, and a plan that deletes.
    /// A plan's replace counts too: a provision run holds a replaced VM by another rule, and a stage has none.
    /// </summary>
    private bool DecidePipelineApproval(Run run, Step step, RunContext context)
    {
        step.StartedAt ??= clock.GetUtcNow();
        PipelineDocument document;
        try
        {
            document = PipelineDocument.Build(run, context);
        }
        catch (RunException e)
        {
            step.Status = StepStatuses.Failed;
            step.Detail = e.Message;
            Finish(run, RunStatuses.Failed, e.Message);
            return false;
        }

        step.Document = document.Text;
        step.PlanSha256 = document.Sha256;
        var policy = context.Policy!;
        var manual = policy.Approval == "manual" && document.HasChanges;
        var destructiveManual = document.Destructive.Count > 0 && policy.Destructive == "manual";
        step.ApprovalRequired = manual || destructiveManual;
        if (!step.ApprovalRequired)
        {
            step.Decision = "approved";
            step.DecidedBy = "policy";
            step.DecidedAt = clock.GetUtcNow();
            step.DecisionNote = !document.HasChanges
                ? "No stage has anything to change, so this needs no approval."
                : document.Destructive.Count > 0
                    ? $"Approval and destructive are automatic (approval set by {policy.ApprovalSetBy}, destructive {policy.DestructiveSetBy})."
                    : $"Approval is automatic (set by {policy.ApprovalSetBy}).";
            step.Status = StepStatuses.Succeeded;
            step.FinishedAt = clock.GetUtcNow();
            return true;
        }

        step.Status = StepStatuses.AwaitingApproval;
        step.Detail = manual
            ? $"Approval is manual (set by {policy.ApprovalSetBy})."
            : $"Destructive is manual (set by {policy.DestructiveSetBy}): {string.Join("; ", document.Destructive)}.";
        return false;
    }

    /// <summary>
    /// What a finished stage step established. A plan with nothing to apply skips its apply. A playbook
    /// can report a declared outcome that ends the stage, and nothing after it runs.
    /// </summary>
    private async Task ApplyStageEffectsAsync(OctopushyDb db, Run run, Step step, JsonNode? outputs, CancellationToken cancellationToken)
    {
        if (step.Name == StepNames.Plan && outputs?["hasChanges"]?.GetValue<bool>() == false)
        {
            var apply = run.Steps.Single(s => s.Stage == step.Stage && s.Name == StepNames.Apply);
            apply.Status = StepStatuses.Skipped;
            apply.Detail = "The plan has no changes.";
        }
        else if (step.Name == StepNames.Playbook)
        {
            var ansible = RunContext.Parse(run.Context).ForStep(step).Ansible!;
            if (outputs?["outcome"]?.GetValue<string>() is { } name && ansible.Outcomes.TryGetValue(name, out var declared) && declared.Result == "failed")
            {
                step.Status = StepStatuses.Failed;
                step.Detail = $"It reported {name}{(outputs["detail"]?.GetValue<string>() is { } detail ? $": {detail}" : "")}.";
                return;
            }

            await RecordKeyUseAsync(db, run, step, cancellationToken);
        }
    }

    /// <summary>Every stage ended well, so the service is at these inputs.</summary>
    private async Task FinishPipelineAsync(OctopushyDb db, Run run, CancellationToken cancellationToken)
    {
        if (await db.GuestConfigurations.FirstOrDefaultAsync(c => c.ResourceId == run.ResourceId, cancellationToken) is { } state)
        {
            state.AppliedHash = run.InputsHash;
            state.AppliedRevisionId = run.RevisionId;
            state.AppliedAt = clock.GetUtcNow();
        }

        Finish(run, RunStatuses.Succeeded, null);
    }

    /// <summary>
    /// Why this run may not start changing its guest now, or null. Another run may be changing that guest, and
    /// for a pipeline the guest must still be ready: an approval says nothing about what has happened to it since.
    /// </summary>
    private async Task<string?> ChangeHeldAsync(OctopushyDb db, Run run, RunContext context, bool dispatching, CancellationToken cancellationToken)
    {
        if (await GuestWork.HolderAsync(db, run, cancellationToken, dispatching) is { } holder)
            return $"Waiting for run #{holder.Number}, which is changing the same guest.";
        if (context.GuestResourceId is not { } id)
            return null;
        var head = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
        var guest = await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (head is null || guest is null)
            return "Waiting for the current repository source.";
        try
        {
            return await GuestWork.WaitingForAsync(db, guest, await revisions.GetAsync(head.Commit, cancellationToken), cancellationToken);
        }
        catch (Exception e) when (e is GitException or IOException)
        {
            // One unreadable revision stops this run, not the pass that advances every other.
            return $"Waiting for the current revision to be readable: {e.Message}";
        }
    }

    /// <summary>
    /// Puts a change that may not go yet back with its run, which holds it until it may. In the queue it would
    /// take a place in each pass from the work that may be what it waits for, such as its guest's setup.
    /// </summary>
    private void TakeBack(Attempt attempt, Step step, Run run, string reason)
    {
        attempt.Status = AttemptStatuses.Cancelled;
        attempt.Error = reason;
        attempt.FinishedAt = clock.GetUtcNow();
        step.Status = StepStatuses.Pending;
        SetDetail(run, reason);
    }

    /// <summary>A pipeline's change that is still queued, or was taken back, reached no agent, so it changed nothing.</summary>
    private static bool NeverSent(Run run, Attempt attempt) =>
        run.Operation == Operations.Pipeline && attempt.AssignedAt is null && attempt.StartedAt is null
        && attempt.Status is AttemptStatuses.Queued or AttemptStatuses.Cancelled;

    /// <summary>
    /// Continues a failed pipeline from the stage that failed. The approval still covers it, unless what it
    /// approved is no longer what the repository asks for. A saved plan cannot be used again once the failed
    /// apply moved its state, so that takes a new run.
    /// </summary>
    public Task RetryStageAsync(Guid runId, CancellationToken cancellationToken) =>
        WithDbAsync(async db =>
        {
            var run = await LoadRunAsync(db, runId, cancellationToken);
            var failed = run.Steps.OrderBy(s => s.Ordinal).FirstOrDefault(s => s.Status == StepStatuses.Failed);
            if (run.Operation != Operations.Pipeline || run.Status != RunStatuses.Failed || failed?.Stage is null || !Applying.Contains(failed.Name))
                throw new RunException("Only a pipeline that failed while applying a stage can be retried.");
            if (run.Steps.SelectMany(s => s.Attempts).Any(a => a.Status == AttemptStatuses.Uncertain && a.Mutating))
                throw new RunException("A stage's outcome is uncertain, so it cannot be repeated. Inspect the guest and deploy again.");
            if (await RepositoryTrust.BlockedReasonAsync(db, run, cancellationToken) is { } trustReason)
                throw new RunException(trustReason);
            var resource = await db.Resources.FirstAsync(r => r.Id == run.ResourceId, cancellationToken);
            var state = await db.GuestConfigurations.AsNoTracking().FirstOrDefaultAsync(c => c.ResourceId == run.ResourceId, cancellationToken);
            if (!resource.Valid || resource.MissingSince is not null || resource.RetiredAt is not null
                || state is null || state.DesiredHash != run.InputsHash || state.ProposedHash != run.InputsHash
                || await db.Runs.AnyAsync(r => r.ResourceId == run.ResourceId && r.Id != run.Id && r.CreatedAt > run.CreatedAt, cancellationToken))
                throw new RunException("The inputs changed since this was approved. Deploy again: a new run needs its own approval.");
            if (failed.Name == StepNames.Apply && await StateMovedAsync(db, run, failed, cancellationToken))
                throw new RunException("The failed apply changed this stage's state, so OpenTofu will not use the saved plan. Deploy again to plan what is left.");

            foreach (var step in run.Steps.Where(s => s.Ordinal >= failed.Ordinal && s.Detail != "The plan has no changes."))
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

    private static async Task<bool> StateMovedAsync(OctopushyDb db, Run run, Step apply, CancellationToken cancellationToken)
    {
        var planned = run.Steps.Single(s => s.Stage == apply.Stage && s.Name == StepNames.Plan).Attempts
            .Where(a => a.Status == AttemptStatuses.Succeeded).MaxBy(a => a.Number)?.StateVersion;
        var head = await db.TofuStates.AsNoTracking().Where(s => s.ResourceId == run.ResourceId && s.Stage == apply.Stage)
            .Select(s => (int?)s.HeadVersion).FirstOrDefaultAsync(cancellationToken);
        return (planned ?? 0) != (head ?? 0);
    }

    /// <summary>
    /// A provision run that created or replaced something may have made a new machine. What was applied was
    /// applied to the old one, so its services are deployed again, after its setup has run again.
    /// </summary>
    private static async Task ProposeServicesAgainAsync(OctopushyDb db, Run run, CancellationToken cancellationToken)
    {
        var counts = run.Steps.Single(s => s.Name == StepNames.Plan).Outputs is { } text ? JsonNode.Parse(text)?["counts"] : null;
        if ((counts?["create"]?.GetValue<int>() ?? 0) + (counts?["replace"]?.GetValue<int>() ?? 0) == 0)
            return;
        var family = await GuestWork.FamilyAsync(db, run.ResourceId, cancellationToken);
        if (family.Count < 2)
            return;
        foreach (var state in await db.GuestConfigurations.Where(c => family.Contains(c.ResourceId)).ToListAsync(cancellationToken))
        {
            state.ProposedHash = null;
            state.AppliedHash = null;
        }
    }

    /// <summary>
    /// The guest is gone, so its services are too, and whatever their runs still meant to do. Nothing else
    /// removes them: see docs/pipelines.md. A run left uncertain stays for a person to close.
    /// </summary>
    private async Task RetireServicesAsync(OctopushyDb db, Run run, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var guest = await db.Resources.Where(r => r.Id == run.ResourceId).Select(r => r.Address).FirstAsync(cancellationToken);
        foreach (var service in await db.Resources.Where(r => r.Kind == ResourceKinds.Service && r.ParentAddress == guest && r.RetiredAt == null).ToListAsync(cancellationToken))
        {
            service.RetiredAt = now;
            foreach (var id in await db.Runs.Where(r => r.ResourceId == service.Id && r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed
                    && r.Status != RunStatuses.Cancelled && r.Status != RunStatuses.Uncertain).Select(r => r.Id).ToListAsync(cancellationToken))
            {
                await CancelInternalAsync(db, id, "The guest was removed.", cancellationToken);
            }
        }
    }
}
