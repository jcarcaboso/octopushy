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

    /// <summary>What a configure run runs. It reaches the guest over SSH, so it never uses <see cref="Proxmox"/>'s credentials.</summary>
    public AnsibleContext? Ansible { get; init; }

    public required ProxmoxContext Proxmox { get; init; }

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

    /// <summary>Whether Octopushy's own key goes to this guest with the keys it declares.</summary>
    public bool ManagedAccess { get; init; }

    public long? RootDiskBytes { get; init; }

    /// <summary>The time a scheduled run was for, which its limit is counted from.</summary>
    public DateTimeOffset? ScheduledFor { get; init; }

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

/// <summary>
/// A configure run: one action of a recipe, against one guest. <see cref="Vars"/> holds no secret
/// value; a secret var names its role in <see cref="RunContext.Secrets"/>.
/// </summary>
public sealed record AnsibleContext(
    string Recipe,
    string Action,
    string Root,
    string Playbook,
    string? Prepare,
    bool UsesAppliedSnapshot,
    bool Destructive,
    int? TimeoutMinutes,
    string Image,
    string GuestName,
    string GuestDirectory,
    string Address,
    int Port,
    string User,
    JsonObject Vars,
    IReadOnlyList<string> SecretVars,
    IReadOnlyList<string> SecretFiles,
    Dictionary<string, AnsibleOutcome> Outcomes,
    /// <summary>What changed under the recipe and its project since the guest's last successful run; null when it has none.</summary>
    IReadOnlyList<string>? ChangedFiles);

/// <summary>A declared outcome, as the run was built with it.</summary>
public sealed record AnsibleOutcome(string? Description, JsonObject? ApproveVars, string Result);

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
    string? ExpectIpv4);

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
    /// <summary>Shows what a configure run will do, with no secrets and no contact with the guest.</summary>
    public const string Preview = "preview";
    public const string Playbook = "playbook";
    /// <summary>A person decides what a playbook's declared outcome should lead to.</summary>
    public const string OutcomeApproval = "outcome-approval";
    /// <summary>The one further run a person may approve after a declared outcome.</summary>
    public const string PlaybookApproved = "playbook-approved";
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

    public const string AnsibleSshKey = "ansible.ssh-key";
    public const string AnsibleHostKey = "ansible.host-key";

    public static string Backend(string environmentVariable) => $"backend.{environmentVariable}";

    /// <summary>A guest's <c>setup.secrets</c> entry.</summary>
    public static string AnsibleVar(string name) => $"ansible.var.{name}";

    /// <summary>A guest's <c>setup.secretFiles</c> entry.</summary>
    public static string AnsibleFile(string name) => $"ansible.file.{name}";
}
