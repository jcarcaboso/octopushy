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

    /// <summary>
    /// Why a guest may not be provisioned at <paramref name="vmid"/>, or null: the installation's latest
    /// observation shows a VM there that is not this guest's. A VM is the guest's own when the guest is bound
    /// to it, or when the server recorded that it sent an approved apply of this guest's plan creating that
    /// VMID on that node (<see cref="Resource.ClaimedVmid"/>). What a state says is never read as ownership.
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
        if (data is null)
            return null;
        var observed = (JsonNode.Parse(data)?["guests"] as JsonArray ?? []).OfType<JsonObject>()
            .FirstOrDefault(g => g["vmid"] is JsonValue id && id.TryGetValue<int>(out var observedVmid) && observedVmid == vmid);
        if (observed is null)
            return null;

        var host = observed["host"]?.ToString() is { Length: > 0 } h ? h : null;
        if (host is not null && guest.ClaimedVmid == vmid && guest.ClaimedInstallation == installationAddress && guest.ClaimedNode == host)
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

    private static string NormalEndpoint(string endpoint) => endpoint.Trim().TrimEnd('/');
}
