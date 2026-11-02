using Microsoft.EntityFrameworkCore;
using Octopushy.Definitions;
using Octopushy.Server.Data;

namespace Octopushy.Server.Orchestration;

/// <summary>What a guest and its services have to do with each other: the order they go in, and that they take turns.</summary>
public static class GuestWork
{
    /// <summary>The guest a guest or service belongs to, with its services.</summary>
    public static async Task<List<Guid>> FamilyAsync(OctopushyDb db, Guid resourceId, CancellationToken cancellationToken)
    {
        var resource = await db.Resources.AsNoTracking().Where(r => r.Id == resourceId)
            .Select(r => new { r.Kind, r.Address, r.ParentAddress }).FirstOrDefaultAsync(cancellationToken);
        if (resource?.Kind is not (ResourceKinds.Guest or ResourceKinds.Service))
            return [resourceId];
        var guest = resource.Kind == ResourceKinds.Service ? resource.ParentAddress : resource.Address;
        return await db.Resources.AsNoTracking()
            .Where(r => r.Address == guest && r.Kind == ResourceKinds.Guest || r.ParentAddress == guest && r.Kind == ResourceKinds.Service)
            .Select(r => r.Id).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Another run that is changing this run's guest, or may be: one with mutating work under way, or
    /// with an outcome nobody has closed. A run waiting for approval is not one, so a service
    /// nobody has approved holds back no other.
    /// </summary>
    public static async Task<Run?> HolderAsync(OctopushyDb db, Run run, CancellationToken cancellationToken)
    {
        var family = await FamilyAsync(db, run.ResourceId, cancellationToken);
        return await db.Runs.AsNoTracking()
            .Where(r => family.Contains(r.ResourceId) && r.Id != run.Id
                && r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled
                && (r.Status == RunStatuses.Uncertain || db.Attempts.Any(a => a.RunId == r.Id && a.Mutating
                    && (a.Status == AttemptStatuses.Queued || a.Status == AttemptStatuses.Assigned
                        || a.Status == AttemptStatuses.Running || a.Status == AttemptStatuses.Uncertain))))
            .OrderBy(r => r.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Why a guest's services cannot run yet, or null when it is ready for them: provision, then setup, then services.
    /// A service that waits is not failed; it says what it waits for.
    /// </summary>
    public static async Task<string?> ServicesWaitForAsync(OctopushyDb db, Resource guest, LabRepository repository, CancellationToken cancellationToken)
    {
        if (repository.Guest(guest.Address) is not { } definition)
            return "The guest is not in the current revision.";
        if (definition.Retired)
            return "The guest is retired.";
        if (guest.MissingSince is not null || guest.RetiredAt is not null || !guest.Valid)
            return "The guest's definition is missing or invalid.";
        var lastProvision = await db.Runs.AsNoTracking()
            .Where(r => r.ResourceId == guest.Id && r.Operation == Operations.Provision)
            .OrderByDescending(r => r.CreatedAt).Select(r => r.Status).FirstOrDefaultAsync(cancellationToken);
        if (guest.BoundVmid is null || guest.SyncedInputsHash != guest.DesiredInputsHash || lastProvision != RunStatuses.Succeeded)
            return "Waiting for the guest's provisioning to catch up with its definition.";
        if (definition.SetupRecipe is { } recipe && repository.Recipes.GetValueOrDefault(recipe)?.Configure?.OnChange is not null)
        {
            var setup = await db.GuestConfigurations.AsNoTracking().FirstOrDefaultAsync(c => c.ResourceId == guest.Id, cancellationToken);
            if (setup?.AppliedHash is null || setup.AppliedHash != setup.DesiredHash)
                return "Waiting for the guest's setup to be applied.";
        }

        return null;
    }
}
