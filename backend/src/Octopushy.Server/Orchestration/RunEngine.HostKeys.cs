using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
using Octopushy.Server.Store;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// A guest's SSH host key, when it is Octopushy's: made before the run that delivers it pins its credentials, renewed for
/// a new machine, and pinned once the apply that delivered it finishes. See <see cref="GuestHostCredentials"/>.
/// </summary>
public sealed partial class RunEngine
{
    /// <summary>How many times a plan is made again for its host key before the run stops and asks a person.</summary>
    private const int HostKeyReplans = 2;

    /// <summary>A store that writes through <paramref name="db"/>, so what it saves commits with the caller's own changes.</summary>
    private VariableStore StoreOver(OctopushyDb db, IServiceScope scope) =>
        ActivatorUtilities.CreateInstance<VariableStore>(scope.ServiceProvider, db);

    /// <summary>
    /// A run pins its credentials before its first step, so the guest's first host key has to exist by then. A guest that
    /// has one keeps it; whether a plan carries it is decided once the plan shows what it makes.
    /// </summary>
    private async Task EnsureHostKeysAsync(OctopushyDb db, IEnumerable<Run> runs, CancellationToken cancellationToken)
    {
        foreach (var run in runs.Where(r => r.Operation == Operations.Provision && !RunStatuses.IsTerminal(r.Status)))
        {
            if (RunContext.Parse(run.Context) is not { Snippets: not null } uploading)
                continue;
            await using var scope = scopes.CreateAsyncScope();
            await GuestHostCredentials.EnsureAsync(db, StoreOver(db, scope), run.ResourceId,
                uploading.Proxmox.Name ?? run.ResourceId.ToString(), clock.GetUtcNow(), cancellationToken);
        }
    }

    /// <summary>
    /// Whether a provision plan has to be made again with another host key, which this then sets up: the step goes back
    /// to pending, carrying that key, and the approval that follows is of the new plan.
    /// <list type="bullet">
    /// <item>A plan that makes the guest's machine, new or as a replacement, must not give it a key an earlier machine was
    /// given: that key may be on the old machine's disks, its cloud-init drive or a backup. It gets a new one.</item>
    /// <item>A plan that keeps the machine must not change the key it was given: the snippet would hand the running
    /// machine another key at its next start, and nothing would pin it. It carries the delivered key again.</item>
    /// </list>
    /// Either way the pin moves only when an apply carrying the new key finishes, in the same save.
    /// </summary>
    private async Task<bool> ReplanForHostKeyAsync(OctopushyDb db, Run run, Step step, JsonNode? outputs, CancellationToken cancellationToken)
    {
        var context = RunContext.Parse(run.Context);
        if (run.Operation != Operations.Provision || context.Snippets is null
            || !context.SecretVersions.TryGetValue(SecretNames.GuestHostKey, out var carried))
            return false;
        var keys = await db.GuestHostKeys.AsNoTracking().Where(k => k.ResourceId == run.ResourceId).ToListAsync(cancellationToken);
        var pinned = keys.Where(k => k.DeliveredAt is not null).MaxBy(k => k.Version);
        var makesMachine = GuestVmPlan.MakesGuestMachine(outputs, context) || GuestVmPlan.MakesUnnamedMachine(outputs);
        var renew = makesMachine && keys.Any(k => k.Version == carried && k.DeliveredAt is not null);
        var rebase = !makesMachine && pinned is not null && pinned.Version != carried;
        if (!renew && !rebase)
        {
            // The plan that is kept says why it was made again.
            if (context.HostKeyReplan is { } note)
                step.Detail = note;
            return false;
        }

        if (step.Attempts.Count(a => a.Status == AttemptStatuses.Succeeded) > HostKeyReplans)
        {
            step.Status = StepStatuses.Failed;
            step.Detail = "Each plan of this run changed whether it makes a new machine, so no host key fits it. Inspect the guest and plan again.";
            return true;
        }

        int next;
        string why;
        if (renew)
        {
            await using var scope = scopes.CreateAsyncScope();
            next = (await GuestHostCredentials.RenewAsync(db, StoreOver(db, scope), run.ResourceId,
                context.Proxmox.Name ?? run.ResourceId.ToString(), clock.GetUtcNow(), cancellationToken)).Version;
            why = "Planned again: this plan makes a new machine, and the host key the first plan carried was given to an earlier one. "
                + "This plan carries a new host key, which is pinned once the apply that makes the machine finishes.";
        }
        else
        {
            next = pinned!.Version;
            await GuestHostCredentials.RebaseAsync(db, run.ResourceId, next, clock.GetUtcNow(), cancellationToken);
            why = "Planned again: this plan keeps the machine, and the first plan carried a host key the machine was never given. "
                + "This plan carries the key it has.";
        }

        logger.LogInformation("Run {Run} plans again with host key version {Version} of its guest", run.Id, next);
        context.SecretVersions[SecretNames.GuestHostKey] = next;
        run.Context = (context with { HostKeyReplan = why }).Serialize();
        step.Detail = why;
        step.Status = StepStatuses.Pending;
        step.Outputs = null;
        step.FinishedAt = null;
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
