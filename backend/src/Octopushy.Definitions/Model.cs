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
    Cluster,
    Service,
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

    /// <summary>VMIDs Octopushy does not manage here. Nothing else keeps a plan off them when the token can reach them.</summary>
    public IReadOnlyList<int> ReservedVmids =>
        (Json["reservedVmids"] as JsonArray)?.Select(v => v!.GetValue<int>()).ToList() ?? [];
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

    public string Type => Json["type"]!.GetValue<string>();

    public bool IsContainer => Type == GuestTypes.Container;

    /// <summary>The OpenTofu variables of the recipe's root that come from the store, by variable name.</summary>
    public IReadOnlyDictionary<string, StoreReference> ProvisionSecrets =>
        (Json["provision"]?["secrets"] as JsonObject)?.ToDictionary(
            p => p.Key,
            p => new StoreReference(p.Value!["store"]!.GetValue<string>(), p.Value["version"]?.GetValue<int>()),
            StringComparer.Ordinal) ?? [];

    /// <summary>The configure recipe of <c>setup.recipe</c>, if the guest has one.</summary>
    public string? SetupRecipe => Json["setup"]?["recipe"]?.GetValue<string>();

    public ManagedWorkerSettings? ManagedWorker => Json["worker"] is JsonObject worker
        ? new(worker["templateVersion"]!.GetValue<int>(), Guid.Parse(worker["generation"]!.GetValue<string>()),
            worker["labels"]!.AsArray().Select(label => label!.GetValue<string>()).ToArray(),
            worker["maxConcurrency"]!.GetValue<int>())
        : null;
}

public static class GuestTypes
{
    public const string Vm = "vm";
    public const string Container = "container";
}

public static class ClusterRoles
{
    public const string Primary = "primary";
    public const string Secondary = "secondary";
}

/// <summary>A guest of an application cluster, by its full address.</summary>
public sealed record ClusterMember(string Guest, string Role);

/// <summary>
/// An application cluster: guests of one lab that a recipe forms into one application. Each
/// member stays an ordinary guest; this relates them and holds what they share.
/// </summary>
public sealed record ClusterDefinition(
    string Id,
    string Lab,
    string File,
    JsonObject Json,
    string Type,
    IReadOnlyList<ClusterMember> Members,
    StoreReference Credentials,
    PolicySettings? Policy,
    IReadOnlyList<string>? AgentLabels)
{
    public string Address => $"{Lab}/clusters/{Id}";

    /// <summary>The name of the cluster's OpenTofu state: its address with dots.</summary>
    public string Stack => Address.Replace('/', '.');

    /// <summary>The recipe that implements <see cref="Type"/>.</summary>
    public string Recipe => $"{Type}-cluster";

    public string ClusterDomain => Json["settings"]!["clusterDomain"]!.GetValue<string>();

    public ClusterMember? Primary => Members.FirstOrDefault(m => m.Role == ClusterRoles.Primary);
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
    string? GuestFilesystem = null,
    string GuestType = GuestTypes.Vm,
    IReadOnlyList<string>? Modules = null,
    IReadOnlyList<string>? Secrets = null,
    ConfigureRecipe? Configure = null);

/// <summary>What a recipe with <c>operation: configure</c> offers.</summary>
public sealed record ConfigureRecipe(
    IReadOnlyDictionary<string, RecipeAction> Actions,
    IReadOnlyDictionary<string, RecipeOutcome> Outcomes,
    IReadOnlyList<string> Secrets)
{
    /// <summary>What runs when a guest or these files change, if anything does.</summary>
    public RecipeAction? OnChange => Actions.Values.FirstOrDefault(a => a.OnChange);

    /// <summary>What runs when a service's folder is deleted, if anything does.</summary>
    public RecipeAction? Removal => Actions.Values.FirstOrDefault(a => a.Removal);
}

/// <summary>One thing that can be run from a configure recipe.</summary>
public sealed record RecipeAction(
    string Id,
    string? Description,
    string Playbook,
    string? Prepare,
    bool UsesAppliedSnapshot,
    bool Destructive,
    bool OnChange,
    int? TimeoutMinutes,
    bool Removal = false);

/// <summary>
/// One deployment of something on a guest: a resource of its own, with its own status, approval and
/// failures. <see cref="Guest"/> is the guest's address. Its code may be a shared recipe; its definition is not shared.
/// </summary>
public sealed record ServiceDefinition(
    string Id,
    string Guest,
    string File,
    string Directory,
    JsonObject Json,
    string Recipe,
    PolicySettings? Policy,
    IReadOnlyList<string>? AgentLabels)
{
    public string Address => $"{Guest}/{Id}";

    /// <summary>What the service declares it listens on, as written.</summary>
    public JsonArray Ports => Json["ports"]?.DeepClone().AsArray() ?? [];
}

/// <summary>A result a playbook may report that is neither success nor failure.</summary>
public sealed record RecipeOutcome(string Id, string? Description, JsonObject? ApproveVars, string Result);

/// <summary>A store key, optional exact version, and the resource whose definition uses it.</summary>
public sealed record StoreUse(string Key, string Consumer, string File, int? Version = null);
