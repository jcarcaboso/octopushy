using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;

namespace Octopushy.Server.Orchestration;

/// <summary>What is particular to a configure run: approval has no plan to read, and a playbook can report an outcome.</summary>
public sealed partial class RunEngine
{
    /// <summary>
    /// Applies the policy to a preview, or waits for a person on a declared outcome. Nothing shows
    /// what a playbook will change, so every run is treated as a change, and the recipe's
    /// declaration stands in for the plan's count of deletes.
    /// </summary>
    private bool DecideConfigureApproval(Run run, Step step, RunContext context)
    {
        var preview = run.Steps.Single(s => s.Name == StepNames.Preview);
        var digest = preview.Outputs is null ? null : JsonNode.Parse(preview.Outputs)?["planSha256"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(digest))
        {
            step.Status = StepStatuses.Failed;
            step.Detail = "The preview has no saved document for an approval to name.";
            Finish(run, RunStatuses.Failed, step.Detail);
            return false;
        }

        step.PlanSha256 = digest;
        var ansible = context.Ansible!;
        var policy = context.Policy!;
        step.StartedAt ??= clock.GetUtcNow();
        if (step.Name == StepNames.OutcomeApproval)
        {
            var reported = OutcomeOf(run);
            step.ApprovalRequired = true;
            step.Status = StepStatuses.AwaitingApproval;
            var vars = string.Join(", ", reported?.Declared.ApproveVars?.Select(p => p.Key) ?? []);
            step.Detail = $"The playbook reported {reported?.Name}"
                + (reported?.Detail is { } detail ? $": {detail}." : ".")
                + (reported?.Declared.Description is { } description ? $" {description}" : "")
                + $" Approving runs {ansible.Action} once more with {vars}.";
            return false;
        }

        var manual = policy.Approval == "manual";
        var destructiveManual = ansible.Destructive && policy.Destructive == "manual";
        step.ApprovalRequired = manual || destructiveManual;
        if (!step.ApprovalRequired)
        {
            step.Decision = "approved";
            step.DecidedBy = "policy";
            step.DecidedAt = clock.GetUtcNow();
            step.DecisionNote = ansible.Destructive
                ? $"Approval and destructive are automatic (approval set by {policy.ApprovalSetBy}, destructive {policy.DestructiveSetBy})."
                : $"Approval is automatic (set by {policy.ApprovalSetBy}).";
            step.Status = StepStatuses.Succeeded;
            step.FinishedAt = clock.GetUtcNow();
            return true;
        }

        step.Status = StepStatuses.AwaitingApproval;
        step.Detail = destructiveManual && !manual
            ? $"The recipe declares {ansible.Action} destructive and destructive is manual (set by {policy.DestructiveSetBy})."
            : $"Approval is manual (set by {policy.ApprovalSetBy}).";
        return false;
    }

    /// <summary>What the latest playbook step reported, if it was a declared outcome.</summary>
    internal static ReportedOutcome? OutcomeOf(Run run)
    {
        var context = RunContext.Parse(run.Context).Ansible!;
        var step = run.Steps.Where(s => s.Name is StepNames.Playbook or StepNames.PlaybookApproved && s.Outputs is not null)
            .MaxBy(s => s.Ordinal);
        var outputs = step?.Outputs is null ? null : JsonNode.Parse(step.Outputs);
        return outputs?["outcome"]?.GetValue<string>() is { } name && context.Outcomes.TryGetValue(name, out var declared)
            ? new ReportedOutcome(name, outputs["detail"]?.GetValue<string>(), declared)
            : null;
    }

    internal sealed record ReportedOutcome(string Name, string? Detail, AnsibleOutcome Declared);

    private string? ConfiguredDetail(Run run) =>
        OutcomeOf(run) is { } reported ? $"The playbook reported {reported.Name}{(reported.Detail is { } detail ? $": {detail}" : "")}." : null;

    /// <summary>
    /// Settles a finished playbook. Success promotes what was prepared. A declared outcome waits
    /// for a person if the recipe lets one approve it, once, and otherwise ends as it declares.
    /// </summary>
    private async Task ApplyConfigureEffectsAsync(OctopushyDb db, Run run, Step step, CancellationToken cancellationToken)
    {
        var context = RunContext.Parse(run.Context).Ansible!;
        var reported = OutcomeOf(run);
        var waits = reported?.Declared.ApproveVars is not null && step.Name == StepNames.Playbook;
        if (!waits)
        {
            foreach (var later in run.Steps.Where(s => s.Ordinal > step.Ordinal))
            {
                later.Status = StepStatuses.Skipped;
                later.Detail = reported is null ? "The playbook finished." : "The playbook reported an outcome that ends the run.";
            }
        }

        if (reported is not null)
        {
            var approvedOnce = reported.Declared.ApproveVars is not null && step.Name == StepNames.PlaybookApproved;
            if (approvedOnce || !waits && reported.Declared.Result == "failed")
            {
                step.Status = StepStatuses.Failed;
                step.Detail = $"It reported {reported.Name}{(reported.Detail is { } detail ? $": {detail}" : "")}"
                    + (approvedOnce ? ", after the one approved attempt." : ".");
                return;
            }

            if (waits)
                return;
        }

        var configuration = await db.GuestConfigurations.FirstOrDefaultAsync(c => c.ResourceId == run.ResourceId, cancellationToken);
        if (configuration is null)
            return;
        configuration.AppliedHash = run.InputsHash;
        configuration.AppliedRevisionId = run.RevisionId;
        configuration.AppliedAt = clock.GetUtcNow();
        configuration.AppliedAction = context.Action;
        var prepared = run.Steps.Single(s => s.Name == StepNames.Preview).Outputs is { } text ? JsonNode.Parse(text)?["prepared"] : null;
        if (prepared is JsonObject)
            configuration.Snapshot = prepared.ToJsonString();
    }

    /// <summary>A provision run that created or replaced something may have made a new machine, which has to be configured.</summary>
    private static async Task ProposeConfigurationAgainAsync(OctopushyDb db, Run run, CancellationToken cancellationToken)
    {
        var counts = run.Steps.Single(s => s.Name == StepNames.Plan).Outputs is { } text ? JsonNode.Parse(text)?["counts"] : null;
        if ((counts?["create"]?.GetValue<int>() ?? 0) + (counts?["replace"]?.GetValue<int>() ?? 0) == 0)
            return;
        var configuration = await db.GuestConfigurations.FirstOrDefaultAsync(c => c.ResourceId == run.ResourceId, cancellationToken);
        if (configuration is not null)
            configuration.ProposedHash = null;
    }
}
