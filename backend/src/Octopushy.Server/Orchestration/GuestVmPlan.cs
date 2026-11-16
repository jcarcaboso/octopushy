using System.Text.Json.Nodes;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// What a provision plan does to Proxmox machines, VMs and containers alike, read from the plan step's outputs. A
/// container's VMID is in the same namespace as a VM's, and replacing either wipes its disks, so every rule here is the same
/// for both.
/// </summary>
public static class GuestVmPlan
{
    public const string VmResourceType = "proxmox_virtual_environment_vm";
    public const string ContainerResourceType = "proxmox_virtual_environment_container";

    /// <summary>OpenTofu's reason for replacing a resource whose creation did not finish.</summary>
    private const string TaintedReason = "replace_because_tainted";

    private static IEnumerable<JsonNode> Removals(JsonNode? outputs) =>
        (outputs?["changes"] as JsonArray ?? []).OfType<JsonNode>()
            .Where(c => IsMachine(c) && Actions(c).Contains("delete"));

    /// <summary>A Proxmox VM or container: what a guest's machine stage makes.</summary>
    private static bool IsMachine(JsonNode change) => change["type"]?.ToString() is VmResourceType or ContainerResourceType;

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
            IsMachine(c)
            && Actions(c).Contains("create")
            && IsGuests(c, context));

    /// <summary>The plan makes a machine it cannot place: its VMID or host is not known until apply, or the agent did not say.</summary>
    public static bool MakesUnnamedMachine(JsonNode? outputs) =>
        (outputs?["changes"] as JsonArray ?? []).OfType<JsonNode>().Any(c =>
            IsMachine(c)
            && Actions(c).Contains("create")
            && (c["vmId"] is null || c["node"] is null));

    /// <summary>
    /// Proxmox resource types that never act on a VM or container: files on a node's datastore. Only these may
    /// change without naming the run's own machine. Anything else of a Proxmox provider has to name it.
    /// See docs/safety-boundary.md.
    /// </summary>
    public static readonly IReadOnlySet<string> MachineFreeTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "proxmox_virtual_environment_file",
        "proxmox_virtual_environment_download_file",
        "proxmox_download_file",
    };

    /// <summary>A VM, or a node, that a change names; either may be unknown.</summary>
    private sealed record Target(int? Vmid, string? Node);

    /// <summary>
    /// Why the plan may not go on because it reaches past <paramref name="context"/>'s own machine, or null. A
    /// provision or retire run owns exactly one machine, the VMID and node of its guest; a pipeline stage owns
    /// none. Refused, before any approval and whatever the policy says:
    /// <list type="bullet">
    /// <item>any change that imports an existing resource;</item>
    /// <item>a Proxmox resource that is created, updated, deleted or replaced and names any VM but the run's
    /// own, names its own VMID without its node, or, for a stage, names a VM at all;</item>
    /// <item>one that names no VM, or one not known until apply, unless its type is in <see cref="MachineFreeTypes"/>;</item>
    /// <item>a plan that counts changes without listing them.</item>
    /// </list>
    /// One exception: the guest's own machine moving to its node from another host of its installation, as an update of
    /// that machine alone, when the guest is <paramref name="bound"/> to its VMID. A VMID is one machine in an installation,
    /// whichever host it is on, and only the guest's binding says the machine is its own.
    /// What cannot be shown to be the guest's own is treated as someone else's. See docs/safety-boundary.md.
    /// </summary>
    /// <param name="bound">The run's guest is bound to its VMID in its installation; see <see cref="SafetyBoundary.OwnsAsync"/>.</param>
    public static string? ForeignChange(JsonNode? outputs, RunContext context, bool bound = false)
    {
        var changes = outputs?["changes"] as JsonArray;
        if (changes is null)
        {
            var counts = outputs?["counts"] as JsonObject;
            return (counts ?? []).Any(c => c.Value is JsonValue v && v.TryGetValue<int>(out var n) && n > 0)
                ? "The plan counts changes without listing them, so it cannot be shown to leave other Proxmox machines alone. It was refused."
                : null;
        }

        var listed = changes.OfType<JsonNode>().ToList();
        var imports = listed.Where(c => c["importing"] is JsonValue i && i.TryGetValue<bool>(out var importing) && importing)
            .Select(c => c["address"]?.ToString() ?? c["type"]?.ToString() ?? "a resource").ToList();
        if (imports.Count > 0)
            return $"The plan imports an existing resource ({string.Join(", ", imports)}). Octopushy refuses imports: what it would adopt "
                + "is something it did not create, whatever the policy says.";

        var stage = context.Stage is not null;
        var foreign = new List<string>();
        foreach (var change in listed.Where(c => IsProxmox(c) && Mutates(c)))
        {
            var targets = Targets(change);
            var machines = targets.Where(t => t.Vmid is not null).ToList();
            var freeType = MachineFreeTypes.Contains(change["type"]?.ToString() ?? "");
            var unknown = change["targetUnknown"] is JsonValue u && u.TryGetValue<bool>(out var isUnknown) && isUnknown;
            if (!stage && bound && !unknown && MovesOwnMachine(change, machines, context))
                continue;
            if (!stage && bound && !unknown && ReplacesOwnMachineElsewhere(change, machines, context) is { } from)
                return $"The plan would move {context.Proxmox.Machine} {context.Proxmox.Vmid} from {from} to {context.Proxmox.Node} by replacing it, "
                    + $"which wipes its disks. Set migrate = true on the {change["type"]} of the workflow's module so the provider migrates it, "
                    + $"or set host back to {from}. Octopushy refuses this plan whatever the policy says.";
            var refused = stage
                ? machines.Count > 0 || !freeType
                : machines.Any(t => t.Vmid != context.Proxmox.Vmid || t.Node is null || t.Node != context.Proxmox.Node)
                  || !freeType && (machines.Count == 0 || unknown);
            if (refused)
                foreign.Add(Describe(change, targets, unknown));
        }

        if (foreign.Count == 0)
            return null;
        var what = string.Join("; ", foreign);
        return stage
            ? $"Stage {context.Stage} would change a Proxmox resource that acts on a machine ({what}). A pipeline stage owns no machine, so Octopushy refuses this plan whatever the policy says."
            : $"The plan would change a Proxmox resource that is not this guest's {context.Proxmox.Machine} {context.Proxmox.Vmid} on {context.Proxmox.Node} ({what}). "
              + "Octopushy refuses to act on a machine it does not manage, whatever the policy says.";
    }

    /// <summary>
    /// The node the plan migrates the guest's own machine from, to the guest's node, or null when it moves nothing. Such a plan
    /// is not foreign when the guest is bound to its VMID (see <see cref="ForeignChange"/>), and it waits for a person: the
    /// machine may have been moved in Proxmox on purpose, and the definition not told yet.
    /// </summary>
    public static string? MovesGuestMachineFrom(JsonNode? outputs, RunContext context) =>
        (outputs?["changes"] as JsonArray ?? []).OfType<JsonNode>()
            .Where(c => IsMachine(c) && Actions(c) is ["update"])
            .Select(c => OwnVmidOnTwoNodes(Targets(c).Where(t => t.Vmid is not null).ToList(), context))
            .FirstOrDefault(from => from is not null);

    /// <summary>
    /// The guest's own machine resource, updated in place, naming only its own VMID, on its own node and on one other: a
    /// migration to its node. Nothing is deleted or created.
    /// </summary>
    private static bool MovesOwnMachine(JsonNode change, List<Target> machines, RunContext context) =>
        IsMachine(change) && Actions(change) is ["update"] && OwnVmidOnTwoNodes(machines, context) is not null;

    /// <summary>The node the guest's own machine is replaced from, when the plan replaces it to place it on its node; null otherwise.</summary>
    private static string? ReplacesOwnMachineElsewhere(JsonNode change, List<Target> machines, RunContext context) =>
        IsMachine(change) && Actions(change).Contains("delete") && Actions(change).Contains("create") ? OwnVmidOnTwoNodes(machines, context) : null;

    /// <summary>The other node, when the targets are exactly the guest's VMID on its node and on one other.</summary>
    private static string? OwnVmidOnTwoNodes(List<Target> machines, RunContext context)
    {
        if (machines.Count != 2 || machines.Any(t => t.Vmid != context.Proxmox.Vmid || t.Node is null)
            || machines.Count(t => t.Node == context.Proxmox.Node) != 1)
            return null;
        return machines.Single(t => t.Node != context.Proxmox.Node).Node;
    }

    /// <summary>A resource of a Proxmox provider: the agent says so, or its type does, for a summary that predates that.</summary>
    private static bool IsProxmox(JsonNode change) =>
        change["proxmox"] is JsonValue p && p.TryGetValue<bool>(out var proxmox) && proxmox
        || change["type"]?.ToString()?.StartsWith("proxmox_", StringComparison.Ordinal) == true;

    private static bool Mutates(JsonNode change) => Actions(change).Any(a => a is "create" or "update" or "delete");

    /// <summary>Every VM and node the change names: its <c>targets</c>, and the machine identity of a VM or container.</summary>
    private static List<Target> Targets(JsonNode change)
    {
        static Target Of(JsonNode? node) => new(
            node?["vmId"] is JsonValue id && id.TryGetValue<int>(out var vmid) ? vmid : null,
            node?["node"]?.ToString() is { Length: > 0 } name ? name : null);
        var targets = (change["targets"] as JsonArray ?? []).Select(Of).ToList();
        if (change["vmId"] is not null || change["node"] is not null)
            targets.Add(Of(change));
        return targets.Where(t => t.Vmid is not null || t.Node is not null).Distinct().ToList();
    }

    /// <summary>A change as a person can check it: what it is, what it does, and the VMIDs and nodes it names, or that it names none.</summary>
    private static string Describe(JsonNode change, List<Target> targets, bool unknown)
    {
        var actions = Actions(change);
        var verb = actions.Contains("delete") ? actions.Contains("create") ? "replaces" : "deletes" : actions.Contains("create") ? "creates" : "updates";
        static string Name(Target t) =>
            $"{(t.Vmid is { } vmid ? $"VMID {vmid}" : "an unknown VMID")} on {(t.Node is { } node ? $"node {node}" : "an unknown node")}";
        var named = targets.Count == 0 ? "an unknown VMID on an unknown node" : string.Join(", ", targets.Select(Name));
        return $"{change["address"]?.ToString() ?? change["type"]?.ToString()} {verb} {named}{(unknown ? ", some of it not known until apply" : "")}";
    }

    /// <summary>The plan deletes or replaces some Proxmox VM or container, its disks included.</summary>
    public static bool RemovesVm(JsonNode? outputs) => Removals(outputs).Any();

    /// <summary>The plan deletes or replaces the machine this guest owns. Nothing else justifies lifting its protection.</summary>
    public static bool RemovesGuestVm(JsonNode? outputs, RunContext context) =>
        Removals(outputs).Any(c => IsGuests(c, context));

    /// <summary>
    /// The plan changes the container this guest owns in place. Proxmox refuses any change to a protected container's
    /// disks, a root disk that only grows included, so such a plan needs its protection lifted as well, and put back after.
    /// A VM's disks grow while it is protected, so an update to a VM never counts.
    /// </summary>
    public static bool UpdatesGuestContainer(JsonNode? outputs, RunContext context) =>
        (outputs?["changes"] as JsonArray ?? []).OfType<JsonNode>().Any(c =>
            c["type"]?.ToString() == ContainerResourceType && Actions(c) is ["update"] && IsGuests(c, context));

    /// <summary>The plan removes the machine this guest owns, or changes its container in place: the only reasons to lift its protection.</summary>
    public static bool LiftsProtection(JsonNode? outputs, RunContext context) =>
        RemovesGuestVm(outputs, context) || UpdatesGuestContainer(outputs, context);

    /// <summary>
    /// The only machine the plan removes is this guest's, and OpenTofu replaces it because creating it did not finish.
    /// A replacement for any other reason, such as an edit, does not count.
    /// </summary>
    public static bool RecreatesFailedVm(JsonNode? outputs, RunContext context) =>
        Removals(outputs).ToList() is { Count: > 0 } removals && removals.All(c =>
            IsGuests(c, context) && c["actionReason"]?.ToString() == TaintedReason && Actions(c).Contains("create"));
}
