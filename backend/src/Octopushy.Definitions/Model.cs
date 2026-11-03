using System.Text.Json.Nodes;

namespace Octopushy.Definitions;

public enum IssueSeverity
{
    Error,
    Warning,
}

/// <summary>A problem found in the lab repository, located by file and line.</summary>
public sealed record DefinitionIssue(IssueSeverity Severity, string File, int? Line, string Message, string? Address = null);

public enum ResourceKind
{
    Lab,
    Installation,
    Guest,
    Service,
}

/// <summary>A variable-store key with an optional exact version, written as <c>{ store: key, version: 2 }</c>.</summary>
public sealed record StoreReference(string Key, int? Version = null);

/// <summary>Policy settings as written on one resource; unset fields flow down from above.</summary>
public sealed record PolicySettings(string? Approval, string? Destructive);

/// <summary>Where a resource's definition lives and whether it, and everything it relies on, is valid.</summary>
/// <param name="Workflow">For a guest or service, the workflow and version it resolved to, such as <c>fedora-vm@2.3</c>.</param>
public sealed record ResourceEntry(
    ResourceKind Kind,
    string Address,
    string? ParentAddress,
    string File,
    JsonObject? Json,
    bool Valid,
    string? Workflow = null);

public sealed record RepositoryDefinition(
    string File,
    JsonObject Json,
    PolicySettings? DefaultPolicy,
    IReadOnlyList<string>? DefaultAgentLabels);

public sealed record LabDefinition(
    string Id,
    string File,
    JsonObject Json,
    string Name,
    string? Timezone,
    PolicySettings? Policy,
    IReadOnlyList<string>? AgentLabels)
{
    public string Address => Id;
}

public sealed record ConnectionDefinition(
    string Endpoint, bool VerifyTls, StoreReference ReadOnlyToken, StoreReference PrivilegedToken, StoreReference? CaCertificate,
    bool SharedToken = false);

public sealed record HostDefinition(
    string Id,
    string? Endpoint,
    IReadOnlyDictionary<string, string> Datastores,
    IReadOnlyList<string> Bridges,
    int? ConcurrentOperations,
    JsonObject Json);

/// <summary>
/// A Proxmox installation. <paramref name="Managed"/> is false for <c>manage: false</c>: Octopushy only observes
/// it, and no guest or service may be defined under it. See docs/safety-boundary.md.
/// </summary>
public sealed record InstallationDefinition(
    string Id,
    string Lab,
    string File,
    JsonObject Json,
    string Type,
    ConnectionDefinition Connection,
    IReadOnlyDictionary<string, HostDefinition> Hosts,
    PolicySettings? Policy,
    IReadOnlyList<string>? AgentLabels,
    bool Managed = true)
{
    public string Address => $"{Lab}/{Id}";

    public string DisplayName => Json["name"]?.GetValue<string>() ?? Id;
}

/// <summary>
/// A VM. <paramref name="WorkflowReference"/> is what <c>guest.yaml</c> names; <paramref name="Workflow"/> is the version it
/// resolved to, a workflow with <c>manages: vm</c>, or null when it resolved to none.
/// </summary>
public sealed record GuestDefinition(
    string Id,
    string Lab,
    string Installation,
    string File,
    string Directory,
    JsonObject Json,
    string Lifecycle,
    string? RenamedFrom,
    string Host,
    int Vmid,
    string? Template,
    WorkflowReference WorkflowReference,
    WorkflowDefinition? Workflow,
    PolicySettings? Policy,
    IReadOnlyList<string>? AgentLabels)
{
    public string InstallationAddress => $"{Lab}/{Installation}";

    public string Address => $"{Lab}/{Installation}/{Id}";

    /// <summary>The name of the guest's OpenTofu state: its address with dots.</summary>
    public string Stack => Address.Replace('/', '.');

    public bool Retired => Lifecycle == "retired";

    /// <summary>The workflow's Ansible stage that sets the machine up, if it has one.</summary>
    public WorkflowStage? Setup => Workflow?.Setup;
}

/// <summary>
/// One thing that runs on a guest, from <c>services/&lt;id&gt;/service.yaml</c> in the guest's folder: a workflow with
/// <c>manages: service</c>, run as its stages. Octopushy knows nothing of what it is: what its inputs mean is its workflow's.
/// </summary>
/// <param name="Stages">The resolved workflow's stages, with the service's inputs and secrets; empty when it resolved to none.</param>
public sealed record ServiceDefinition(
    string Id,
    string GuestAddress,
    string File,
    string Directory,
    JsonObject Json,
    WorkflowReference WorkflowReference,
    WorkflowDefinition? Workflow,
    IReadOnlyList<StageDefinition> Stages,
    PolicySettings? Policy,
    IReadOnlyList<string>? AgentLabels)
{
    public string Address => $"{GuestAddress}/{Id}";
}

/// <summary>
/// A stage of a service's workflow as the service runs it. Every stage gets the service's inputs; only an Ansible stage gets
/// its secrets, and an OpenTofu stage none.
/// </summary>
public sealed record StageDefinition(
    string Name,
    WorkflowStage Definition,
    JsonObject Inputs,
    IReadOnlyDictionary<string, StoreReference> Secrets,
    IReadOnlyDictionary<string, StoreReference> SecretFiles);

public sealed record ImageSource(string Url, string ChecksumAlgorithm, string Checksum, string FileName);

public sealed record TemplateDefinition(
    string Id,
    string File,
    JsonObject Json,
    string? Os,
    ImageSource? Image,
    IReadOnlyList<string> AvailableOn);

/// <summary>A store key, optional exact version, and the resource whose definition uses it.</summary>
public sealed record StoreUse(string Key, string Consumer, string File, int? Version = null);
