using System.Text.Json.Nodes;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// What a provision plan does to Proxmox guests, read from the plan step's outputs. A guest is a VM
/// or a container, and the rules are the same for both: removing one wipes its disks, so only its own
/// guest, found by VMID and node, can have its protection lifted.
/// </summary>
public static class GuestVmPlan
{
    public const string VmResourceType = "proxmox_virtual_environment_vm";
    public const string ContainerResourceType = "proxmox_virtual_environment_container";

    /// <summary>OpenTofu's reason for replacing a resource whose creation did not finish.</summary>
    private const string TaintedReason = "replace_because_tainted";

    /// <summary>What the run's guest is called in messages: "VM", or "container" for an LXC guest.</summary>
    public static string Noun(RunContext context) => IsContainer(context) ? "container" : "VM";

    private static bool IsContainer(RunContext context) => context.Proxmox.GuestKind == "lxc";

    /// <summary>The resource a guest of this kind is made of.</summary>
    private static string OwnType(RunContext context) => IsContainer(context) ? ContainerResourceType : VmResourceType;

    private static IEnumerable<JsonNode> Removals(JsonNode? outputs) =>
        (outputs?["changes"] as JsonArray ?? []).OfType<JsonNode>()
            .Where(c => c["type"]?.ToString() is VmResourceType or ContainerResourceType && Actions(c).Contains("delete"));

    private static List<string?> Actions(JsonNode change) =>
        (change["actions"] as JsonArray ?? []).Select(a => a?.ToString()).ToList();

    /// <summary>The state names this guest's own VMID on its own host, so the change is to the guest it owns.</summary>
    private static bool IsGuests(JsonNode change, RunContext context) =>
        change["type"]?.ToString() == OwnType(context)
        && change["vmId"] is JsonValue id && id.TryGetValue<int>(out var vmid) && vmid == context.Proxmox.Vmid
        && change["node"]?.ToString() == context.Proxmox.Node;

    /// <summary>The plan creates a resource of this guest's kind with nothing to replace, so the one it had is gone.</summary>
    public static bool CreatesGuest(JsonNode? outputs, RunContext context) =>
        (outputs?["changes"] as JsonArray ?? []).OfType<JsonNode>().Any(c => c["type"]?.ToString() == OwnType(context)
            && Actions(c) is var actions && actions.Contains("create") && !actions.Contains("delete"));

    /// <summary>Every VMID the plan names, on any resource, before or after the change.</summary>
    public static IEnumerable<int> NamedVmids(JsonNode? outputs) =>
        (outputs?["changes"] as JsonArray ?? []).OfType<JsonNode>()
            .SelectMany(c => (c["vmIds"] as JsonArray ?? []).Append(c["vmId"]))
            .Select(id => id is JsonValue value && value.TryGetValue<int>(out var vmid) ? vmid : (int?)null)
            .OfType<int>().Distinct();

    /// <summary>The plan deletes or replaces some Proxmox VM or container, its disks included.</summary>
    public static bool RemovesVm(JsonNode? outputs) => Removals(outputs).Any();

    /// <summary>The plan deletes or replaces the VM or container this guest owns. Nothing else justifies lifting its protection.</summary>
    public static bool RemovesGuestVm(JsonNode? outputs, RunContext context) =>
        Removals(outputs).Any(c => IsGuests(c, context));

    /// <summary>
    /// The only VM or container the plan removes is this guest's, and OpenTofu replaces it because creating it
    /// did not finish. A replacement for any other reason, such as an edit, does not count.
    /// </summary>
    public static bool RecreatesFailedVm(JsonNode? outputs, RunContext context) =>
        Removals(outputs).ToList() is { Count: > 0 } removals && removals.All(c =>
            IsGuests(c, context) && c["actionReason"]?.ToString() == TaintedReason && Actions(c).Contains("create"));
}
