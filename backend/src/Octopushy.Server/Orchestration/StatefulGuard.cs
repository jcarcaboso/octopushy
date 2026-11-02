using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// What stops a plan before approval, whatever the policy says: no approval can pass it, so nobody is asked.
/// A guest marked <c>stateful</c> holds data that exists nowhere else, and Proxmox deletes a guest's volumes with
/// the guest. Once it has been deployed, a plan that would replace or delete it, or create it again, fails here.
/// Until then there is nothing to keep, so a first deployment that failed can still be retried, by the table of
/// rule 11. A VMID an installation reserves is not Octopushy's to change at all.
/// </summary>
public static class StatefulGuard
{
    /// <summary>
    /// Read from the definition last synced, not the run's revision: that one survives a deleted folder, and
    /// a removal built from an older revision must see that the marker was taken out since.
    /// </summary>
    public static bool Declared(Resource resource) =>
        resource.Definition is { } definition
        && JsonNode.Parse(definition)?["stateful"] is JsonValue marker && marker.TryGetValue<bool>(out var stateful) && stateful;

    /// <summary>
    /// A guest is deployed once a run verified it and, when it has setup or services, one of those has
    /// succeeded. A playbook that fails is run again, and until one has worked there is no data to keep.
    /// </summary>
    public static async Task<bool> ArmedAsync(OctopushyDb db, Resource resource, CancellationToken cancellationToken)
    {
        if (!Declared(resource) || resource.VerifiedAt is null)
            return false;
        var configured = JsonNode.Parse(resource.Definition!)?["setup"]?["recipe"] is not null
            || await db.Resources.AnyAsync(r => r.Kind == ResourceKinds.Service && r.ParentAddress == resource.Address, cancellationToken);
        if (!configured)
            return true;
        var family = await GuestWork.FamilyAsync(db, resource.Id, cancellationToken);
        return await db.Runs.AnyAsync(r => family.Contains(r.ResourceId) && r.Operation == Operations.Configure && r.Status == RunStatuses.Succeeded, cancellationToken);
    }

    /// <summary>Why this plan must not go on, or null when it may go on to the policy.</summary>
    public static async Task<string?> RefusalAsync(OctopushyDb db, Resource resource, JsonNode? outputs, RunContext context, CancellationToken cancellationToken)
    {
        var reserved = context.Proxmox.ReservedVmids is { Count: > 0 } vmids
            ? GuestVmPlan.NamedVmids(outputs).Where(vmids.Contains).ToList() : [];
        if (reserved.Count > 0)
            return $"This plan names VMID {string.Join(", ", reserved)}, which the installation reserves: Octopushy does not manage it. Nothing changed. Find what in the root reaches it.";

        if (!await ArmedAsync(db, resource, cancellationToken))
            return null;

        var noun = GuestVmPlan.Noun(context);
        var vmid = context.Proxmox.Vmid;
        var onPurpose = resource.MissingSince is null
            ? "To do this on purpose, take `stateful: true` out of its guest.yaml in a commit of its own, and plan again: that plan follows the destructive policy."
            : "To do this on purpose, restore the guest's folder, take `stateful: true` out of its guest.yaml in a commit, then delete the folder again.";
        if (GuestVmPlan.RemovesVm(outputs))
            return $"{resource.Address} is stateful, and this plan would destroy {noun} {vmid} with the data on it. Nothing changed, and no approval can let this plan through. Check the plan for what forces it. {onPurpose}";
        if (GuestVmPlan.CreatesGuest(outputs, context))
            return $"{resource.Address} is stateful, but {noun} {vmid} is gone: this plan would create it again, empty. Nothing changed. Restore it from a backup. {onPurpose}";
        return null;
    }
}
