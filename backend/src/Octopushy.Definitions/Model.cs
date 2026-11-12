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

    /// <summary>Which binding fills each interface slot for its services: slot to the name of <c>labs/&lt;lab&gt;/interfaces/&lt;slot&gt;/&lt;name&gt;.yaml</c>.</summary>
    public IReadOnlyDictionary<string, string> Interfaces { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// What fills one interface slot, from <c>labs/&lt;lab&gt;/interfaces/&lt;slot&gt;/&lt;name&gt;.yaml</c>, for the installations that
/// name it. Octopushy knows nothing of what a slot is: a stage that declares it gets <paramref name="Inputs"/> as the extra var
/// named <paramref name="Slot"/>, and <paramref name="Secrets"/> as extra vars of their own. See docs/workflows.md#interfaces.
/// </summary>
public sealed record InterfaceBinding(
    string Lab,
    string Slot,
    string Name,
    string File,
    JsonObject Json,
    JsonObject Inputs,
    IReadOnlyDictionary<string, StoreReference> Secrets)
{
    public string Address => $"{Lab}/interfaces/{Slot}/{Name}";
}

/// <summary>
/// A VM or a Proxmox container, as its <see cref="Type"/> says. <paramref name="WorkflowReference"/> is what <c>guest.yaml</c>
/// names; <paramref name="Workflow"/> is the version it resolved to, a workflow whose <c>manages</c> is the guest's type, or
/// null when it resolved to none.
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
    /// <summary>
    /// <c>vm</c> or <c>container</c>, as <c>guest.yaml</c>'s <c>type</c> says; see <see cref="GuestTypes"/>. The one accessor of
    /// a guest's kind: it reads the definition, so there is no second copy to disagree with it.
    /// </summary>
    public string Type => Json["type"]?.GetValue<string>() ?? GuestTypes.Vm;

    /// <summary>How Proxmox lists the guest: <c>qemu</c> or <c>lxc</c>.</summary>
    public string ProxmoxKind => GuestTypes.ProxmoxKind(Type);

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

    /// <summary>
    /// Where people open the service and what uptime checks: its own <see cref="LinksOf">links</see>, then each of its
    /// <see cref="Names"/> as <c>https://&lt;name&gt;/</c> unless a link already opens that host. Never part of what a run does.
    /// </summary>
    public IReadOnlyList<string> Links
    {
        get
        {
            var links = LinksOf(Json);
            var hosts = links.Select(l => new Uri(l).IdnHost).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return links.Concat(Names.Where(n => !hosts.Contains(n.Name)).Select(n => n.Url)).Distinct(StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// The host names the service's stages set, from the input each stage names with <c>names</c>, for the stages that run:
    /// a stage whose interface slot is not bound sets none. What is not a host name, such as <c>_dmarc.example.com</c> or a
    /// wildcard, is left out; a trailing dot is dropped, and each name is kept once, in lower case.
    /// </summary>
    public IReadOnlyList<ServiceName> Names =>
        Stages.Where(s => s.Definition.Names is not null)
            .SelectMany(s => ServiceName.Read(new JsonObject { ["inputs"] = Json["inputs"]?.DeepClone() }, s.Definition.Names!)
                .Select(name => new ServiceName(name, s.Name, $"https://{name}/")))
            .DistinctBy(n => n.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Where people open the service, from <c>links</c> in <c>service.yaml</c>: shown in the Inventory and checked for uptime,
    /// never part of what a run does. Only absolute http and https URLs with a host are kept, and <c>tcp://host:port</c> for a
    /// service that does not speak HTTP.
    /// </summary>
    public static IReadOnlyList<string> LinksOf(JsonNode? definition) =>
        (definition?["links"] as JsonArray)?
            .Select(l => l is JsonValue v && v.TryGetValue<string>(out var text) ? text : null)
            .Where(l => Uri.TryCreate(l, UriKind.Absolute, out var uri) && uri.Host.Length > 0
                && (uri.Scheme is "http" or "https" || uri.Scheme == "tcp" && uri.Port is > 0 and <= 65535 && uri.PathAndQuery is "" or "/"))
            .Select(l => l!)
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];
}

/// <summary>A host name a service's <paramref name="Stage"/> sets, and the URL it is opened and checked at.</summary>
public sealed record ServiceName(string Name, string Stage, string Url)
{
    /// <summary>
    /// The host names at <paramref name="path"/> in <paramref name="root"/>: dotted property names, where <c>[]</c> after one
    /// goes through each item of its array. Anything missing or not a host name is skipped.
    /// </summary>
    public static IEnumerable<string> Read(JsonNode root, string path)
    {
        IEnumerable<JsonNode?> nodes = [root];
        foreach (var segment in path.Split('.'))
        {
            var each = segment.EndsWith("[]", StringComparison.Ordinal);
            var property = each ? segment[..^2] : segment;
            nodes = nodes.Select(n => (n as JsonObject)?[property]);
            if (each)
                nodes = nodes.SelectMany(n => (n as JsonArray) ?? []);
        }

        foreach (var node in nodes)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
                continue;
            var name = text.TrimEnd('.').ToLowerInvariant();
            if (name.Contains('.') && Uri.CheckHostName(name) == UriHostNameType.Dns)
                yield return name;
        }
    }
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
    IReadOnlyDictionary<string, StoreReference> SecretFiles)
{
    /// <summary>
    /// For a stage that declares an interface slot, the binding its installation names for it, whose inputs and secrets are in
    /// <see cref="Inputs"/> and <see cref="Secrets"/>. Null for any other stage. A stage whose slot is not bound is not run.
    /// </summary>
    public InterfaceBinding? Binding { get; init; }
}

public sealed record ImageSource(string Url, string ChecksumAlgorithm, string Checksum, string FileName);

/// <summary>
/// A template whose image Octopushy builds from a Nix flake output: <c>source.build</c>. The flake is <see cref="Flake"/> of the
/// workflow it names, at the version that resolved. See docs/built-templates.md.
/// </summary>
/// <param name="WorkflowReference">What <c>source.build.workflow</c> names, such as <c>nixos-host@1</c>.</param>
/// <param name="WorkflowCommit">
/// The commit of the workflows repository the workflow resolved to. For a workflow kept in the lab repository, which has no
/// release commit, <c>tree:</c> and a hash of the flake's folder, so only a change of the flake builds again.
/// </param>
/// <param name="WorkflowRepository">The workflows repository, or null for a workflow kept in the lab repository.</param>
/// <param name="LockPath">The lab's <c>flake.lock</c>, repository-relative, or null to use the workflow's own.</param>
/// <param name="LockSha256">The SHA-256 of the lock's bytes, or null without one.</param>
/// <param name="Key">The build key: the same key is never built twice. See <see cref="KeyOf"/>.</param>
public sealed record BuildSource(
    string WorkflowReference,
    string WorkflowId,
    string WorkflowVersion,
    string WorkflowCommit,
    string? WorkflowRepository,
    string Output,
    string? LockPath,
    string? LockSha256,
    string Key)
{
    /// <summary>The only system built for now. The key names it, so another can come later.</summary>
    public const string System = "x86_64-linux";

    /// <summary>
    /// Where the flake is in a run bundle. A workflow's folder names no flake today, so it is this folder of the workflows
    /// repository at the resolved release, or of the lab repository for workflows kept there.
    /// </summary>
    public const string Flake = "modules/nixos";

    public string WorkflowLabel => $"{WorkflowId}@{WorkflowVersion}";

    /// <summary>
    /// The lowercase hex SHA-256 of <c>"octopushy-image-build-v1\n" + commit + "\n" + output + "\n" + system + "\n" + (lock
    /// SHA-256 or "none")</c>, in UTF-8.
    /// </summary>
    public static string KeyOf(string workflowCommit, string output, string? lockSha256) =>
        Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(global::System.Text.Encoding.UTF8.GetBytes(
            "octopushy-image-build-v1\n" + workflowCommit + "\n" + output + "\n" + System + "\n" + (lockSha256 ?? "none"))));
}

/// <summary>
/// A built template's image as a provision run gets it: its SHA-256, its size, and the plain file name the agent puts it at,
/// <c>&lt;first 12 of the SHA-256&gt;-&lt;the name the build gave it&gt;</c>.
/// </summary>
public sealed record TemplateImage(string Sha256, long Size, string FileName)
{
    /// <summary>The longest name a build may give its image: the agent takes a <c>templateImage</c> name of at most 128 characters, and the prefix adds 13.</summary>
    public const int MaxBuiltFileName = 128 - 13;

    public static TemplateImage Of(string sha256, long size, string builtFileName) => new(sha256, size, $"{sha256[..12]}-{builtFileName}");
}

/// <param name="Build">For <c>source.build</c>, the build as it resolved; null for a downloaded image, or a build that did not resolve.</param>
public sealed record TemplateDefinition(
    string Id,
    string File,
    JsonObject Json,
    string? Os,
    ImageSource? Image,
    IReadOnlyList<string> AvailableOn,
    BuildSource? Build = null)
{
    /// <summary>False when the template has an error of its own: no guest using it is valid then.</summary>
    public bool Valid { get; init; } = true;

    /// <summary>The address its issues are filed under.</summary>
    public string Address => $"templates/{Id}";
}

/// <summary>A store key, optional exact version, and the resource whose definition uses it.</summary>
public sealed record StoreUse(string Key, string Consumer, string File, int? Version = null);
