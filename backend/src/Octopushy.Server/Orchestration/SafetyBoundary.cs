using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
using Octopushy.Server.Store;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// What keeps Octopushy off machines it does not manage, apart from the refusal of plans in
/// <see cref="GuestVmPlan.ForeignRemoval"/>: installations it only observes (<c>manage: false</c>), and a
/// guest whose VMID is a VM it observes and does not manage. See docs/safety-boundary.md.
/// </summary>
public static class SafetyBoundary
{
    /// <summary>An installation that says <c>manage: false</c>, with what would let a run reach it.</summary>
    public sealed record ObservedOnlyInstallation(string Address, IReadOnlySet<string> StoreKeys, IReadOnlySet<string> Endpoints);

    /// <summary>Whether an installation's definition, as JSON, says <c>manage: false</c>.</summary>
    public static bool ObservedOnly(JsonNode? definition) =>
        definition?["manage"] is JsonValue manage && manage.TryGetValue<bool>(out var managed) && !managed;

    public static string ObservedOnlyReason(string installation) =>
        $"{installation} is observed only (manage: false): Octopushy runs nothing against it and releases its credentials only to observe it.";

    /// <summary>
    /// The installations that say <c>manage: false</c> in the latest revision synced, with the store keys
    /// of their Proxmox tokens and their endpoints, so that a run naming any of them is refused too. A CA
    /// certificate is no credential, and installations may share one, so it does not count.
    /// </summary>
    public static async Task<List<ObservedOnlyInstallation>> ObservedOnlyInstallationsAsync(OctopushyDb db, CancellationToken cancellationToken)
    {
        var installations = await db.Resources.AsNoTracking()
            .Where(r => r.Kind == ResourceKinds.Installation && r.RetiredAt == null && r.Definition != null)
            .Select(r => new { r.Address, r.Definition })
            .ToListAsync(cancellationToken);
        var result = new List<ObservedOnlyInstallation>();
        foreach (var installation in installations)
        {
            var definition = JsonNode.Parse(installation.Definition!);
            if (!ObservedOnly(definition))
                continue;
            var connection = definition!["connection"];
            var keys = new[] { "token", "readOnlyToken", "privilegedToken" }
                .Select(role => connection?[role]?["store"]?.ToString())
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
            var endpoints = (definition["hosts"] as JsonObject ?? []).Select(h => h.Value?["endpoint"]?.ToString())
                .Append(connection?["endpoint"]?.ToString())
                .OfType<string>()
                .Select(NormalEndpoint)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            result.Add(new ObservedOnlyInstallation(installation.Address, keys, endpoints));
        }

        return result;
    }

    /// <summary>
    /// Why this run may not exist or go on because it would work in an installation Octopushy only observes,
    /// or null. Its resource, its context's address and host, the store keys it names and the endpoint it
    /// reaches each count, so a run with a context that was altered after it was made is refused too.
    /// </summary>
    public static async Task<string?> ObservedOnlyRefusalAsync(OctopushyDb db, Run run, RunContext context, CancellationToken cancellationToken,
        IReadOnlyList<ObservedOnlyInstallation>? observedOnly = null)
    {
        observedOnly ??= await ObservedOnlyInstallationsAsync(db, cancellationToken);
        if (observedOnly.Count == 0)
            return null;
        var resource = await db.Resources.AsNoTracking().Where(r => r.Id == run.ResourceId).Select(r => r.Address).FirstOrDefaultAsync(cancellationToken);
        var endpoint = context.Proxmox.Endpoint is { } e ? NormalEndpoint(e) : null;
        foreach (var installation in observedOnly)
        {
            bool Under(string? address) => address is not null
                && (address == installation.Address || address.StartsWith(installation.Address + "/", StringComparison.Ordinal));
            if (Under(resource) || Under(context.Address) || Under(context.HostKey)
                || context.Secrets.Values.Any(installation.StoreKeys.Contains)
                || endpoint is not null && installation.Endpoints.Contains(endpoint))
                return ObservedOnlyReason(installation.Address);
        }

        return null;
    }

    /// <summary>
    /// Why a guest may not be provisioned at <paramref name="vmid"/>, or null: the installation's latest
    /// observation shows a VM there that is not this guest's. A VM bound to the guest is its own, and so is
    /// one the guest's own OpenTofu state records, which an apply of it made but did not finish.
    /// </summary>
    /// <remarks>
    /// Without an observation of the installation yet, nothing is refused here: there is nothing to compare
    /// with. Creating a VM at a VMID that exists fails at the provider anyway, and the removal of a machine
    /// that is not the guest's own is refused by <see cref="GuestVmPlan.ForeignRemoval"/> whatever is observed.
    /// </remarks>
    public static async Task<string?> VmidCollisionAsync(OctopushyDb db, TofuStateStore states, Resource guest, string installationAddress, int vmid,
        CancellationToken cancellationToken, Guid? exceptRun = null)
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

        // Work of this guest's that may have made the VM and not reported yet: its result binds it.
        if (await db.Attempts.AsNoTracking().AnyAsync(a => a.Mutating && a.RunId != exceptRun
                && (a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running || a.Status == AttemptStatuses.Uncertain)
                && db.Runs.Any(r => r.Id == a.RunId && r.ResourceId == guest.Id), cancellationToken))
            return null;

        var node = observed["host"]?.ToString() is { Length: > 0 } host ? host : "an unknown node";
        var holder = await db.Resources.AsNoTracking()
            .Where(r => r.Id != guest.Id && r.Kind == ResourceKinds.Guest && r.RetiredAt == null && r.BoundVmid == vmid && r.BoundInstallation == installationAddress)
            .Select(r => r.Address).FirstOrDefaultAsync(cancellationToken);
        if (holder is not null)
            return $"VMID {vmid} on {node} is the VM of {holder}, not of this guest.";
        if (await states.RecordsMachineAsync(guest.Id, vmid, cancellationToken))
            return null;
        var name = observed["name"]?.ToString() is { Length: > 0 } n ? $" ({n})" : "";
        return $"VMID {vmid} on {node} is an existing VM Octopushy does not manage{name}. Give this guest a free VMID: "
            + "Octopushy plans nothing against a machine it did not create.";
    }

    private static string NormalEndpoint(string endpoint) => endpoint.Trim().TrimEnd('/');
}
