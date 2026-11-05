using Microsoft.EntityFrameworkCore;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Store;

namespace Octopushy.Server.Orchestration;

/// <summary>What a guest and its services have to do with each other: the order they go in, and that they take turns.</summary>
public static class GuestWork
{
    /// <summary>The guest a guest or service belongs to, with its services. A resource of any other kind is alone.</summary>
    public static async Task<List<Guid>> FamilyAsync(OctopushyDb db, Guid resourceId, CancellationToken cancellationToken)
    {
        var resource = await db.Resources.AsNoTracking().Where(r => r.Id == resourceId)
            .Select(r => new { r.Kind, r.Address, r.ParentAddress }).FirstOrDefaultAsync(cancellationToken);
        if (resource?.Kind is not (ResourceKinds.Guest or ResourceKinds.Service))
            return [resourceId];
        var guest = resource.Kind == ResourceKinds.Service ? resource.ParentAddress : resource.Address;
        return await db.Resources.AsNoTracking()
            .Where(r => r.RetiredAt == null && (r.Address == guest && r.Kind == ResourceKinds.Guest || r.ParentAddress == guest && r.Kind == ResourceKinds.Service))
            .Select(r => r.Id).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Another run that is changing this run's guest, or may be: one with mutating work handed out, or with an
    /// outcome nobody has closed. A sibling's work that is queued counts too, so services go one after another.
    /// A guest's own run does not wait for queued service work, which may itself be waiting for that guest. A run
    /// waiting for approval is not a holder, so a service nobody has approved holds back no other.
    /// </summary>
    public static async Task<Run?> HolderAsync(OctopushyDb db, Run run, CancellationToken cancellationToken, bool dispatching = false)
    {
        var family = await FamilyAsync(db, run.ResourceId, cancellationToken);
        if (family.Count < 2)
            return null;
        var countQueued = !dispatching && !await db.Resources.AnyAsync(r => r.Id == run.ResourceId && r.Kind == ResourceKinds.Guest, cancellationToken);
        return await db.Runs.AsNoTracking()
            .Where(r => family.Contains(r.ResourceId) && r.Id != run.Id
                && r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled
                && (r.Status == RunStatuses.Uncertain || db.Attempts.Any(a => a.RunId == r.Id && a.Mutating
                    && (a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running || a.Status == AttemptStatuses.Uncertain
                        || countQueued && a.Status == AttemptStatuses.Queued && r.Status != RunStatuses.Blocked))))
            .OrderBy(r => r.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Why a guest's services cannot change anything yet, or null when it is ready for them: provision, then
    /// setup, then services. This is asked when a run is proposed and again each time one is about to change the
    /// guest, because what was true when a run was approved may not be true when its turn comes.
    /// </summary>
    public static async Task<string?> WaitingForAsync(OctopushyDb db, Resource guest, LabRepository repository, CancellationToken cancellationToken,
        ServiceDefinition? service = null)
    {
        if (repository.Guest(guest.Address) is not { } definition)
            return "The guest is not in the current revision.";
        if (definition.Retired)
            return "The guest is retired.";
        if (guest.MissingSince is not null || guest.RetiredAt is not null || !guest.Valid)
            return "The guest's definition is missing or invalid.";
        // A plan that was rejected, cancelled or refused changed nothing, so it says nothing about the machine. The
        // newest run that did decides: an apply that failed stays the last word until one works.
        var lastProvision = await db.Runs.AsNoTracking()
            .Where(r => r.ResourceId == guest.Id && r.Operation == Operations.Provision
                && (r.Status == RunStatuses.Succeeded || r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled
                    || db.Attempts.Any(a => a.RunId == r.Id && a.Mutating && a.Status != AttemptStatuses.Queued && a.Status != AttemptStatuses.Cancelled)))
            .OrderByDescending(r => r.CreatedAt).Select(r => r.Status).FirstOrDefaultAsync(cancellationToken);
        if (guest.BoundVmid is null || guest.SyncedInputsHash != guest.DesiredInputsHash || lastProvision != RunStatuses.Succeeded)
            return "Waiting for the guest's provisioning to catch up with its definition.";
        if (definition.Setup?.Configure?.OnChange is not null)
        {
            var setup = await db.GuestConfigurations.AsNoTracking().FirstOrDefaultAsync(c => c.ResourceId == guest.Id, cancellationToken);
            if (setup?.AppliedHash is null || setup.AppliedHash != setup.DesiredHash)
                return "Waiting for the guest's setup to be applied.";
        }

        var runsOnGuest = service?.Stages.Any(s => s.Definition.Executor == Executors.Ansible) ?? false;
        return runsOnGuest ? await OwnKeyRefusedAsync(db, guest.Id, definition, cancellationToken) : null;
    }

    /// <summary>What a run that would log in with Octopushy's key is told when there is no record the key reached the machine.</summary>
    public const string OwnKeyNotDelivered = "Octopushy has no record that its own key reached this machine: no successful apply of the guest's "
        + "provisioning has carried the public key, and no login with it has worked. Octopushy puts its key on a machine only that way, "
        + "so nothing that would log in with it starts. For a machine Octopushy did not create, name the key to log in with in access.sshKey. "
        + "A container takes the key only when it is created, so one made before it needed Octopushy's key has to be rebuilt.";

    /// <summary>
    /// Why Ansible work on this guest, from its workflow's setup stage or a service's stage alike, may not log in with Octopushy's key,
    /// or null. The key follows who manages the machine: Octopushy puts it on one only through an apply of the guest's
    /// provisioning, so where nothing shows that happened, a login with it is not tried. A key the guest names wins.
    /// </summary>
    public static async Task<string?> OwnKeyRefusedAsync(OctopushyDb db, Guid guestId, GuestDefinition definition, CancellationToken cancellationToken) =>
        !NamesOwnKey(definition) && !await GuestAccessCredentials.DeliveredAsync(db, guestId, cancellationToken) ? OwnKeyNotDelivered : null;

    /// <summary>Whether the guest names the key its Ansible work logs in with, so Octopushy's is not used.</summary>
    public static bool NamesOwnKey(GuestDefinition definition) => definition.Json["access"]?["sshKey"] is not null;
}
