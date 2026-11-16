using System.Text.Json.Nodes;
using Octopushy.Server.Data;

namespace Octopushy.Server.Inventory;

public static class MachineDriftKinds
{
    /// <summary>The installation's latest observation lists no machine at the VMID the guest is bound to.</summary>
    public const string Missing = "missing";

    /// <summary>The machine the guest is bound to is on another host of its installation than its definition says.</summary>
    public const string Moved = "moved";
}

/// <summary>
/// Where a guest's own machine is not what Octopushy expects, as the installation's latest observation shows it. It is
/// evidence for a person, not a decision: nothing is planned, retired or written from it.
/// </summary>
public sealed record MachineDrift(string Kind, int Vmid, string? DeclaredHost, string? ObservedHost, DateTimeOffset ObservedAt, string Detail)
{
    /// <summary>
    /// The drift of a guest bound to a machine, or null when there is none or the observation cannot tell. Only an
    /// observation made after the guest was bound counts, and only one that proves it saw every VM, of a cluster that was
    /// quorate: a VM the token may not audit, or a node a minority partition cannot see, is not missing.
    /// </summary>
    public static MachineDrift? Of(Resource guest, string? declaredHost, JsonObject? observation)
    {
        if (guest.RetiredAt is not null || guest.BoundVmid is not { } vmid || guest.BoundInstallation != guest.ParentAddress
            || guest.BoundAt is not { } boundAt || observation is null)
            return null;
        if (observation["observedAt"] is not JsonValue at || !at.TryGetValue<DateTimeOffset>(out var observedAt) || observedAt <= boundAt)
            return null;
        if (observation["vmVisibility"]?["complete"] is not JsonValue complete || !complete.TryGetValue<bool>(out var sawAll) || !sawAll)
            return null;
        if (!observation.ContainsKey("cluster"))
            return null;
        if (observation["cluster"] is JsonObject cluster
            && (cluster["quorate"] is not JsonValue q || !q.TryGetValue<bool>(out var quorate) || !quorate))
            return null;
        if (observation["guests"] is not JsonArray guests)
            return null;

        var machine = guests.OfType<JsonObject>().FirstOrDefault(g => g["vmid"] is JsonValue v && v.TryGetValue<int>(out var id) && id == vmid);
        var when = observedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
        if (machine is null)
            return new MachineDrift(MachineDriftKinds.Missing, vmid, declaredHost, null, observedAt,
                $"VMID {vmid}, which this guest was created at, was not on any host at the last observation ({when}). If it was deleted by "
                + $"mistake, restore it from a backup at VMID {vmid}. A plan that would create it again waits for a person.");

        var observedHost = machine["host"]?.ToString();
        if (declaredHost is not null && observedHost is { Length: > 0 } && observedHost != declaredHost)
            return new MachineDrift(MachineDriftKinds.Moved, vmid, declaredHost, observedHost, observedAt,
                $"VMID {vmid} is on {observedHost} at the last observation ({when}), and the definition places it on {declaredHost}. "
                + $"A plan of the guest would move it back to {declaredHost}, and waits for a person. Accept placement records host: {observedHost}.");
        return null;
    }
}
