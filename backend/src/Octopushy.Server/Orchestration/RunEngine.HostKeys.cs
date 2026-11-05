using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
using Octopushy.Server.Store;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// A guest's SSH host key, when it is Octopushy's: made before the run that delivers it pins its credentials, renewed for
/// a new machine, and pinned once the apply that delivered it finishes. See <see cref="GuestHostCredentials"/>.
/// <para>
/// Nothing here saves on its own. A new key, the run's pinned version and the plan going back to pending are changes to
/// one unit of work, which the caller's single save commits or loses whole, so no state where one happened without the
/// others can be left behind.
/// </para>
/// </summary>
public sealed partial class RunEngine
{
    /// <summary>How many times a plan is made again for its host key before the run stops and asks a person.</summary>
    private const int HostKeyReplans = 2;

    /// <summary>A store that writes through <paramref name="db"/>, so what it adds commits with the caller's own changes.</summary>
    private static VariableStore StoreOver(OctopushyDb db, IServiceScope scope) =>
        ActivatorUtilities.CreateInstance<VariableStore>(scope.ServiceProvider, db);

    /// <summary>
    /// A run pins its credentials before its first step, so the guest's first host key has to exist by then. It is added to
    /// the same unit of work as the run, so the run and its key are saved together. A guest that has one keeps it; whether
    /// a plan carries it is decided once the plan shows what it makes.
    /// </summary>
    private async Task EnsureHostKeysAsync(OctopushyDb db, IEnumerable<Run> runs, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = StoreOver(db, scope);
        foreach (var run in runs.Where(r => r.Operation == Operations.Provision && !RunStatuses.IsTerminal(r.Status)))
        {
            if (RunContext.Parse(run.Context) is not { Snippets: not null } uploading)
                continue;
            await GuestHostCredentials.EnsureAsync(db, store, run.ResourceId,
                uploading.Proxmox.Name ?? run.ResourceId.ToString(), clock.GetUtcNow(), cancellationToken);
        }
    }

    /// <summary>
    /// A provision plan that succeeded and is not approved yet is asked once more whether its key fits, before anything
    /// offers it for approval or decides it by policy. A plan whose result was recorded before a crash, or whose key another
    /// run spent since, goes back to being planned here. True when it did.
    /// </summary>
    private async Task<bool> RecheckHostKeyAsync(OctopushyDb db, Run run, RunContext context, CancellationToken cancellationToken)
    {
        if (run.Operation != Operations.Provision || context.Snippets is null)
            return false;
        var plan = run.Steps.SingleOrDefault(s => s.Name == StepNames.Plan && s.Stage is null);
        var approval = run.Steps.SingleOrDefault(s => s.Name == StepNames.Approval && s.Stage is null);
        if (plan?.Status != StepStatuses.Succeeded || approval is null
            || approval.Status is not (StepStatuses.Pending or StepStatuses.Blocked or StepStatuses.AwaitingApproval))
            return false;
        return await ReplanForHostKeyAsync(db, run, plan, plan.Outputs is null ? null : JsonNode.Parse(plan.Outputs), cancellationToken);
    }

    /// <summary>
    /// Whether a provision plan has to be made again with another host key, which this then sets up: the plan and every step
    /// after it go back to pending, the run carries that key, and the approval that follows is of the new plan.
    /// <list type="bullet">
    /// <item>A plan that makes the guest's machine, new or as a replacement, must not give it a key another machine may hold:
    /// one an apply of another run carried, whether that apply finished, failed or was lost. It gets a new one.</item>
    /// <item>A plan that keeps the machine must not change the key it was given for one that was only ever planned: the
    /// snippet would hand the running machine another key at its next start, and nothing would pin it. It carries the
    /// delivered key again.</item>
    /// </list>
    /// Either way the pin moves only when an apply carrying the new key finishes, in the same save. See
    /// <see cref="GuestHostCredentials.Fit"/>.
    /// </summary>
    private async Task<bool> ReplanForHostKeyAsync(OctopushyDb db, Run run, Step step, JsonNode? outputs, CancellationToken cancellationToken)
    {
        var context = RunContext.Parse(run.Context);
        if (run.Operation != Operations.Provision || context.Snippets is null
            || !context.SecretVersions.TryGetValue(SecretNames.GuestHostKey, out var carried))
            return false;
        var keys = await db.GuestHostKeys.Where(k => k.ResourceId == run.ResourceId).ToListAsync(cancellationToken);
        var makesMachine = GuestVmPlan.MakesGuestMachine(outputs, context) || GuestVmPlan.MakesUnnamedMachine(outputs);
        var verdict = GuestHostCredentials.Fit(keys, carried, makesMachine, run.Id);
        var fit = verdict.Fit;
        if (fit == HostKeyFit.Fits)
        {
            // The plan that is kept says why it was made again.
            if (context.HostKeyReplan is { } note)
                step.Detail = note;
            return false;
        }

        if (fit == HostKeyFit.Reconcile)
        {
            step.Status = StepStatuses.Failed;
            step.Detail = $"This plan keeps the machine, and Octopushy cannot tell which of its host keys the machine holds: the plan carries "
                + $"version {carried}, and an apply since carried version {verdict.Target}, which may be on the machine instead. Nothing was planned "
                + "on a guess. Check which key the guest serves and plan again, or replace the VM, which gives it a new key.";
            return true;
        }

        var later = run.Steps.Where(s => s.Stage is null && s.Ordinal > step.Ordinal).ToList();
        if (later.Any(s => s.Attempts.Count > 0 || s.Status == StepStatuses.Succeeded && s.Kind == StepKinds.Agent))
        {
            // Too late to plan again: something after the plan already went out. The apply's own check refuses it.
            step.Status = StepStatuses.Failed;
            step.Detail = "This plan's host key no longer fits the machine it makes, and the run has gone past its plan. Plan again.";
            return true;
        }

        if (step.Attempts.Count(a => a.Status == AttemptStatuses.Succeeded) > HostKeyReplans)
        {
            step.Status = StepStatuses.Failed;
            step.Detail = "Each plan of this run changed whether it makes a new machine, so no host key fits it. Inspect the guest and plan again.";
            return true;
        }

        int next;
        string why;
        if (fit == HostKeyFit.Renew)
        {
            await using var scope = scopes.CreateAsyncScope();
            next = (await GuestHostCredentials.RenewAsync(db, StoreOver(db, scope), run.ResourceId,
                context.Proxmox.Name ?? run.ResourceId.ToString(), clock.GetUtcNow(), cancellationToken)).Version;
            why = "Planned again: this plan makes a new machine, and the host key the first plan carried may be on another machine already. "
                + "This plan carries a new host key, which is pinned once the apply that makes the machine finishes.";
        }
        else
        {
            next = verdict.Target!.Value;
            await GuestHostCredentials.RebaseAsync(db, run.ResourceId, next, clock.GetUtcNow(), cancellationToken);
            why = "Planned again: this plan keeps the machine, and the first plan carried a host key no apply ever carried. This plan "
                + $"carries version {next}, the newest key an apply carried, which the machine may hold even if that apply did not finish; "
                + "the pin moves to it once this apply finishes.";
        }

        logger.LogInformation("Run {Run} plans again with host key version {Version} of its guest", run.Id, next);
        context.SecretVersions[SecretNames.GuestHostKey] = next;
        run.Context = (context with { HostKeyReplan = why }).Serialize();
        step.Status = StepStatuses.Pending;
        step.Outputs = null;
        step.FinishedAt = null;
        step.Detail = why;
        // What came of the old plan is undone with it: an approval decided by policy, steps it skipped.
        foreach (var after in later)
        {
            after.Status = StepStatuses.Pending;
            after.Detail = null;
            after.Decision = null;
            after.DecidedBy = null;
            after.DecidedAt = null;
            after.DecisionNote = null;
            after.PlanSha256 = null;
            after.ApprovalRequired = false;
            after.StartedAt = null;
            after.FinishedAt = null;
        }

        if (run.Status == RunStatuses.AwaitingApproval)
            run.Status = RunStatuses.Running;
        return true;
    }

    /// <summary>An apply carrying the guest's host key finished: that version is the pin from now on.</summary>
    private static async Task RecordDeliveredHostKeyAsync(OctopushyDb db, Run run, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var context = RunContext.Parse(run.Context);
        if (context.Snippets is not null && context.SecretVersions.TryGetValue(SecretNames.GuestHostKey, out var version))
            await GuestHostCredentials.DeliveredAsync(db, run.ResourceId, version, run.Id, now, cancellationToken);
    }
}
