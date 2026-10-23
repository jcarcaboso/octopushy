using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// What a run's steps are built from, fixed when the run is created. It names secrets by their
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

    public long? RootDiskBytes { get; init; }

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static RunContext Parse(string json) =>
        JsonSerializer.Deserialize<RunContext>(json, Json) ?? throw new InvalidOperationException("The run has no context.");
}

public sealed record PolicyContext(string Approval, string ApprovalSetBy, string Destructive, string DestructiveSetBy);

public sealed record OpenTofuContext(
    string Stack,
    string Root,
    string Image,
    BackendContext Backend,
    JsonObject Variables,
    Dictionary<string, string> Env,
    Dictionary<string, string> SecretEnv);

public sealed record BackendContext(string Type, Dictionary<string, JsonNode?> Config, string? Workspace);

public sealed record ProxmoxContext(
    string Endpoint,
    bool VerifyTls,
    string? CaCertificateKey,
    string? Node,
    int? Vmid,
    string? Name,
    string? ExpectIpv4);

/// <summary>Step names, shared with the API.</summary>
public static class StepNames
{
    public const string Plan = "plan";
    public const string Approval = "approval";
    public const string Unprotect = "unprotect";
    public const string Apply = "apply";
    public const string Verify = "verify";
    public const string VerifyAbsent = "verify-absent";
    public const string Observe = "observe";
    public const string GrowRoot = "grow-root";
    public const string VerifyRoot = "verify-root";
}

/// <summary>The secret names executors look for.</summary>
public static class SecretNames
{
    public const string ProxmoxReadOnlyToken = "proxmox.read-only-token";
    public const string ProxmoxPrivilegedToken = "proxmox.privileged-token";
    /// <summary>Executor wire alias only; never a persisted role in new runs.</summary>
    public const string ProxmoxToken = "proxmox.token";
    public const string ProxmoxCa = "proxmox.ca";

    public static string Backend(string environmentVariable) => $"backend.{environmentVariable}";
}
