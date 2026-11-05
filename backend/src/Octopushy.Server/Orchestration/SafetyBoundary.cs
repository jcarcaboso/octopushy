using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// What keeps Octopushy off machines it does not manage, apart from the refusal of plans in
/// <see cref="GuestVmPlan.ForeignChange"/>: installations it only observes (<c>manage: false</c>), and a
/// guest whose VMID is a VM it observes and does not manage. See docs/safety-boundary.md.
/// </summary>
public static class SafetyBoundary
{
    /// <summary>
    /// An installation on the denylist, with what would let work reach it. <paramref name="ObservedOnly"/> is
    /// true while it is observed only: nothing but its own observations may touch it. It is false for one that
    /// asks to be managed and has not been trusted again since: then its keys and endpoints are still refused
    /// to every other installation's work, and its own work waits for that trust.
    /// </summary>
    public sealed record ObservedOnlyInstallation(string Address, bool ObservedOnly, IReadOnlySet<string> StoreKeys, IReadOnlySet<string> Endpoints);

    /// <summary>Whether an installation's definition, as JSON, says <c>manage: false</c>.</summary>
    public static bool ObservedOnly(JsonNode? definition) =>
        definition?["manage"] is JsonValue manage && manage.TryGetValue<bool>(out var managed) && !managed;

    public static string ObservedOnlyReason(string installation) =>
        $"{installation} is observed only (manage: false): Octopushy runs nothing against it and releases its credentials only to observe it.";

    private static string ReservedReason(string installation) =>
        $"This work names a credential or endpoint of {installation}, which is observed only, or was and has not been trusted "
        + "again as managed. Octopushy releases them to nothing but that installation's own observations.";

    /// <summary>The store keys of the Proxmox tokens an installation definition declares. A CA certificate is no credential, and installations may share one.</summary>
    public static IEnumerable<string> TokenKeys(JsonNode? definition) =>
        new[] { "token", "readOnlyToken", "privilegedToken" }
            .Select(role => definition?["connection"]?[role]?["store"]?.ToString())
            .OfType<string>();

    /// <summary>The endpoints an installation definition declares, as they are compared.</summary>
    public static IEnumerable<string> Endpoints(JsonNode? definition) =>
        Git.RepositoryTrust.DeclaredEndpoints(definition).Select(NormalEndpoint);

    /// <summary>
    /// The denylist, as the server has recorded it on the installations: it outlives a definition that is
    /// invalid or missing, so breaking ziost's file does not take it off. See <see cref="Resource.ObservedOnly"/>.
    /// </summary>
    public static async Task<List<ObservedOnlyInstallation>> ObservedOnlyInstallationsAsync(OctopushyDb db, CancellationToken cancellationToken)
    {
        var installations = await db.Resources.AsNoTracking()
            .Where(r => r.Kind == ResourceKinds.Installation && (r.ObservedOnly || r.ObservedOnlyStoreKeys.Count > 0 || r.ObservedOnlyEndpoints.Count > 0))
            .Select(r => new { r.Address, r.ObservedOnly, r.ObservedOnlyStoreKeys, r.ObservedOnlyEndpoints })
            .ToListAsync(cancellationToken);
        return installations.Select(i => new ObservedOnlyInstallation(
                i.Address,
                i.ObservedOnly,
                i.ObservedOnlyStoreKeys.ToHashSet(StringComparer.Ordinal),
                i.ObservedOnlyEndpoints.Select(NormalEndpoint).ToHashSet(StringComparer.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>
    /// Why this run may not exist or go on because it would reach an installation on the denylist, or null.
    /// Its resource, its context's address and host, the store keys it names and the endpoint it reaches each
    /// count, so a run with a context that was altered after it was made is refused too, and so is a run of
    /// another installation that names an observed-only one's token.
    /// </summary>
    public static async Task<string?> ObservedOnlyRefusalAsync(OctopushyDb db, Run run, RunContext context, CancellationToken cancellationToken,
        IReadOnlyList<ObservedOnlyInstallation>? observedOnly = null)
    {
        observedOnly ??= await ObservedOnlyInstallationsAsync(db, cancellationToken);
        if (observedOnly.Count == 0)
            return null;
        var resource = await db.Resources.AsNoTracking().Where(r => r.Id == run.ResourceId).Select(r => r.Address).FirstOrDefaultAsync(cancellationToken);
        foreach (var installation in observedOnly)
        {
            bool Under(string? address) => address is not null
                && (address == installation.Address || address.StartsWith(installation.Address + "/", StringComparison.Ordinal));
            var own = Under(resource) || Under(context.Address) || Under(context.HostKey);
            if (installation.ObservedOnly && own)
                return ObservedOnlyReason(installation.Address);
            // An installation's own work may use its own keys once it is managed; trust holds it until then.
            if ((installation.ObservedOnly || !(Under(resource) && Under(context.Address))) && Names(installation, context))
                return installation.ObservedOnly ? ObservedOnlyReason(installation.Address) : ReservedReason(installation.Address);
        }

        return null;
    }

    /// <summary>
    /// Why an observation of the installation at <paramref name="address"/> may not be sent, or null: it names
    /// a credential or endpoint of another installation on the denylist. Its own observation always may.
    /// </summary>
    public static async Task<string?> ObservationRefusalAsync(OctopushyDb db, string? address, RunContext context, CancellationToken cancellationToken)
    {
        foreach (var installation in await ObservedOnlyInstallationsAsync(db, cancellationToken))
        {
            if ((address != installation.Address || context.Address != installation.Address) && Names(installation, context))
                return ReservedReason(installation.Address);
        }

        return null;
    }

    private static bool Names(ObservedOnlyInstallation installation, RunContext context) =>
        context.Secrets.Values.Any(installation.StoreKeys.Contains)
        || context.Proxmox.Endpoint is { } endpoint && installation.Endpoints.Contains(NormalEndpoint(endpoint));

    /// <summary>The tag a provision module puts on the VMs it makes, as the example module does.</summary>
    public const string OwnerTag = "octopushy";

    /// <summary>What the description of a VM made for the guest at <paramref name="address"/> contains.</summary>
    public static string OwnerDescription(string address) => $"Managed by Octopushy as {address}.";

    /// <summary>
    /// Whether the guest owns the VM at this VMID on this node: it is bound to it, after a confirmed creation, or
    /// it holds a reservation for exactly that machine that is still recoverable. Lifting protection and the
    /// VMID check both ask this. See docs/safety-boundary.md.
    /// </summary>
    public static async Task<bool> OwnsAsync(OctopushyDb db, Resource guest, string installation, string? node, int vmid, CancellationToken cancellationToken) =>
        guest.BoundVmid == vmid && guest.BoundInstallation == installation
        || node is not null && await RecoverableReservationAsync(db, guest, installation, node, vmid, cancellationToken);

    /// <summary>
    /// A reservation grants nothing on its own. It lets the guest try its own create again after an apply that
    /// did not finish, unless an observation from before the reservation already showed a VM at that VMID: then
    /// the create failed on a VM that was there first, and that VM is someone else's.
    /// </summary>
    public static async Task<bool> RecoverableReservationAsync(OctopushyDb db, Resource guest, string installation, string node, int vmid,
        CancellationToken cancellationToken)
    {
        if (guest.ReservedVmid != vmid || guest.ReservedInstallation != installation || guest.ReservedNode != node || guest.ReservedAt is not { } reservedAt)
            return false;
        var installationId = await db.Resources.AsNoTracking()
            .Where(r => r.Kind == ResourceKinds.Installation && r.Address == installation && r.RetiredAt == null)
            .Select(r => (Guid?)r.Id).FirstOrDefaultAsync(cancellationToken);
        var earlier = await db.Observations.AsNoTracking()
            .Where(o => o.ResourceId == installationId && o.ObservedAt < reservedAt)
            .Select(o => o.Data)
            .ToListAsync(cancellationToken);
        return !earlier.Any(data => Observed(data, vmid) is not null);
    }

    /// <summary>Whether the guest's reservation is for this run's machine. A reservation for any other grants nothing here.</summary>
    public static bool Reserves(Resource guest, RunContext context) =>
        guest.ReservedVmid is { } vmid && vmid == context.Proxmox.Vmid && guest.ReservedNode == context.Proxmox.Node
        && guest.ReservedInstallation == guest.ParentAddress;

    /// <summary>
    /// Why a verified VM does not confirm the creation an apply of this guest's reported, or null when it does:
    /// the VM verify-guest saw is on the guest's node, with Octopushy's tag and a description naming this guest.
    /// </summary>
    public static string? MarksMissing(JsonNode? verified, RunContext context)
    {
        var tags = (verified?["tags"] as JsonArray ?? []).Select(t => t?.ToString()).ToList();
        var description = verified?["description"]?.ToString() ?? "";
        return verified?["node"]?.ToString() == context.Proxmox.Node && tags.Contains(OwnerTag)
               && description.Contains(OwnerDescription(context.Address), StringComparison.Ordinal)
            ? null
            : $"{context.Proxmox.Machine} {context.Proxmox.Vmid} on {context.Proxmox.Node} does not carry Octopushy's tag '{OwnerTag}' and the description "
              + $"'{OwnerDescription(context.Address)}', so Octopushy does not record it as this guest's. Its provision module has to set both.";
    }

    /// <summary>
    /// Why a guest may not be provisioned or retired at <paramref name="vmid"/>, or null: the installation's latest
    /// observation shows a VM there that the guest does not own (<see cref="OwnsAsync"/>). What a state says is
    /// never read as ownership, and neither is a reservation an earlier observation contradicts.
    /// </summary>
    /// <remarks>
    /// Without an observation of the installation yet, nothing is refused here: there is nothing to compare
    /// with. Creating a VM at a VMID that exists fails at the provider anyway, and changing a machine that is
    /// not the guest's own is refused by <see cref="GuestVmPlan.ForeignChange"/> whatever is observed.
    /// </remarks>
    public static async Task<string?> VmidCollisionAsync(OctopushyDb db, Resource guest, string installationAddress, int vmid,
        CancellationToken cancellationToken)
    {
        if (guest.BoundVmid == vmid && guest.BoundInstallation == installationAddress)
            return null;
        var installationId = await db.Resources.AsNoTracking()
            .Where(r => r.Kind == ResourceKinds.Installation && r.Address == installationAddress && r.RetiredAt == null)
            .Select(r => (Guid?)r.Id).FirstOrDefaultAsync(cancellationToken);
        var data = installationId is null ? null : await db.Observations.AsNoTracking()
            .Where(o => o.ResourceId == installationId)
            .OrderByDescending(o => o.ObservedAt)
            .Select(o => o.Data)
            .FirstOrDefaultAsync(cancellationToken);
        if (data is null || Observed(data, vmid) is not { } observed)
            return null;

        var host = observed["host"]?.ToString() is { Length: > 0 } h ? h : null;
        if (host is not null && await RecoverableReservationAsync(db, guest, installationAddress, host, vmid, cancellationToken))
            return null;
        var node = host ?? "an unknown node";
        var holder = await db.Resources.AsNoTracking()
            .Where(r => r.Id != guest.Id && r.Kind == ResourceKinds.Guest && r.RetiredAt == null && r.BoundVmid == vmid && r.BoundInstallation == installationAddress)
            .Select(r => r.Address).FirstOrDefaultAsync(cancellationToken);
        if (holder is not null)
            return $"VMID {vmid} on {node} is the VM of {holder}, not of this guest.";
        var name = observed["name"]?.ToString() is { Length: > 0 } n ? $" ({n})" : "";
        return $"VMID {vmid} on {node} is an existing VM Octopushy does not manage{name}. Give this guest a free VMID: "
            + "Octopushy plans nothing against a machine it did not create.";
    }

    /// <summary>The VM an observation shows at this VMID, or null.</summary>
    private static JsonObject? Observed(string data, int vmid) =>
        (JsonNode.Parse(data)?["guests"] as JsonArray ?? []).OfType<JsonObject>()
            .FirstOrDefault(g => g["vmid"] is JsonValue id && id.TryGetValue<int>(out var observedVmid) && observedVmid == vmid);

    private static string NormalEndpoint(string endpoint) => endpoint.Trim().TrimEnd('/');
}
