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

    /// <summary>Semantic credential roles mapped to store keys; the builder selects the wire alias.</summary>
    public Dictionary<string, string> Secrets { get; init; } = [];

    /// <summary>Optional versions declared in Git; candidates require promotion before use.</summary>
    public Dictionary<string, int> RequestedSecretVersions { get; init; } = [];

    /// <summary>Exact versions fixed before the first attempt. Never contains secret values.</summary>
    public Dictionary<string, int> SecretVersions { get; init; } = [];

    public bool SecretBindingsPinned { get; set; }

    /// <summary><c>proxmox</c> or <c>none</c>.</summary>
    public string Verify { get; init; } = "proxmox";

    /// <summary>
    /// The commit that deleted the guest's folder, when this retire run removes a guest whose
    /// definition disappeared. The run itself is built from the last revision that defined it.
    /// </summary>
    public string? DefinitionRemovedIn { get; init; }

    /// <summary>Whether Octopushy's own key goes to this guest with the keys it declares.</summary>
    public bool ManagedAccess { get; init; }

    /// <summary>
    /// A pipeline run's stages, in order. Each names one <see cref="OpenTofu"/> or <see cref="Ansible"/>,
    /// and a step of the stage sees the context <see cref="ForStage"/> makes. In <see cref="Secrets"/> and
    /// its version maps, the roles of a stage are written <c>stage/role</c>, so two stages can bind the
    /// same extra var to different keys and the run still pins them all at once.
    /// </summary>
    public IReadOnlyList<StageContext>? Stages { get; init; }

    /// <summary>The stage this is the view of, from <see cref="ForStage"/>.</summary>
    public string? Stage { get; init; }

    /// <summary>The guest a pipeline run's service is on, whose SSH key its stages log in with.</summary>
    public Guid? GuestResourceId { get; init; }

    /// <summary>The versions a rotation of the guest's key works with.</summary>
    public AccessRotationContext? AccessRotation { get; init; }

    /// <summary>The time a scheduled run was for, which its limit is counted from.</summary>
    public DateTimeOffset? ScheduledFor { get; init; }

    /// <summary>A role as a pipeline run records it.</summary>
    public static string StageRole(string stage, string role) => $"{stage}/{role}";

    /// <summary>A stage's role, or any other, without its stage.</summary>
    public static string BareRole(string role) => role[(role.IndexOf('/') + 1)..];

    /// <summary>What a step of the stage sees: its own tool and only its own secrets, under their usual names.</summary>
    public RunContext ForStage(string name)
    {
        var stage = Stages?.SingleOrDefault(s => s.Name == name)
            ?? throw new InvalidOperationException($"The run has no stage {name}.");
        var prefix = StageRole(name, "");
        Dictionary<string, T> Own<T>(Dictionary<string, T> all) where T : notnull =>
            all.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(p => p.Key[prefix.Length..], p => p.Value, StringComparer.Ordinal);
        return this with
        {
            Stages = null,
            Stage = name,
            OpenTofu = stage.OpenTofu,
            Ansible = stage.Ansible,
            Secrets = Own(Secrets),
            RequestedSecretVersions = Own(RequestedSecretVersions),
            SecretVersions = Own(SecretVersions),
        };
    }

    /// <summary>What a step's work sees: <see cref="ForStage"/> of a stage's step, and otherwise the whole run.</summary>
    public RunContext ForStep(Data.Step? step) => step?.Stage is { } stage ? ForStage(stage) : this;

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static RunContext Parse(string json) =>
        JsonSerializer.Deserialize<RunContext>(json, Json) ?? throw new InvalidOperationException("The run has no context.");
}

/// <summary>One stage of a pipeline run, as it was built: exactly one of the two tools.</summary>
public sealed record StageContext(string Name, string Recipe, OpenTofuContext? OpenTofu, AnsibleContext? Ansible);

/// <summary>
/// A rotation of a guest's own key. <see cref="Current"/> adds the candidate; the candidate does
/// everything after that; <see cref="Remove"/> are the versions it takes off the guest.
/// </summary>
public sealed record AccessRotationContext(int Current, int Candidate, IReadOnlyList<int> Remove);

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
    /// <summary>Shows what a configure run will do, with no secrets and no contact with the guest.</summary>
    public const string Preview = "preview";
    public const string Playbook = "playbook";
    /// <summary>A person decides what a playbook's declared outcome should lead to.</summary>
    public const string OutcomeApproval = "outcome-approval";
    /// <summary>The one further run a person may approve after a declared outcome.</summary>
    public const string PlaybookApproved = "playbook-approved";
    /// <summary>A fresh login with the key in use alone, so a rotation starts from a key shown to work.</summary>
    public const string VerifyCurrent = "verify-current";
    /// <summary>Adds a candidate key beside the keys on a guest, with the key in use.</summary>
    public const string AuthorizeKey = "authorize-key";
    /// <summary>A fresh login with the candidate alone, and the privileges a run needs.</summary>
    public const string VerifyKey = "verify-key";
    /// <summary>A person makes the candidate the key runs use, with that evidence in front of them.</summary>
    public const string PromoteKey = "promote-key";
    /// <summary>A separate decision to take the old key off the guest.</summary>
    public const string ApproveRemoval = "approve-removal";
    public const string RemoveKey = "remove-key";
    /// <summary>A fresh login with the promoted key, and proof the old one is gone.</summary>
    public const string VerifyRemoval = "verify-removal";
    /// <summary>A login with the old key, which sshd has to refuse. Only its answer shows the key is gone.</summary>
    public const string ConfirmRefused = "confirm-refused";

    /// <summary>The steps a person decides, whatever the policy says.</summary>
    public static bool IsAccessDecision(string name) => name is PromoteKey or ApproveRemoval;
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

    public const string AnsibleSshKey = "ansible.ssh-key";
    public const string AnsibleRefusedKeys = "ansible.refused-keys";
    public const string AnsibleHostKey = "ansible.host-key";

    public static string Backend(string environmentVariable) => $"backend.{environmentVariable}";

    /// <summary>A guest's <c>setup.secrets</c> entry.</summary>
    public static string AnsibleVar(string name) => $"ansible.var.{name}";

    /// <summary>A guest's <c>setup.secretFiles</c> entry.</summary>
    public static string AnsibleFile(string name) => $"ansible.file.{name}";
}
