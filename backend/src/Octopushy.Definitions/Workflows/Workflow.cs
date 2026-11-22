using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;

namespace Octopushy.Definitions;

/// <summary>A workflow's version, <c>M.m</c>: a new major is a breaking change, a new minor is not.</summary>
public readonly partial record struct WorkflowVersion(int Major, int Minor) : IComparable<WorkflowVersion>
{
    public int CompareTo(WorkflowVersion other) => Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

    public override string ToString() => $"{Major}.{Minor}";

    public static bool TryParse(string? text, out WorkflowVersion version)
    {
        version = default;
        if (text is null || VersionPattern().Match(text) is not { Success: true } match)
            return false;
        version = new WorkflowVersion(
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
        return true;
    }

    [GeneratedRegex(@"^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\z")]
    private static partial Regex VersionPattern();
}

/// <summary>
/// What a guest or service names: <c>id@M</c>, the newest <c>M.x</c>, or <c>id@M.m</c>, exactly that version. A
/// reference without a version is invalid, so a major version is never adopted by accident.
/// </summary>
public sealed partial record WorkflowReference(string Id, int Major, int? Minor)
{
    public bool Matches(WorkflowVersion version) => version.Major == Major && (Minor is not { } minor || version.Minor == minor);

    public override string ToString() => Minor is { } minor ? $"{Id}@{Major}.{minor}" : $"{Id}@{Major}";

    public static WorkflowReference? Parse(string? text)
    {
        if (text is null || ReferencePattern().Match(text) is not { Success: true } match)
            return null;
        return new WorkflowReference(
            match.Groups[1].Value,
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : null);
    }

    [GeneratedRegex(@"^([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)@(0|[1-9][0-9]{0,8})(?:\.(0|[1-9][0-9]{0,8}))?\z")]
    private static partial Regex ReferencePattern();
}

/// <summary>What a workflow manages, from its <c>manages</c>.</summary>
public static class WorkflowKinds
{
    public const string Vm = "vm";
    public const string Container = "container";
    public const string Service = "service";

    /// <summary>Whether a workflow of this kind makes a machine for a guest: a VM or a Proxmox container.</summary>
    public static bool IsMachine(string? manages) => manages is Vm or Container;

    /// <summary>The kind of workflow a guest of this <c>type</c> names: <c>vm</c> or <c>container</c>, the same word.</summary>
    public static string ForGuestType(string type) => type == GuestTypes.Container ? Container : Vm;
}

/// <summary>What a guest is, from its <c>type</c>, and how Proxmox lists it.</summary>
public static class GuestTypes
{
    public const string Vm = "vm";
    public const string Container = "container";

    /// <summary>The guest's kind in Proxmox: <c>qemu</c> for a VM, <c>lxc</c> for a container.</summary>
    public static string ProxmoxKind(string? type) => type == Container ? "lxc" : "qemu";
}

/// <summary>The tools a stage can run, and the agent's own Proxmox executor, which no stage names. See contracts/agent/v1/executors.md.</summary>
public static class Executors
{
    public const string OpenTofu = "opentofu";
    public const string Ansible = "ansible";
    public const string Proxmox = "proxmox";
}

/// <summary>
/// One workflow, at one version, read from <c>workflows/&lt;id&gt;/workflow.yaml</c> under <paramref name="SourceRoot"/>: everything
/// needed to manage one kind of thing. Its paths are relative to <paramref name="SourceRoot"/>, the directory that holds
/// <c>workflows/</c> and <c>modules/</c>; for workflows kept in the lab repository, that is the repository, and for a
/// release of a workflows repository, the files of its tagged commit.
/// </summary>
/// <param name="InputsSchema">The inputs schema's path, inside the workflow's folder.</param>
/// <param name="Ui">Hints for a form rendered from the inputs schema: stored and served, never interpreted.</param>
/// <param name="Actions">The actions it declares, by name. A guest's setup is not one of them: see <see cref="WorkflowStage.Configure"/>.</param>
public sealed record WorkflowDefinition(
    string Id,
    WorkflowVersion Version,
    string SourceRoot,
    string File,
    string Directory,
    JsonObject Json,
    string Manages,
    string? Description,
    string? InputsSchema,
    JsonObject? Ui,
    IReadOnlyList<string> Secrets,
    IReadOnlyList<WorkflowStage> Stages,
    IReadOnlyDictionary<string, WorkflowAction> Actions,
    IReadOnlyDictionary<string, WorkflowOutcome> Outcomes)
{
    /// <summary>The name of the action a guest's Ansible stage runs when anything it is built from changes.</summary>
    public const string SetupAction = "setup";

    /// <summary>False when the workflow has an error of its own: nothing that names it is valid then.</summary>
    public bool Valid { get; init; } = true;

    /// <summary>The inputs schema, built; null when it has none or it is not a valid schema.</summary>
    public JsonSchema? Schema { get; init; }

    /// <summary>The inputs schema as written, served to forms; null when it has none or it is not a valid schema.</summary>
    public JsonObject? SchemaJson { get; init; }

    /// <summary>
    /// <see cref="Schema"/> as an attached guest is checked against it: <c>hardware</c> and <c>network</c> are not required, since
    /// no machine stage ever reads them, and what such a guest does write is still checked.
    /// </summary>
    public JsonSchema? AttachedSchema { get; init; }

    /// <summary>
    /// What this workflow's inputs schema finds wrong with a guest definition's <c>hardware</c>, <c>network</c> and <c>inputs</c>.
    /// An attached guest may leave out <c>hardware</c> and <c>network</c>; see <see cref="AttachedSchema"/>.
    /// </summary>
    public IReadOnlyList<Schemas.SchemaError> EvaluateGuest(JsonObject guest)
    {
        var attached = guest["machine"] is JsonValue machine && machine.TryGetValue<string>(out var text) && text == GuestMachines.Attached;
        if ((attached ? AttachedSchema : Schema) is not { } schema)
            return [];
        var instance = new JsonObject();
        foreach (var section in new[] { "hardware", "network" })
        {
            // A section left as null would fail its type: an attached guest that leaves one out has none to check.
            if (!attached || guest[section] is not null)
                instance[section] = guest[section]?.DeepClone();
        }
        instance["inputs"] = guest["inputs"]?.DeepClone() ?? new JsonObject();
        return Schemas.SchemaCatalog.Evaluate(schema, instance);
    }

    /// <summary>For a workflow from a workflows repository, the repository it was released from; null for one kept in the lab repository.</summary>
    public string? Repository { get; init; }

    /// <summary>For a workflow from a workflows repository, the commit its release tag recorded; null for one kept in the lab repository.</summary>
    public string? Commit { get; init; }

    /// <summary>The workflow at its version, as runs and pages name it: <c>fedora-vm@2.3</c>.</summary>
    public string Label => $"{Id}@{Version}";

    /// <summary>What its issues are filed under.</summary>
    public string Address => $"workflows/{Id}";

    /// <summary>For <c>manages: vm</c> or <c>container</c>, the OpenTofu stage that makes the machine: its provision and retire runs.</summary>
    public WorkflowStage? Machine => WorkflowKinds.IsMachine(Manages) ? Stages.FirstOrDefault(s => s.Executor == Executors.OpenTofu) : null;

    /// <summary>For <c>manages: vm</c> or <c>container</c>, the Ansible stage that sets the machine up, if it has one: its configure runs.</summary>
    public WorkflowStage? Setup => WorkflowKinds.IsMachine(Manages) ? Stages.Skip(1).FirstOrDefault(s => s.Executor == Executors.Ansible) : null;

    /// <summary>Every directory a stage of the workflow runs from.</summary>
    public IEnumerable<string> Roots => Stages.Select(s => s.Root).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// For <c>manages: vm</c> or <c>container</c> with a setup stage: the values a playbook of that stage may hand back with
    /// <c>set_stats</c> as <c>octopushy_reports</c>, by name. Octopushy keeps the latest of each per guest, from a run that
    /// reached the guest over its pinned host key, and gives them to the guest's peers. See docs/workflows.md#reports-and-peers.
    /// </summary>
    public IReadOnlyDictionary<string, WorkflowReport> Reports { get; init; } = new Dictionary<string, WorkflowReport>(StringComparer.Ordinal);

    /// <summary>
    /// For <c>manages: vm</c> or <c>container</c> with a setup stage: the guests whose reports this guest's runs get, as a path
    /// into <c>{ inputs }</c> such as <c>inputs.technitium_members[].name</c>. Each value names a guest of the same lab by id.
    /// </summary>
    public string? Peers { get; init; }
}

/// <summary>A value a playbook may report, and the pattern the whole of it has to match before Octopushy keeps it.</summary>
public sealed record WorkflowReport(string Name, string? Description, string Pattern)
{
    /// <summary>The longest value kept, in characters. A report is a fingerprint or a state, never a document.</summary>
    public const int MaxLength = 256;

    /// <summary>How long a pattern may take on one value, which a repository wrote.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Whether <paramref name="value"/> is one this report may hold: short enough, and matched whole by its pattern.</summary>
    public bool Accepts(string value)
    {
        if (value.Length is 0 or > MaxLength)
            return false;
        try
        {
            return Whole(Pattern).IsMatch(value);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>The pattern as it is matched: against the whole value. Validation builds this same one, so what passes it matches.</summary>
    public static System.Text.RegularExpressions.Regex Whole(string pattern) =>
        new($@"\A(?:{pattern})\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant, MatchTimeout);
}

/// <summary>
/// One stage of a workflow: one executor, run from <paramref name="Root"/>. An Ansible stage carries what can be run from it in
/// <paramref name="Configure"/>; an OpenTofu stage has none.
/// </summary>
/// <param name="Verify"><c>proxmox</c> when the stage makes a VM or container that is checked through the Proxmox API afterwards, otherwise <c>none</c>.</param>
public sealed record WorkflowStage(
    string Name,
    string Executor,
    string Root,
    string Verify,
    string? Image,
    ConfigureActions? Configure)
{
    /// <summary>
    /// For the stage that makes a VM, <c>hostKey: managed</c>: its module installs the SSH host key Octopushy generates
    /// for each guest, in a cloud-init snippet. See <see cref="ManagedHostKey"/>.
    /// </summary>
    public bool ManagedHostKey { get; init; }

    /// <summary>The OpenTofu stage timeout, in minutes; null uses the agent default of 60.</summary>
    public int? TimeoutMinutes { get; init; }

    /// <summary>
    /// For an Ansible stage of <c>manages: service</c>, the interface slot it fills, such as <c>dns</c>: it runs only where the
    /// service's installation binds the slot. See docs/workflows.md#interfaces.
    /// </summary>
    public string? Interface { get; init; }

    /// <summary>The slot's schema, from <c>interfaces/&lt;slot&gt;.schema.json</c> of the workflow's source, built; null without <see cref="Interface"/>.</summary>
    public JsonSchema? InterfaceSchema { get; init; }

    /// <summary>
    /// For a stage of <c>manages: service</c>, the host names it sets: a path into <c>{ inputs }</c> such as
    /// <c>inputs.dns_records[].name</c>. See <see cref="ServiceDefinition.Names"/>.
    /// </summary>
    public string? Names { get; init; }
}

/// <summary>What an Ansible stage offers: the action that runs on change, any others, the outcomes it may report, and the secrets it needs.</summary>
public sealed record ConfigureActions(
    IReadOnlyDictionary<string, WorkflowAction> Actions,
    IReadOnlyDictionary<string, WorkflowOutcome> Outcomes,
    IReadOnlyList<string> Secrets)
{
    /// <summary>What runs when the guest, the service or the workflow changes.</summary>
    public WorkflowAction? OnChange => Actions.Values.FirstOrDefault(a => a.OnChange);
}

/// <summary>One playbook that can be run from an Ansible stage.</summary>
public sealed record WorkflowAction(
    string Id,
    string? Description,
    string Playbook,
    string? Prepare,
    bool UsesAppliedSnapshot,
    bool Destructive,
    bool OnChange,
    int? TimeoutMinutes)
{
    /// <summary>
    /// The approval the workflow gives this action on every guest, if it sets one. It wins over the guest's approval
    /// settings, and the guest's <c>policy.actions</c> wins over it. The setup stage's own action has none.
    /// </summary>
    public string? Approval { get; init; }
}

/// <summary>A result a playbook may report that is neither success nor failure.</summary>
/// <param name="Promote">False when a run that ends with it succeeds without marking anything applied.</param>
public sealed record WorkflowOutcome(string Id, string? Description, JsonObject? ApproveVars, string Result, bool Promote = true);
