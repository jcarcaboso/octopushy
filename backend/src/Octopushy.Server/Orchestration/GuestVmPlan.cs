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

    /// <summary>
    /// Why the plan may not go on because it deletes or replaces a Proxmox VM or container that is not
    /// <paramref name="context"/>'s own machine, or null. A provision or retire run owns exactly one machine,
    /// the VMID and node of its guest, as <see cref="IsGuests"/> decides; a pipeline stage owns none. A
    /// machine the plan cannot name is refused too, and so is a removal the plan does not list: what cannot
    /// be shown to be the guest's own is treated as someone else's. No approval or policy lifts this.
    /// See docs/safety-boundary.md.
    /// </summary>
    public static string? ForeignRemoval(JsonNode? outputs, RunContext context)
    {
        var changes = outputs?["changes"] as JsonArray;
        if (changes is null)
        {
            var counts = outputs?["counts"];
            return (counts?["delete"]?.GetValue<int>() ?? 0) + (counts?["replace"]?.GetValue<int>() ?? 0) > 0
                ? "The plan deletes or replaces resources without listing them, so it cannot be shown to leave other Proxmox machines alone. It was refused."
                : null;
        }

        var stage = context.Stage is not null;
        var foreign = changes.OfType<JsonNode>()
            .Where(c => c["type"]?.ToString() is VmResourceType or ContainerResourceType && Actions(c).Contains("delete"))
            .Where(c => stage || !IsGuests(c, context))
            .Select(Describe)
            .ToList();
        if (foreign.Count == 0)
            return null;
        var what = string.Join("; ", foreign);
        return stage
            ? $"Stage {context.Stage} would delete or replace a Proxmox machine ({what}). A pipeline stage owns no machine, so Octopushy refuses this plan whatever the policy says."
            : $"The plan would delete or replace a Proxmox machine that is not this guest's VM {context.Proxmox.Vmid} on {context.Proxmox.Node} ({what}). "
              + "Octopushy refuses to remove a machine it does not manage, whatever the policy says.";
    }

    /// <summary>A removal as a person can check it: what it is, and the VMID and node it names, or that it names none.</summary>
    private static string Describe(JsonNode change)
    {
        var vmid = change["vmId"] is JsonValue id && id.TryGetValue<int>(out var value) ? $"VMID {value}" : "an unknown VMID";
        var node = change["node"]?.ToString() is { Length: > 0 } name ? $"node {name}" : "an unknown node";
        var verb = Actions(change).Contains("create") ? "replaces" : "deletes";
        return $"{change["address"]?.ToString() ?? change["type"]?.ToString()} {verb} {vmid} on {node}";
    }

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
