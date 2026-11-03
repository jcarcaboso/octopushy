using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Store;

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

        if (StepNames.IsAccessDecision(step.Name))
        {
            step.ApprovalRequired = true;
            step.Status = StepStatuses.AwaitingApproval;
            step.Detail = step.Name == StepNames.PromoteKey
                ? "A fresh login with the candidate key alone worked, with the privileges a run needs. Promoting makes it the key runs use on this guest, and no other."
                : "Octopushy now uses the new key. Approving removes the old one from the guest. Other keys there are not touched.";
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
    /// Whether a scheduled run has lost the right to start changing its guest. It has that right
    /// until its limit, counted from its time and not from when it was started: one starting
    /// hours late would run at a time nobody chose. What has gone out ends the usual way, and a
    /// person who is deciding, or has decided, overrides the limit: the time is then theirs.
    /// </summary>
    internal static bool MissedItsTime(Run run, DateTimeOffset now) =>
        run.Reason == RunReasons.Schedule
        && !RunStatuses.IsTerminal(run.Status)
        && run.Status != RunStatuses.Uncertain
        && (RunContext.Parse(run.Context).ScheduledFor ?? run.CreatedAt) + Planner.ScheduleLateLimit < now
        && !run.Steps.Any(s => s.Status == StepStatuses.AwaitingApproval || s is { ApprovalRequired: true, Decision: "approved" })
        && !run.Steps.SelectMany(s => s.Attempts).Any(a => a.Mutating && a.Status != AttemptStatuses.Queued);

    /// <summary>
    /// Cancels a scheduled run that has missed its time. Every release of a change comes through
    /// here, so nothing depends on how recently the periodic pass ran. A release passes the time
    /// it will record, read after the work that leads up to it, so that time is what gets decided.
    /// </summary>
    internal async Task<bool> StopIfLateAsync(OctopushyDb db, Run run, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!MissedItsTime(run, now))
            return false;
        await CancelInternalAsync(db, run.Id, "This did not start within an hour of its time, so nothing reached the guest. The next time tries again.", cancellationToken);
        return true;
    }

    /// <summary>
    /// Settles a finished playbook. Success promotes what was prepared. A declared outcome waits
    /// for a person if the recipe lets one approve it, once, and otherwise ends as it declares:
    /// a declared success promotes like any other, and a declared failure promotes nothing.
    /// A scheduled run never waits on an approvable one: holding the guest for a person would
    /// block every later run, so it ends as succeeded, promotes nothing, and the next time tries
    /// again. An outcome no person can approve has nothing to wait for, so it ends as declared
    /// on a scheduled run too.
    /// </summary>
    private async Task ApplyConfigureEffectsAsync(OctopushyDb db, Run run, Step step, CancellationToken cancellationToken)
    {
        var context = RunContext.Parse(run.Context).Ansible!;
        var reported = OutcomeOf(run);
        var approvable = reported?.Declared.ApproveVars is not null;
        var scheduled = approvable && run.Reason == RunReasons.Schedule;
        var waits = approvable && step.Name == StepNames.Playbook && !scheduled;
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
            if (scheduled)
            {
                step.Detail = $"It reported {reported.Name}. A scheduled run does not wait for a person, so the next time tries again.";
                return;
            }

            var approvedOnce = approvable && step.Name == StepNames.PlaybookApproved;
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

        await RecordKeyUseAsync(db, run, step, cancellationToken);
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

    /// <summary>What each finished step of a rotation shows. A change is recorded when a login has shown it, not when it was asked for.</summary>
    private async Task ApplyRotationEffectsAsync(OctopushyDb db, Run run, Step step, JsonNode? outputs, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rotation = RunContext.Parse(run.Context).AccessRotation!;
        var keys = await db.GuestAccessKeys.Where(k => k.ResourceId == run.ResourceId).ToListAsync(cancellationToken);
        switch (step.Name)
        {
            case StepNames.AuthorizeKey:
                keys.Single(k => k.Version == rotation.Candidate).AuthorizedAt = now;
                break;
            case StepNames.VerifyCurrent or StepNames.VerifyKey:
                var verified = keys.Single(k => k.Version == (step.Name == StepNames.VerifyKey ? rotation.Candidate : rotation.Current));
                verified.VerifiedAt = now;
                verified.VerifiedRunId = run.Id;
                break;
            case StepNames.ConfirmRefused:
                // Only sshd refusing them counts. Until then they stay on the list, so a later
                // rotation looks again, and takes off whatever is still there.
                if (outputs?["refusedKeys"]?.GetValue<int>() != rotation.Remove.Count)
                {
                    step.Status = StepStatuses.Failed;
                    step.Detail = "Nothing showed sshd refusing every old key.";
                    if (step.Attempts.MaxBy(a => a.Number) is { } attempt)
                    {
                        attempt.Status = AttemptStatuses.Failed;
                        attempt.Error = step.Detail;
                    }

                    return;
                }

                foreach (var removed in keys.Where(k => rotation.Remove.Contains(k.Version)))
                {
                    removed.RemovedAt = now;
                }

                await GuestAccessCredentials.RevokeRemovedAsync(db, run.ResourceId, rotation, now, cancellationToken);
                break;
        }
    }

    /// <summary>A person's decision, on the record with what it rested on.</summary>
    private async Task PromoteRotatedKeyAsync(OctopushyDb db, Guid runId, string user, string? note, CancellationToken cancellationToken)
    {
        var run = await LoadRunAsync(db, runId, cancellationToken);
        var verification = run.Steps.Single(s => s.Name == StepNames.VerifyKey).Attempts.MaxBy(a => a.Number)!;
        var evidence = $"Run #{run.Number}, attempt {verification.Id}: a fresh login with this key alone, finished {verification.FinishedAt:u}."
            + (string.IsNullOrWhiteSpace(note) ? "" : $" {note.Trim()}");
        await GuestAccessCredentials.PromoteAsync(db, run.ResourceId, RunContext.Parse(run.Context).AccessRotation!, user, evidence, clock.GetUtcNow(), cancellationToken);
    }

    /// <summary>An apply carrying Octopushy's key finished. Whether it reached the machine is for a login to show.</summary>
    private static async Task RecordProvisionedKeyAsync(OctopushyDb db, Run run, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!RunContext.Parse(run.Context).ManagedAccess)
            return;
        var key = await db.GuestAccessKeys.FirstOrDefaultAsync(k => k.ResourceId == run.ResourceId && k.Bootstrap, cancellationToken);
        if (key is not null)
            key.ProvisionedAt = now;
    }

    /// <summary>A run holding this key finished having done something. That is use, not proof of a login.</summary>
    private async Task RecordKeyUseAsync(OctopushyDb db, Run run, Step step, CancellationToken cancellationToken)
    {
        var context = RunContext.Parse(run.Context).ForStep(step);
        var guest = context.GuestResourceId ?? run.ResourceId;
        if (context.Secrets.GetValueOrDefault(SecretNames.AnsibleSshKey) != GuestAccessCredentials.Key(guest)
            || !context.SecretVersions.TryGetValue(SecretNames.AnsibleSshKey, out var version))
            return;
        var recap = step.Outputs is { } outputs ? JsonNode.Parse(outputs)?["recap"] : null;
        if ((recap?["ok"]?.GetValue<int>() ?? 0) + (recap?["changed"]?.GetValue<int>() ?? 0) == 0)
            return;
        var key = await db.GuestAccessKeys.FirstOrDefaultAsync(k => k.ResourceId == guest && k.Version == version, cancellationToken);
        if (key is not null)
        {
            key.LastUsedAt = clock.GetUtcNow();
            key.LastUsedRunId = run.Id;
        }
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
