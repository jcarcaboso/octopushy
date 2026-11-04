using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Octopushy.Server.Changes;

/// <summary>
/// A secret of a new guest or service: an existing store key (<c>Store</c>), or a value Octopushy generates (<c>Generate</c>),
/// which the workflow must request for that secret in its <c>ui.secrets</c>. <c>Labels</c> and <c>Name</c> are what an
/// <c>agent-enrollment-token</c> is created with.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SecretChoice(string? Store = null, string? Generate = null, IReadOnlyList<string>? Labels = null, string? Name = null);

/// <summary>
/// A deploy from the workflow wizard: a new folder, written as one commit. <c>Kind</c> is <c>create-guest</c>, under the
/// installation <c>Parent</c> (<c>lab/installation</c>), or <c>create-service</c>, under the guest <c>Parent</c>.
/// <c>Workflow</c> is <c>id@M</c> or <c>id@M.m</c>. A guest needs <c>Host</c> and <c>Vmid</c>; <c>AccessUser</c> and
/// <c>HostKey</c> (a store key) are how a workflow's setup stage logs in. <c>ApproveAtDeploy</c> approves the first plan in
/// the same click, when it only creates. <c>BaseCommit</c> is the commit the wizard was loaded from. <c>ReviewedRelease</c>
/// and <c>ReviewDigest</c> are what the review returned: a deploy whose workflow release or files differ is refused.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeployRequest(
    string Kind,
    string BaseCommit,
    string Parent,
    string Name,
    string Workflow,
    string? Host = null,
    int? Vmid = null,
    string? Template = null,
    JsonObject? Hardware = null,
    JsonObject? Network = null,
    JsonObject? Inputs = null,
    IReadOnlyDictionary<string, SecretChoice>? Secrets = null,
    string? AccessUser = null,
    string? HostKey = null,
    bool ApproveAtDeploy = false,
    string? ReviewedRelease = null,
    string? ReviewDigest = null);

/// <summary>
/// A value a deploy generates when it is committed, as the review shows it: the secret, the store key it goes in, the
/// generator, and what it makes. The value itself is never shown.
/// </summary>
public sealed record GeneratedValueDto(string Secret, string Key, string Generator, string Detail);

/// <summary>A host of an installation: declared in <c>installation.yaml</c>, seen by the latest observation, or both.</summary>
public sealed record DeployHostDto(string Name, bool Declared, bool Observed, string? Status);

/// <summary>A guest a service can be deployed on.</summary>
public sealed record DeployGuestDto(string Address, string Name, string? Workflow, bool Valid);

/// <summary>
/// An installation a guest can be deployed to. <c>Managed</c> is false for one with <c>manage: false</c>, which
/// <c>Reason</c> explains: nothing can be deployed there. <c>AgentLabels</c> are its lab's, which an enrollment token a deploy
/// generates starts with. <c>VmidProblem</c> says why its free VMIDs cannot be known now, such as an observation that is
/// missing, failed, too old or incomplete; <c>Id</c> is the installation to observe again.
/// </summary>
public sealed record DeployInstallationDto(
    string Address,
    string Lab,
    string Name,
    string Type,
    bool Managed,
    string? Reason,
    IReadOnlyList<DeployHostDto> Hosts,
    IReadOnlyList<DeployGuestDto> Guests,
    IReadOnlyList<string> AgentLabels,
    DateTimeOffset? ObservedAt,
    Guid? Id = null,
    string? VmidProblem = null);

/// <summary>
/// Where the wizard can deploy, read at the head commit (<c>BaseCommit</c>): the installations and their hosts and guests, and
/// the templates. <c>BlockedReason</c> says why nothing can be deployed now.
/// </summary>
public sealed record DeployTargetsDto(string? BaseCommit, string? BlockedReason, IReadOnlyList<DeployInstallationDto> Installations, IReadOnlyList<string> Templates);

/// <summary>
/// The smallest VMID at or above <c>From</c> that is free on the installation now: not observed anywhere on it, not declared
/// at the head commit, and not held by a deploy in progress. Null, with <c>Problem</c>, when that cannot be known reliably.
/// </summary>
public sealed record NextVmidDto(string Installation, int? Vmid, int From, string? Problem = null);
