using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// What a run's steps, or an observation, are built from, fixed when they are created. It names secrets by their
/// store keys and never holds their values.
/// </summary>
public sealed record RunContext
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public required string Address { get; init; }

    public string? Commit { get; init; }

    public IReadOnlyList<string> AgentLabels { get; init; } = [];

    public string AgentLabelsSetBy { get; init; } = "default";

    /// <summary>The Proxmox host the run acts on, as <c>lab/installation/host</c>.</summary>
    public string? HostKey { get; init; }

    public int? HostLimit { get; init; }

    public PolicyContext? Policy { get; init; }

    public OpenTofuContext? OpenTofu { get; init; }

    /// <summary>The installation the work acts on. A cluster's run has none, so this stays <see cref="ProxmoxContext.None"/>.</summary>
    public ProxmoxContext Proxmox { get; init; } = ProxmoxContext.None;

    /// <summary>The application cluster the work acts on, instead of a Proxmox installation.</summary>
    public ClusterContext? Cluster { get; init; }

    /// <summary>The digest of the code the work runs, which a grant of a protected secret must match.</summary>
    public string? CodeDigest { get; init; }

    /// <summary>The folders that digest covers: all a run holding a protected secret receives.</summary>
    public IReadOnlyList<string>? ReviewedPaths { get; init; }

    /// <summary>Whether that code is known to call no module, so that none need be installed.</summary>
    public bool CallsNoModules { get; init; }

    public DiskReporterContext? DiskReporter { get; init; }

    /// <summary>Semantic credential roles mapped to store keys; the builder selects the wire alias.</summary>
    public Dictionary<string, string> Secrets { get; init; } = [];

    /// <summary>Optional versions declared in Git; candidates require promotion before use.</summary>
    public Dictionary<string, int> RequestedSecretVersions { get; init; } = [];

    /// <summary>Exact versions fixed before the first attempt. Never contains secret values.</summary>
    public Dictionary<string, int> SecretVersions { get; init; } = [];

    public bool SecretBindingsPinned { get; set; }

    /// <summary><c>proxmox</c> or <c>none</c>.</summary>
    public string Verify { get; init; } = "proxmox";

    public ManagedWorkerContext? ManagedWorker { get; init; }

    public WorkerUpdateContext? WorkerUpdate { get; init; }

    /// <summary>
    /// The commit that deleted the guest's folder, when this retire run removes a guest whose
    /// definition disappeared. The run itself is built from the last revision that defined it.
    /// </summary>
    public string? DefinitionRemovedIn { get; init; }

    public long? RootDiskBytes { get; init; }

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static RunContext Parse(string json) =>
        JsonSerializer.Deserialize<RunContext>(json, Json) ?? throw new InvalidOperationException("The run has no context.");
}

public sealed record ManagedWorkerContext(Guid Generation, IReadOnlyList<string> Labels, int MaxConcurrency, string ServerUrl);

public sealed record WorkerUpdateContext(Guid AgentId, string ArchiveUrl, string ArchiveSha256, string SystemPath, string ImageSha256, string? CaCertificatePem);

public sealed record PolicyContext(string Approval, string ApprovalSetBy, string Destructive, string DestructiveSetBy, bool DestructiveExplicit = false);

public sealed record OpenTofuContext(
    string Stack,
    string Root,
    string Image,
    BackendContext Backend,
    JsonObject Variables,
    Dictionary<string, string> Env,
    Dictionary<string, string> SecretEnv);

public sealed record BackendContext(string Type, Dictionary<string, JsonNode?> Config, string? Workspace)
{
    /// <summary>The backend type that means Octopushy keeps the state itself.</summary>
    public const string BuiltInType = "octopushy";

    public static BackendContext BuiltIn => new(BuiltInType, [], null);

    [JsonIgnore]
    public bool IsBuiltIn => Type == BuiltInType;
}

public sealed record ProxmoxContext(
    string Endpoint,
    bool VerifyTls,
    string? CaCertificateKey,
    string? Node,
    int? Vmid,
    string? Name,
    string? ExpectIpv4,
    /// <summary><c>lxc</c> for a container. Null, for a VM, leaves the wire input as it always was.</summary>
    string? GuestKind = null)
{
    /// <summary>For work that does not go through Proxmox.</summary>
    public static readonly ProxmoxContext None = new("", true, null, null, null, null, null);
}

/// <summary>
/// An application cluster's run. <see cref="Endpoints"/> and <see cref="CodeDigest"/> are what
/// trust is checked against; <see cref="Input"/> is the <c>technitium</c> executor's input,
/// without what a step adds from the approved plan.
/// </summary>
public sealed record ClusterContext(
    string Type,
    string Primary,
    string ClusterDomain,
    IReadOnlyList<ClusterMemberContext> Members,
    IReadOnlyList<string> Endpoints,
    string CodeDigest,
    JsonObject Input,
    /// <summary>The hash of the declared list URLs, to tell whether they were ever downloaded.</summary>
    string BlockListsHash = "",
    /// <summary>Reads certificates only: it carries no password, so it needs no trust.</summary>
    bool Probe = false);

public sealed record ClusterMemberContext(string Guest, string Role, string Address);

public sealed record DiskReporterContext(
    string Endpoint,
    string? BinaryUrl,
    string? BinarySha256,
    int IntervalSeconds,
    string? CaCertificatePem,
    string InstallationMode = "fhs-download-v1",
    string? ImageSha256 = null);

/// <summary>Step names, shared with the API. <c>observe</c> is not a step: it names the work of an observation.</summary>
public static class StepNames
{
    public const string Plan = "plan";
    public const string Approval = "approval";
    public const string Unprotect = "unprotect";
    public const string Apply = "apply";
    /// <summary>Added to a provision run whose apply did not finish after it lifted the VM's protection.</summary>
    public const string Reprotect = "reprotect";
    public const string Verify = "verify";
    public const string VerifyAbsent = "verify-absent";
    public const string Observe = "observe";
    public const string GrowRoot = "grow-root";
    public const string VerifyRoot = "verify-root";
    public const string PrepareWorker = "prepare-worker";
    public const string InstallWorker = "install-worker";
    public const string WaitWorker = "wait-worker";
    public const string InstallDiskReporter = "install-disk-reporter";
    public const string UpdateWorkerOs = "update-worker-os";
    public const string VerifyWorkerOs = "verify-worker-os";
    public const string WaitUpdatedWorker = "wait-updated-worker";
    /// <summary>Server-side: stop a managed worker taking work and wait for its attempts to end.</summary>
    public const string DrainWorker = "drain-worker";
    /// <summary>Server-side: remove a removed managed worker's enrollment once its VM is gone.</summary>
    public const string RevokeWorker = "revoke-worker";
    /// <summary>Reads what the members report, for the plan to show what a run must do about them.</summary>
    public const string InspectCluster = "inspect-cluster";
    /// <summary>Takes the members the approved plan removes out of the cluster, before the apply forgets them.</summary>
    public const string DetachMembers = "detach-members";
    /// <summary>Joins the members the approved plan changed, and removes undeclared entries.</summary>
    public const string ReconcileCluster = "reconcile-cluster";
    public const string RefreshBlockLists = "refresh-block-lists";
    public const string VerifyCluster = "verify-cluster";
    /// <summary>Not a step: the work of a cluster's observation.</summary>
    public const string ObserveCluster = "observe-cluster";
    /// <summary>Not a step: reading what certificates a cluster's members present, with no credentials.</summary>
    public const string ProbeCluster = "probe-cluster";
}

/// <summary>The secret names executors look for.</summary>
public static class SecretNames
{
    public const string ProxmoxSharedToken = "proxmox.shared-token";
    public const string ProxmoxReadOnlyToken = "proxmox.read-only-token";
    public const string ProxmoxPrivilegedToken = "proxmox.privileged-token";
    /// <summary>Executor wire alias only; never a persisted role in new runs.</summary>
    public const string ProxmoxToken = "proxmox.token";
    public const string ProxmoxCa = "proxmox.ca";
    public const string DiskReporterToken = "disk-reporter.token";
    /// <summary>A cluster's admin password. Unlike the Proxmox tokens, its role and wire name are the same.</summary>
    public const string ClusterPassword = "technitium.password";

    public const string BackendPrefix = "backend.";

    public static string Backend(string environmentVariable) => $"{BackendPrefix}{environmentVariable}";

    private const string RecipePrefix = "recipe.";

    /// <summary>A guest's <c>provision.secrets</c> entry, delivered as this environment variable.</summary>
    public static string Recipe(string environmentVariable) => RecipePrefix + environmentVariable;

    /// <summary>Whether the apply is the only step that receives this secret.</summary>
    public static bool ApplyOnly(string name) => name.StartsWith(RecipePrefix, StringComparison.Ordinal);
}
