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
    public const string Service = "service";
}

/// <summary>The tools a stage can run.</summary>
public static class Executors
{
    public const string OpenTofu = "opentofu";
    public const string Ansible = "ansible";
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

    /// <summary>For a workflow from a workflows repository, the repository it was released from; null for one kept in the lab repository.</summary>
    public string? Repository { get; init; }

    /// <summary>For a workflow from a workflows repository, the commit its release tag recorded; null for one kept in the lab repository.</summary>
    public string? Commit { get; init; }

    /// <summary>The workflow at its version, as runs and pages name it: <c>fedora-vm@2.3</c>.</summary>
    public string Label => $"{Id}@{Version}";

    /// <summary>What its issues are filed under.</summary>
    public string Address => $"workflows/{Id}";

    /// <summary>For <c>manages: vm</c>, the OpenTofu stage that makes the machine: its provision and retire runs.</summary>
    public WorkflowStage? Machine => Manages == WorkflowKinds.Vm ? Stages.FirstOrDefault(s => s.Executor == Executors.OpenTofu) : null;

    /// <summary>For <c>manages: vm</c>, the Ansible stage that sets the machine up, if it has one: its configure runs.</summary>
    public WorkflowStage? Setup => Manages == WorkflowKinds.Vm ? Stages.Skip(1).FirstOrDefault(s => s.Executor == Executors.Ansible) : null;

    /// <summary>Every directory a stage of the workflow runs from.</summary>
    public IEnumerable<string> Roots => Stages.Select(s => s.Root).Distinct(StringComparer.Ordinal);
}

/// <summary>
/// One stage of a workflow: one executor, run from <paramref name="Root"/>. An Ansible stage carries what can be run from it in
/// <paramref name="Configure"/>; an OpenTofu stage has none.
/// </summary>
/// <param name="Verify"><c>proxmox</c> when the stage makes a VM that is checked through the Proxmox API afterwards, otherwise <c>none</c>.</param>
public sealed record WorkflowStage(
    string Name,
    string Executor,
    string Root,
    string Verify,
    string? Image,
    ConfigureActions? Configure);

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
    int? TimeoutMinutes);

/// <summary>A result a playbook may report that is neither success nor failure.</summary>
public sealed record WorkflowOutcome(string Id, string? Description, JsonObject? ApproveVars, string Result);
