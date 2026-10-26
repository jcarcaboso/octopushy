namespace Octopushy.Server.Data;

/// <summary>Where the lab repository is and how it was last fetched. There is one row.</summary>
public sealed class RepositorySettings
{
    public int Id { get; set; } = 1;

    public string? Url { get; set; }

    public string Branch { get; set; } = "main";

    /// <summary>Rotates whenever the configured URL or branch changes, including disconnects.</summary>
    public Guid SourceId { get; set; } = Guid.NewGuid();

    public int PollSeconds { get; set; } = 60;

    /// <summary><c>none</c>, <c>token</c> (HTTPS), or <c>ssh</c>; the secret is the store key <c>system/git/credential</c>.</summary>
    public string CredentialKind { get; set; } = "none";

    /// <summary>The user name sent with an HTTPS token, such as <c>oauth2</c> for GitLab.</summary>
    public string? CredentialUsername { get; set; }

    public DateTimeOffset? LastFetchAt { get; set; }

    public string? LastFetchError { get; set; }

    public string? HeadCommit { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A commit of the lab repository as Octopushy read it.</summary>
public sealed class DefinitionRevision
{
    public Guid Id { get; set; }

    /// <summary>Null for legacy revisions whose configured source cannot be established.</summary>
    public Guid? SourceId { get; set; }

    public DateTimeOffset? TrustedAt { get; set; }

    public string? TrustedBy { get; set; }

    /// <summary>Increases with every revision loaded, for ordering.</summary>
    public long Sequence { get; set; }

    public required string Commit { get; set; }

    public string? Subject { get; set; }

    public string? Author { get; set; }

    public DateTimeOffset? CommittedAt { get; set; }

    public DateTimeOffset LoadedAt { get; set; }

    public bool RepositoryValid { get; set; }

    public int ErrorCount { get; set; }

    public int WarningCount { get; set; }

    /// <summary>JSON array of issues.</summary>
    public string Issues { get; set; } = "[]";
}

public static class ResourceKinds
{
    public const string Lab = "lab";
    public const string Installation = "installation";
    public const string Guest = "guest";
}

/// <summary>
/// A lab, installation, or guest known to Octopushy, keyed by a stable id. A resource outlives
/// the revisions that define it; its status is derived from these fields and its runs.
/// </summary>
public sealed class Resource
{
    public Guid Id { get; set; }

    public required string Address { get; set; }

    public required string Kind { get; set; }

    public string? ParentAddress { get; set; }

    public required string Lab { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>The latest revision that contains the resource.</summary>
    public Guid? LastRevisionId { get; set; }

    /// <summary>Set when the folder disappeared without the resource being retired.</summary>
    public DateTimeOffset? MissingSince { get; set; }

    /// <summary>Set once a retired guest's removal is verified.</summary>
    public DateTimeOffset? RetiredAt { get; set; }

    public bool Valid { get; set; }

    /// <summary>The definition file's content as JSON, from the latest revision.</summary>
    public string? Definition { get; set; }

    public string? DefinitionFile { get; set; }

    /// <summary>JSON array of the issues that affect this resource.</summary>
    public string Issues { get; set; } = "[]";

    public string? DesiredInputsHash { get; set; }

    public Guid? DesiredRevisionId { get; set; }

    public string? CheckedInputsHash { get; set; }

    public Guid? CheckedRevisionId { get; set; }

    public DateTimeOffset? CheckedAt { get; set; }

    public string? AppliedInputsHash { get; set; }

    public Guid? AppliedRevisionId { get; set; }

    public DateTimeOffset? AppliedAt { get; set; }

    /// <summary>
    /// The inputs the infrastructure was last shown to match: by a plan without changes, or by
    /// a successful apply. A guest is in sync when this equals its desired inputs.
    /// </summary>
    public string? SyncedInputsHash { get; set; }

    public DateTimeOffset? VerifiedAt { get; set; }

    /// <summary>OpenTofu outputs of the latest apply, as JSON.</summary>
    public string? Outputs { get; set; }

    /// <summary>The installation and VMID the guest is bound to after its first apply.</summary>
    public string? BoundInstallation { get; set; }

    public int? BoundVmid { get; set; }

    public DateTimeOffset? BoundAt { get; set; }

    public ManagedWorkerBinding? ManagedWorker { get; set; }
}

/// <summary>One scoped reporting credential and the latest root-filesystem reading per guest.</summary>
public sealed class GuestDiskUsage
{
    public Guid ResourceId { get; set; }

    /// <summary>SHA-256 of the reporting token. Null disables reporting without erasing the last reading.</summary>
    public string? TokenHash { get; set; }

    public int IntervalSeconds { get; set; } = 3600;

    public long? UsedBytes { get; set; }

    public DateTimeOffset? ReceivedAt { get; set; }
}

public static class Operations
{
    public const string Provision = "provision";
    public const string Retire = "retire";
    public const string Observe = "observe";
}

public static class RunStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string AwaitingApproval = "awaiting-approval";
    public const string Blocked = "blocked";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Uncertain = "uncertain";
    public const string Cancelled = "cancelled";

    public static bool IsTerminal(string status) => status is Succeeded or Failed or Cancelled;
}

/// <summary>One requested execution of an operation against one resource at a fixed revision.</summary>
public sealed class Run
{
    public Guid Id { get; set; }

    public long Number { get; set; }

    public Guid ResourceId { get; set; }

    public required string Operation { get; set; }

    public Guid? RevisionId { get; set; }

    public string? Commit { get; set; }

    /// <summary><c>revision</c>, <c>manual</c>, or <c>schedule</c>.</summary>
    public required string Reason { get; set; }

    public string Status { get; set; } = RunStatuses.Queued;

    public string? Detail { get; set; }

    public string? InputsHash { get; set; }

    /// <summary>JSON: what the run's steps are built from, without secret values.</summary>
    public string Context { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public List<Step> Steps { get; set; } = [];
}

public static class StepKinds
{
    public const string Agent = "agent";
    public const string Approval = "approval";
}

public static class StepStatuses
{
    public const string Pending = "pending";
    public const string Queued = "queued";
    public const string Running = "running";
    public const string AwaitingApproval = "awaiting-approval";
    public const string Blocked = "blocked";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Uncertain = "uncertain";
    public const string Skipped = "skipped";
    public const string Cancelled = "cancelled";

    public static bool IsDone(string status) => status is Succeeded or Skipped;
}

/// <summary>A named, observable unit of a run with its own outcome.</summary>
public sealed class Step
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    public Run? Run { get; set; }

    public int Ordinal { get; set; }

    /// <summary><c>plan</c>, <c>approval</c>, <c>unprotect</c>, <c>apply</c>, <c>verify</c>, <c>verify-absent</c>, or <c>observe</c>.</summary>
    public required string Name { get; set; }

    public required string Kind { get; set; }

    public string Status { get; set; } = StepStatuses.Pending;

    public string? Detail { get; set; }

    /// <summary>JSON outputs of the step's successful attempt.</summary>
    public string? Outputs { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public bool ApprovalRequired { get; set; }

    /// <summary><c>approved</c> or <c>rejected</c>.</summary>
    public string? Decision { get; set; }

    /// <summary><c>policy</c> for automatic approvals, otherwise the user's name.</summary>
    public string? DecidedBy { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public string? DecisionNote { get; set; }

    /// <summary>For an approval: the SHA-256 of the plan it approves.</summary>
    public string? PlanSha256 { get; set; }

    public List<Attempt> Attempts { get; set; } = [];
}

public static class AttemptStatuses
{
    public const string Queued = "queued";
    public const string Assigned = "assigned";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Uncertain = "uncertain";
    public const string Cancelled = "cancelled";

    public static bool IsActive(string status) => status is Assigned or Running;

    public static bool IsTerminal(string status) => status is Succeeded or Failed or Uncertain or Cancelled;
}

/// <summary>One execution of a step on an agent; a retry is a new attempt.</summary>
public sealed class Attempt
{
    public Guid Id { get; set; }

    public Guid StepId { get; set; }

    public Step? Step { get; set; }

    public Guid RunId { get; set; }

    public int Number { get; set; }

    public required string Executor { get; set; }

    public required string Action { get; set; }

    /// <summary>Whether the action changes infrastructure, so a lost outcome is uncertain.</summary>
    public bool Mutating { get; set; }

    public string Status { get; set; } = AttemptStatuses.Queued;

    /// <summary>Why a queued attempt has not been assigned yet.</summary>
    public string? WaitingReason { get; set; }

    public string[] RequiredLabels { get; set; } = [];

    /// <summary>The Proxmox host the attempt acts on, for its concurrency limit.</summary>
    public string? HostKey { get; set; }

    public int? HostLimit { get; set; }

    /// <summary>The agent that prepared earlier work for this run, preferred when available.</summary>
    public Guid? PreferredAgentId { get; set; }

    public Guid? AgentId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? AssignedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public string? Error { get; set; }

    public string? Outputs { get; set; }

    /// <summary>How many log lines the server has stored.</summary>
    public long LogLines { get; set; }

    /// <summary>The result arrived after the lease had expired.</summary>
    public bool LateResult { get; set; }
}

public sealed class ArtifactRecord
{
    public Guid Id { get; set; }

    public Guid AttemptId { get; set; }

    public required string Name { get; set; }

    public required string Sha256 { get; set; }

    public long Size { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AgentRecord
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public string[] Labels { get; set; } = [];

    public int MaxConcurrency { get; set; } = 2;

    public bool Disabled { get; set; }

    public DateTimeOffset EnrolledAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    public DateTimeOffset? ConnectedAt { get; set; }

    public string? Version { get; set; }

    public string[] Executors { get; set; } = [];

    public string? ToolRuntime { get; set; }

    public required string CertificateSerial { get; set; }

    /// <summary>Accepted until the agent connects with its renewed certificate.</summary>
    public string? PreviousCertificateSerial { get; set; }

    public DateTimeOffset CertificateExpiresAt { get; set; }
}

public sealed class EnrollmentToken
{
    public Guid Id { get; set; }

    public required string TokenHash { get; set; }

    public string? Name { get; set; }

    public string[] Labels { get; set; } = [];

    public int MaxConcurrency { get; set; } = 2;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? UsedAt { get; set; }

    public Guid? AgentId { get; set; }
}

public static class Visibilities
{
    public const string Plain = "plain";
    public const string Secret = "secret";
}

public sealed class StoreEntry
{
    public required string Key { get; set; }

    public string Visibility { get; set; } = Visibilities.Secret;

    public string? Description { get; set; }

    public int CurrentVersion { get; set; }

    /// <summary>Allocation counter; staging a candidate does not move CurrentVersion.</summary>
    public int LatestVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One encrypted value of a store entry.</summary>
public sealed class StoreVersion
{
    public required string Key { get; set; }

    public int Version { get; set; }

    /// <summary>Nonce, ciphertext, and tag under the version's data key.</summary>
    public required byte[] Ciphertext { get; set; }

    /// <summary>The data key, encrypted under the key-encryption key.</summary>
    public required byte[] WrappedKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ActivatedAt { get; set; }

    public string? ActivatedBy { get; set; }

    public string? ActivationNote { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public string? RevokedBy { get; set; }

    public string? RevocationNote { get; set; }
}

/// <summary>Audit record of a secret released to an attempt; never holds the value.</summary>
public sealed class SecretResolution
{
    public long Id { get; set; }

    public required string Key { get; set; }

    public int Version { get; set; }

    public Guid AttemptId { get; set; }

    public Guid AgentId { get; set; }

    public DateTimeOffset ResolvedAt { get; set; }
}

/// <summary>What an installation's Proxmox API reported at one time.</summary>
public sealed class Observation
{
    public Guid Id { get; set; }

    public Guid ResourceId { get; set; }

    public Guid? AttemptId { get; set; }

    public DateTimeOffset ObservedAt { get; set; }

    public required string Data { get; set; }
}

public sealed class User
{
    public int Id { get; set; }

    public required string Username { get; set; }

    public required string PasswordHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Immutable guest/generation ownership and public bootstrap evidence. Never stores a private key or enrollment token.</summary>
public sealed class ManagedWorkerBinding
{
    public Guid ResourceId { get; set; }
    public Guid Generation { get; set; }
    public required string Installation { get; set; }
    public required string Host { get; set; }
    public int Vmid { get; set; }
    public Guid RevisionId { get; set; }
    public Guid RunId { get; set; }
    public Guid? PrepareAttemptId { get; set; }
    public Guid? AgentId { get; set; }
    public string? CsrDer { get; set; }
    public string? CertificatePem { get; set; }
    public string? CaCertificatePem { get; set; }
    public required string ServerUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PreparedAt { get; set; }
    public DateTimeOffset? InstalledAt { get; set; }
    public DateTimeOffset? AuthenticatedAt { get; set; }
    public DateTimeOffset? ReadyAt { get; set; }
}
