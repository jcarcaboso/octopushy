using System.Text.Json.Nodes;

namespace Octopushy.Server.Orchestration;

/// <summary>What a provision plan does to Proxmox VMs, read from the plan step's outputs.</summary>
public static class GuestVmPlan
{
    public const string VmResourceType = "proxmox_virtual_environment_vm";
    public const string ContainerResourceType = "proxmox_virtual_environment_container";

    /// <summary>OpenTofu's reason for replacing a resource whose creation did not finish.</summary>
    private const string TaintedReason = "replace_because_tainted";

    private static IEnumerable<JsonNode> Removals(JsonNode? outputs) =>
        (outputs?["changes"] as JsonArray ?? []).OfType<JsonNode>()
            .Where(c => c["type"]?.ToString() == VmResourceType && Actions(c).Contains("delete"));

    private static List<string?> Actions(JsonNode change) =>
        (change["actions"] as JsonArray ?? []).Select(a => a?.ToString()).ToList();

    /// <summary>The state names this guest's own VMID on its own host, so the change is to the VM it owns.</summary>
    private static bool IsGuests(JsonNode change, RunContext context) =>
        change["vmId"] is JsonValue id && id.TryGetValue<int>(out var vmid) && vmid == context.Proxmox.Vmid
        && change["node"]?.ToString() == context.Proxmox.Node;

    /// <summary>
    /// The plan makes this guest's machine, new or as a replacement. It has to say so: a machine it
    /// cannot place on this VMID and host, or anything else it makes, does not count.
    /// </summary>
    public static bool MakesGuestMachine(JsonNode? outputs, RunContext context) =>
        (outputs?["changes"] as JsonArray ?? []).OfType<JsonNode>().Any(c =>
            c["type"]?.ToString() is VmResourceType or ContainerResourceType
            && Actions(c).Contains("create")
            && IsGuests(c, context));

    /// <summary>The plan makes a machine it cannot place: its VMID or host is not known until apply, or the agent did not say.</summary>
    public static bool MakesUnnamedMachine(JsonNode? outputs) =>
        (outputs?["changes"] as JsonArray ?? []).OfType<JsonNode>().Any(c =>
            c["type"]?.ToString() is VmResourceType or ContainerResourceType
            && Actions(c).Contains("create")
            && (c["vmId"] is null || c["node"] is null));

    /// <summary>The plan deletes or replaces some Proxmox VM, its disks included.</summary>
    public static bool RemovesVm(JsonNode? outputs) => Removals(outputs).Any();

    /// <summary>The plan deletes or replaces the VM this guest owns. Nothing else justifies lifting its protection.</summary>
    public static bool RemovesGuestVm(JsonNode? outputs, RunContext context) =>
        Removals(outputs).Any(c => IsGuests(c, context));

    /// <summary>
    /// The only VM the plan removes is this guest's, and OpenTofu replaces it because creating it did not finish.
    /// A replacement for any other reason, such as an edit, does not count.
    /// </summary>
    public static bool RecreatesFailedVm(JsonNode? outputs, RunContext context) =>
        Removals(outputs).ToList() is { Count: > 0 } removals && removals.All(c =>
            IsGuests(c, context) && c["actionReason"]?.ToString() == TaintedReason && Actions(c).Contains("create"));
}
