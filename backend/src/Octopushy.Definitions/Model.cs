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
}

/// <summary>A variable-store key with an optional exact version, written as <c>{ store: key, version: 2 }</c>.</summary>
public sealed record StoreReference(string Key, int? Version = null);

/// <summary>Policy settings as written on one resource; unset fields flow down from above.</summary>
public sealed record PolicySettings(string? Approval, string? Destructive);

/// <summary>Where a resource's definition lives and whether it, and everything it relies on, is valid.</summary>
public sealed record ResourceEntry(
    ResourceKind Kind,
    string Address,
    string? ParentAddress,
    string File,
    JsonObject? Json,
    bool Valid);

public sealed record RepositoryDefinition(
    string File,
    JsonObject Json,
    PolicySettings? DefaultPolicy,
    IReadOnlyList<string>? DefaultAgentLabels,
    IReadOnlyDictionary<string, StateBackendDefinition> StateBackends);

/// <summary>An environment value for OpenTofu: written directly, or read from the store.</summary>
public sealed record EnvValue(string? Value, StoreReference? Store);

public sealed record StateBackendDefinition(
    string Id,
    string Type,
    IReadOnlyDictionary<string, JsonNode?> Config,
    string? Workspace,
    IReadOnlyDictionary<string, EnvValue> Env,
    JsonObject Json);

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

public sealed record InstallationDefinition(
    string Id,
    string Lab,
    string File,
    JsonObject Json,
    string Type,
    ConnectionDefinition Connection,
    IReadOnlyDictionary<string, HostDefinition> Hosts,
    PolicySettings? Policy,
    IReadOnlyList<string>? AgentLabels)
{
    public string Address => $"{Lab}/{Id}";

    public string DisplayName => Json["name"]?.GetValue<string>() ?? Id;
}

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
    string Recipe,
    /// <summary>A backend from octopushy.yaml; null keeps the state in Octopushy itself.</summary>
    string? StateBackend,
    PolicySettings? Policy,
    IReadOnlyList<string>? AgentLabels)
{
    public string InstallationAddress => $"{Lab}/{Installation}";

    public string Address => $"{Lab}/{Installation}/{Id}";

    /// <summary>The name of the guest's OpenTofu state: its address with dots.</summary>
    public string Stack => Address.Replace('/', '.');

    public bool Retired => Lifecycle == "retired";

    public ManagedWorkerSettings? ManagedWorker => Json["worker"] is JsonObject worker
        ? new(worker["templateVersion"]!.GetValue<int>(), Guid.Parse(worker["generation"]!.GetValue<string>()),
            worker["labels"]!.AsArray().Select(label => label!.GetValue<string>()).ToArray(),
            worker["maxConcurrency"]!.GetValue<int>())
        : null;
}

public sealed record ManagedWorkerSettings(int TemplateVersion, Guid Generation, IReadOnlyList<string> Labels, int MaxConcurrency);

public sealed record ImageSource(string Url, string ChecksumAlgorithm, string Checksum, string FileName);

public sealed record TemplateDefinition(
    string Id,
    string File,
    JsonObject Json,
    string? Os,
    ImageSource? Image,
    IReadOnlyList<string> AvailableOn);

public sealed record RecipeDefinition(
    string Id,
    string File,
    JsonObject Json,
    string Operation,
    string Executor,
    string Root,
    string? InputsSchema,
    string Verify,
    string? Image,
    string? GuestFilesystem = null);

/// <summary>A store key, optional exact version, and the resource whose definition uses it.</summary>
public sealed record StoreUse(string Key, string Consumer, string File, int? Version = null);
