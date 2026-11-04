using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Changes;
using Octopushy.Server.Data;
using Octopushy.Server.Git;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// Approving a deploy's plan in the same click. A deploy that asks for it records the operator's intent on its change; the
/// intent is decided once, at the first approval of the first run the change's revision proposed for the new guest or
/// service, and it approves that plan only when the plan does nothing but create and every check a person's approval would
/// meet holds. Otherwise the run waits for approval as usual and says why. It never covers a later plan, a retire, or a plan
/// that updates, deletes, replaces or imports anything. See docs/changes-from-the-ui.md.
/// </summary>
public sealed partial class RunEngine
{
    public const string ApprovedAtDeploy = "approved at deploy";

    /// <summary>
    /// Decides a pending pre-approval for <paramref name="run"/>'s approval step: whether it approved the step, as the
    /// operator who deployed, or why it did not. <paramref name="moreThanCreate"/> says why the plan does more than create, or is null.
    /// </summary>
    /// <remarks>With <paramref name="needed"/> false the policy approved the plan already: the intent is settled as not needed.</remarks>
    private async Task<(bool Approved, string? Declined)> PreApproveAsync(OctopushyDb db, Run run, Step step, RunContext context, string? moreThanCreate, bool needed,
        CancellationToken ct)
    {
        if (run.Reason != "revision" || run.RevisionId is null)
            return (false, null);
        var resource = await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == run.ResourceId, ct);
        if (resource is null || resource.Kind is not (ResourceKinds.Guest or ResourceKinds.Service))
            return (false, null);
        var kind = resource.Kind == ResourceKinds.Guest ? ChangeKinds.CreateGuest : ChangeKinds.CreateService;
        var change = await db.Changes
            .Where(c => c.Kind == kind && c.Address == resource.Address && c.PreApproval == PreApprovals.Requested && c.Commit != null
                        && (c.ResourceId == null || c.ResourceId == resource.Id))
            .OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);
        // A deploy refused, failed or superseded before its plan has nothing to approve.
        if (change is null || ChangeStatuses.IsFinal(change.Status))
            return (false, null);
        // The change may not have been looked at since its commit was synced: find its revision now.
        if (change.RevisionId is null)
            await ChangeTracker.RefreshAsync(db, mirror, revisions, change, clock.GetUtcNow(), ct);
        if (change.RevisionId is null || ChangeStatuses.IsFinal(change.Status))
            return (false, null);

        if (!needed)
        {
            change.PreApproval = PreApprovals.NotNeeded;
            change.PreApprovalDetail = $"Run #{run.Number} needed no approval: {step.DecisionNote}";
            change.PreApprovalRunId = run.Id;
            change.UpdatedAt = clock.GetUtcNow();
            return (false, null);
        }

        var reason = await DeclineReasonAsync(db, run, step, context, change, resource, moreThanCreate, ct);
        change.PreApprovalRunId = run.Id;
        change.UpdatedAt = clock.GetUtcNow();
        if (reason is not null)
        {
            change.PreApproval = PreApprovals.Declined;
            change.PreApprovalDetail = reason;
            return (false, reason);
        }

        step.Decision = "approved";
        step.DecidedBy = change.Author;
        step.DecidedAt = clock.GetUtcNow();
        step.DecisionNote = $"{char.ToUpperInvariant(ApprovedAtDeploy[0])}{ApprovedAtDeploy[1..]} (change {change.Id}): the plan only creates.";
        step.Status = StepStatuses.Succeeded;
        step.StartedAt ??= clock.GetUtcNow();
        step.FinishedAt = clock.GetUtcNow();
        change.PreApproval = PreApprovals.Approved;
        change.PreApprovalDetail = $"Run #{run.Number}'s plan was approved at deploy by {change.Author}: it only creates.";
        logger.LogInformation("Run {Run}'s plan was approved at deploy by {Author} (change {Change}).", run.Id, change.Author, change.Id);
        return (true, null);
    }

    /// <summary>Why the pre-approval does not cover this plan, or null when it does.</summary>
    private async Task<string?> DeclineReasonAsync(OctopushyDb db, Run run, Step step, RunContext context, ResourceChange change, Resource resource,
        string? moreThanCreate, CancellationToken ct)
    {
        var expected = resource.Kind == ResourceKinds.Guest ? Operations.Provision : Operations.Pipeline;
        if (change.ResourceId is { } id && id != resource.Id)
            return "The plan is for another resource than the deploy's; approve it yourself.";
        if (run.RevisionId != change.RevisionId)
            return $"Run #{run.Number} is of another revision than the deploy's commit; approve it yourself.";
        if (run.Operation != expected)
            return $"Only the {expected} plan of a deploy is approved at deploy; approve run #{run.Number} yourself.";
        // The first run the deploy's revision proposed for the resource, and only its first decision.
        var earlier = await db.Runs.AsNoTracking().AnyAsync(r => r.ResourceId == resource.Id && r.Id != run.Id && r.CreatedAt >= change.CreatedAt && r.CreatedAt <= run.CreatedAt
            && r.Reason == "revision" && r.Operation == expected, ct);
        if (earlier || run.Steps.Any(s => s.Kind == StepKinds.Approval && s.Id != step.Id && s.Decision is not null))
            return "Only the deploy's first plan is approved at deploy; approve this one yourself.";
        if (moreThanCreate is not null)
            return $"Plan does more than create; approve it yourself. {moreThanCreate}";
        if (step.PlanSha256 is null)
            return "The plan has no fingerprint to approve; approve it yourself.";

        // What a person's approval would meet: the current revision, the safety boundary and trust.
        var head = await RepositoryTrust.HeadRevisions(db).AsNoTracking().Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
        if (head != run.RevisionId)
            return "The repository moved on since the deploy: this plan is not of the current revision. Approve it yourself if it still holds.";
        if (await SafetyBoundary.ObservedOnlyRefusalAsync(db, run, context, ct) is { } observedOnly)
            return observedOnly;
        if (await RepositoryTrust.BlockedReasonAsync(db, run, ct) is { } untrusted)
            return untrusted;
        if (run.Status is RunStatuses.Failed or RunStatuses.Cancelled)
            return "The run did not go on.";
        return null;
    }

    /// <summary>Why a provision plan does more than create, or null when it only creates.</summary>
    internal static string? MoreThanCreate(JsonNode? outputs)
    {
        if (outputs is null)
            return "The plan has no summary.";
        var counts = outputs["counts"] as JsonObject;
        var others = new[] { "update", "delete", "replace" }
            .Select(k => (Kind: k, Count: counts?[k] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0))
            .Where(c => c.Count > 0).Select(c => $"{c.Count} to {c.Kind}").ToList();
        if (others.Count > 0)
            return $"It has {string.Join(", ", others)}.";
        if (outputs["changes"] is not JsonArray changes)
            return "The plan does not list its changes.";
        if (changes.Count == 0)
            return "It creates nothing.";
        foreach (var change in changes.OfType<JsonNode>())
        {
            var address = change["address"]?.ToString() ?? change["type"]?.ToString() ?? "a resource";
            if (change["importing"] is JsonValue i && i.TryGetValue<bool>(out var importing) && importing)
                return $"It imports {address}.";
            var actions = (change["actions"] as JsonArray ?? []).Select(a => a?.ToString()).ToList();
            if (actions.Count == 0 || actions.Any(a => a != "create"))
                return $"It would {string.Join(" and ", actions.Where(a => a is not null).Distinct())} {address}.";
        }

        return null;
    }

    /// <summary>
    /// Why a pipeline cannot be shown to only create, or null. Only a pipeline made entirely of OpenTofu stages, each plan
    /// checked change by change, qualifies: an Ansible stage's preview says what a playbook might change, never that it only
    /// creates, and the workflow's own <c>destructive</c> flag is not proof either. Decided by the owner.
    /// </summary>
    private static string? PipelineMoreThanCreate(Run run, RunContext context)
    {
        var ansible = (context.Stages ?? []).Where(s => s.Ansible is not null).Select(s => s.Name).ToList();
        if (ansible.Count > 0)
            return $"Stage {string.Join(", ", ansible)} runs Ansible, whose preview cannot be shown to only create.";
        foreach (var stage in context.Stages ?? [])
        {
            var plan = run.Steps.SingleOrDefault(s => s.Stage == stage.Name && s.Name == StepNames.Plan);
            if (plan is null)
                return $"Stage {stage.Name} has no plan.";
            if (MoreThanCreate(plan.Outputs is null ? null : JsonNode.Parse(plan.Outputs)) is { } why)
                return $"Stage {stage.Name}: {why}";
        }

        return null;
    }
}
