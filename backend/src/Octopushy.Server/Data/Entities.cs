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

    /// <summary>
    /// The revision the head is: its commit together with what its workflow references resolved to. A new release tag can
    /// make a new head revision with the same commit.
    /// </summary>
    public Guid? HeadRevisionId { get; set; }

    /// <summary>The workflows repository the head's <c>octopushy.yaml</c> names, when it names one.</summary>
    public string? WorkflowsRepository { get; set; }

    public DateTimeOffset? WorkflowsLastFetchAt { get; set; }

    public string? WorkflowsLastFetchError { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Edits to the lab repository kept by Octopushy until they are published. The files live on a
/// Git ref in Octopushy's mirror; this row records where the draft started and where it is.
/// </summary>
public sealed class RepositoryDraft
{
    public Guid Id { get; set; }

    /// <summary>The repository source the draft was started from; a different source makes it stale.</summary>
    public Guid SourceId { get; set; }

    public required string Title { get; set; }

    /// <summary>The branch commit the draft's edits are relative to.</summary>
    public required string BaseCommit { get; set; }

    /// <summary>The draft's latest commit, never pushed.</summary>
    public required string HeadCommit { get; set; }

    /// <summary>Files left with conflict markers by an update; publishing waits until each is saved.</summary>
    public List<string> ConflictPaths { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string? PublishedCommit { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public string? PublishedBy { get; set; }
}

/// <summary>What a change from the UI is, and where it stands. See <see cref="ResourceChange"/>.</summary>
public static class ChangeKinds
{
    public const string Edit = "edit";
    /// <summary>An edit that moves the workflow reference to a newer major of the same workflow.</summary>
    public const string Upgrade = "upgrade";
    /// <summary>Deletes the guest's folder; its retire run follows under its policy.</summary>
    public const string Remove = "remove";
}

public static class ChangeStatuses
{
    /// <summary>Recorded, and being pushed: the push may or may not have reached the branch. Reconciliation settles it.</summary>
    public const string Committing = "committing";
    /// <summary>A push was attempted and its outcome could not be checked. Reconciliation settles it; a person may dismiss it.</summary>
    public const string Uncertain = "uncertain";
    /// <summary>
    /// A push was attempted, and none of its commits is on the branch (yet). Each sync looks again, by reachability, and takes a
    /// late arrival through the normal checks; a person may dismiss it.
    /// </summary>
    public const string Unknown = "unknown";
    /// <summary>A person settled an uncertain or unknown change as not committed, and is recorded with it.</summary>
    public const string Dismissed = "dismissed";
    public const string Committed = "committed";
    public const string Synced = "synced";
    public const string Planning = "planning";
    public const string WaitingForApproval = "waiting-for-approval";
    public const string Applying = "applying";
    public const string Applied = "applied";
    public const string Failed = "failed";
    /// <summary>Refused before anything was committed: the form was stale, it did not validate, or committing is not allowed.</summary>
    public const string Refused = "refused";
    /// <summary>Committed and synced, and the revision proposes no run for the resource: nothing it is built from changed.</summary>
    public const string NoChange = "no-change";
    /// <summary>A later commit set the resource otherwise, or restored it, before this change was applied: it is never applied.</summary>
    public const string Superseded = "superseded";

    public static bool IsFinal(string status) => status is Applied or Failed or Refused or NoChange or Superseded or Dismissed;

    /// <summary>
    /// Definitely not committed: refused before any push was attempted, or dismissed by a person. What a change prepared, such
    /// as generated values, can be cleaned up then and only then.
    /// </summary>
    public static bool IsNotCommitted(string status) => status is Refused or Dismissed;

    /// <summary>A push was attempted and has not been settled yet.</summary>
    public static bool IsUnsettled(string status) => status is Committing or Uncertain or Unknown;
}

/// <summary>
/// A structured edit made in the UI, written by Octopushy as one commit to the lab repository, then followed through the
/// revision it became and the runs that revision proposed for its resource. Git stays the source of truth: this row records
/// who asked for what and where it got to, never the definition itself.
/// </summary>
public sealed class ResourceChange
{
    public Guid Id { get; set; }

    /// <summary><see cref="ChangeKinds"/>.</summary>
    public required string Kind { get; set; }

    /// <summary><c>guest</c> or <c>service</c>.</summary>
    public required string ResourceKind { get; set; }

    public Guid ResourceId { get; set; }

    public required string Address { get; set; }

    public required string Author { get; set; }

    /// <summary>The commit the form was loaded from: the change is refused if a file it touches differs from it.</summary>
    public required string BaseCommit { get; set; }

    /// <summary>The commit's subject line.</summary>
    public required string Message { get; set; }

    /// <summary>The files the change writes or deletes, all inside the resource's own folder.</summary>
    public List<string> Files { get; set; } = [];

    /// <summary>
    /// What the change wrote, by section (<c>workflow</c>, <c>hardware</c>, <c>network</c>, <c>inputs</c>), as JSON: a later
    /// revision carries the change only while the resource still has these values. Null for a removal.
    /// </summary>
    public string? Edited { get; set; }

    /// <summary>The commit pushed; null when the change was refused, or while it is being pushed.</summary>
    public string? Commit { get; set; }

    /// <summary>The commit that set the resource otherwise before this change was applied.</summary>
    public string? SupersededBy { get; set; }

    /// <summary>
    /// Every commit an attempt prepared and was about to push, recorded before that push. The change's commit is the one of
    /// these the branch reaches; a commit's message is never used to find it.
    /// </summary>
    public List<string> CandidateCommits { get; set; } = [];

    public string? DismissedBy { get; set; }

    public DateTimeOffset? DismissedAt { get; set; }

    /// <summary>The first revision loaded that contains the commit.</summary>
    public Guid? RevisionId { get; set; }

    /// <summary>The runs that revision proposed for the resource.</summary>
    public List<Guid> RunIds { get; set; } = [];

    /// <summary><see cref="ChangeStatuses"/>.</summary>
    public string Status { get; set; } = ChangeStatuses.Committed;

    /// <summary>Why it was refused or failed, or what it waits for.</summary>
    public string? Detail { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? SyncedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A commit of the lab repository as Octopushy read it, with what each workflow reference resolved to. Its identity is the
/// source, the commit and <see cref="WorkflowsDigest"/>: a new release tag that changes a resolution is a new revision of the
/// same commit.
/// </summary>
public sealed class DefinitionRevision
{
    public Guid Id { get; set; }

    /// <summary>The configured source it was read from; see <see cref="RepositorySettings.SourceId"/>.</summary>
    public Guid SourceId { get; set; }

    /// <summary>Increases with every revision loaded, for ordering.</summary>
    public long Sequence { get; set; }

    public required string Commit { get; set; }

    /// <summary>The workflows repository its references were resolved in; null when workflows are the repository's own.</summary>
    public string? WorkflowsRepository { get; set; }

    /// <summary>The digest of <see cref="Resolutions"/>; empty when workflows are the repository's own.</summary>
    public string WorkflowsDigest { get; set; } = "";

    /// <summary>JSON array: each reference, and the workflow, version and commit it resolved to, or why none.</summary>
    public string Resolutions { get; set; } = "[]";

    public string? Subject { get; set; }

    public string? Author { get; set; }

    public DateTimeOffset? CommittedAt { get; set; }

    public DateTimeOffset LoadedAt { get; set; }

    public bool RepositoryValid { get; set; }

    public int ErrorCount { get; set; }

    public int WarningCount { get; set; }

    /// <summary>JSON array of issues.</summary>
    public string Issues { get; set; } = "[]";

    /// <summary>
    /// When the revision's inventory was applied: the desired inputs recorded and the runs proposed, in one transaction with
    /// the head moving to it. A head revision without it is applied again.
    /// </summary>
    public DateTimeOffset? InventoryAppliedAt { get; set; }
}

/// <summary>
/// The errors Octopushy recorded for a resource when it applied one revision, such as a removal that cannot be planned: what
/// that revision did to the resource, kept apart from the resource's current issues, which a later revision replaces.
/// </summary>
public sealed class RevisionIssues
{
    public Guid ResourceId { get; set; }

    public Guid RevisionId { get; set; }

    public string Issues { get; set; } = "[]";
}

/// <summary>
/// A release of a workflow in a workflows repository, from its tag <c>&lt;id&gt;/v&lt;M&gt;.&lt;m&gt;</c>. The commit is the one the tag
/// pointed to the first time Octopushy saw it, and it never changes: a tag that later points elsewhere is recorded in
/// <see cref="MovedTo"/> and not followed, and a deleted tag stays a release while its commit is in the mirror.
/// </summary>
public sealed class WorkflowReleaseRecord
{
    public required string Repository { get; set; }

    public required string WorkflowId { get; set; }

    public int Major { get; set; }

    public int Minor { get; set; }

    public required string Commit { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>Why the release cannot be used, such as a workflow.yaml that declares another version. Fixed with its commit.</summary>
    public string? Problem { get; set; }

    /// <summary>Where the tag points now when that is not <see cref="Commit"/>.</summary>
    public string? MovedTo { get; set; }

    public DateTimeOffset? MovedAt { get; set; }

    /// <summary>When the tag was last found missing from the repository; null while it is there.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public string Version => $"{Major}.{Minor}";

    public string Tag => $"{WorkflowId}/v{Version}";
}

/// <summary>
/// An operator's confirmation that a Proxmox installation's API endpoints may receive its
/// credentials. Revisions need no trust of their own; a new installation, or a new endpoint for
/// one, waits for this before any credential-bearing work is sent to it.
/// </summary>
public sealed class InstallationTrust
{
    /// <summary>The installation's address, <c>lab/installation</c>.</summary>
    public required string Address { get; set; }

    /// <summary>Every endpoint the installation declared when it was trusted, sorted.</summary>
    public List<string> Endpoints { get; set; } = [];

    public DateTimeOffset TrustedAt { get; set; }

    public required string TrustedBy { get; set; }

    /// <summary>
    /// The installation was observed only (<c>manage: false</c>) when it was trusted. Such trust covers
    /// observations alone: once the installation asks to be managed, it has to be trusted again.
    /// </summary>
    public bool ObservedOnly { get; set; }
}

public static class ResourceKinds
{
    public const string Lab = "lab";
    public const string Installation = "installation";
    public const string Guest = "guest";
    public const string Service = "service";
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

    /// <summary>For a guest or service, the workflow and version the latest revision resolved it to, such as <c>fedora-vm@2.3</c>.</summary>
    public string? Workflow { get; set; }

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

    /// <summary>When an installation was last sent to be observed; the schedule observes it again once this is older than the interval.</summary>
    public DateTimeOffset? ObserveRequestedAt { get; set; }

    /// <summary>Why the installation's latest observation did not succeed; cleared when one does.</summary>
    public string? ObserveError { get; set; }

    public DateTimeOffset? ObserveErrorAt { get; set; }

    /// <summary>
    /// An installation that said <c>manage: false</c> in its last definition that could be read. It stays
    /// so while its definition is invalid or missing; only a valid definition that asks to be managed ends it.
    /// </summary>
    public bool ObservedOnly { get; set; }

    /// <summary>
    /// The store keys of the tokens an observed-only installation has declared. They stay refused to every
    /// other installation's work while it is observed only, and until it is trusted again as managed.
    /// </summary>
    public List<string> ObservedOnlyStoreKeys { get; set; } = [];

    /// <summary>The endpoints an observed-only installation has declared, held like <see cref="ObservedOnlyStoreKeys"/>.</summary>
    public List<string> ObservedOnlyEndpoints { get; set; } = [];

    /// <summary>
    /// A reservation: the installation, node and VMID of the machine an approved provision plan of this guest
    /// creates, recorded when its apply is sent, and when it was first sent. It grants nothing by itself.
    /// Ownership is <see cref="BoundVmid"/>, given only when a verified VM shows that create worked. Until
    /// then the reservation lets this guest try its create again after an apply that did not finish, unless
    /// an observation from before <see cref="ReservedAt"/> already showed a VM at that VMID; see SafetyBoundary.
    /// It is cleared when the guest's VMID or node changes while it is unbound.
    /// </summary>
    public string? ReservedInstallation { get; set; }

    public string? ReservedNode { get; set; }

    public int? ReservedVmid { get; set; }

    public DateTimeOffset? ReservedAt { get; set; }

    /// <summary>
    /// When an apply of an approved plan that creates the reserved machine succeeded. Only with this, and a
    /// verified VM that carries Octopushy's tag and description, is the guest bound to it.
    /// </summary>
    public DateTimeOffset? ReservationAppliedAt { get; set; }
}

/// <summary>Where one of a guest's schedules stands. The definition itself stays in Git.</summary>
public sealed class GuestScheduleState
{
    public Guid ResourceId { get; set; }

    public required string Action { get; set; }

    public required string Cron { get; set; }

    /// <summary>The newest time this schedule has dealt with, so each comes up once.</summary>
    public DateTimeOffset LastSlot { get; set; }

    /// <summary>The run the last firing started.</summary>
    public Guid? LastRunId { get; set; }

    /// <summary>Why the last firing started nothing; null once one has.</summary>
    public string? Note { get; set; }

    public DateTimeOffset? NoteAt { get; set; }
}

/// <summary>
/// One version of Octopushy's key for a guest, and what is known about it on the guest. This is
/// kept apart from the provisioning hash, so what is authorized can change without a plan. The
/// store's current version says which one runs use; this says where each one stands.
/// </summary>
public sealed class GuestAccessKey
{
    public Guid ResourceId { get; set; }

    /// <summary>The store version holding the private half.</summary>
    public int Version { get; set; }

    public required string PublicKey { get; set; }

    public required string Fingerprint { get; set; }

    /// <summary>
    /// The version OpenTofu carries. It stays fixed while the machine exists: a changed key reboots a
    /// VM and replaces a container, so a change of key on a running guest never goes that way.
    /// </summary>
    public bool Bootstrap { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When an apply carrying this version finished. Cloud-init may still have to run.</summary>
    public DateTimeOffset? ProvisionedAt { get; set; }

    /// <summary>
    /// When a run holding this key last finished with a task done. A playbook can do that without
    /// connecting, so this says the key was in use. It is not proof of a login.
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    public Guid? LastUsedRunId { get; set; }

    /// <summary>When a rotation added this version beside what was on the guest.</summary>
    public DateTimeOffset? AuthorizedAt { get; set; }

    /// <summary>When a step that does nothing else logged in with this key alone, and what ran it.</summary>
    public DateTimeOffset? VerifiedAt { get; set; }

    public Guid? VerifiedRunId { get; set; }

    /// <summary>When a person made this the version runs use, and what they had to go on.</summary>
    public DateTimeOffset? PromotedAt { get; set; }

    public string? PromotedBy { get; set; }

    public string? PromotionEvidence { get; set; }

    /// <summary>When a login showed this version gone from the guest. Other keys there are not touched.</summary>
    public DateTimeOffset? RemovedAt { get; set; }
}

/// <summary>Where a guest's setup stands. The row exists once a guest's workflow has a setup stage.</summary>
public sealed class GuestConfiguration
{
    public Guid ResourceId { get; set; }

    /// <summary>The configure inputs of the latest revision; null when the guest's workflow has no setup stage any more.</summary>
    public string? DesiredHash { get; set; }

    public Guid? DesiredRevisionId { get; set; }

    /// <summary>The inputs the last automatic run was made for. Each hash gets one.</summary>
    public string? ProposedHash { get; set; }

    public string? AppliedHash { get; set; }

    public Guid? AppliedRevisionId { get; set; }

    public DateTimeOffset? AppliedAt { get; set; }

    public string? AppliedAction { get; set; }

    /// <summary>What the last successful run prepared, as JSON, for an action that reuses it.</summary>
    public string? Snapshot { get; set; }

    /// <summary>
    /// JSON: each store key the guest's <c>setup.secrets</c> and <c>setup.secretFiles</c> name
    /// without a version, and the active version its onChange action was last started with, or
    /// null when the key had none. A different active version starts that action once. Null
    /// until first recorded.
    /// </summary>
    public string? SecretVersions { get; set; }
}

public static class Operations
{
    public const string Provision = "provision";
    public const string Retire = "retire";
    public const string Configure = "configure";
    /// <summary>A service's stages: everything planned, one approval, then everything applied in order.</summary>
    public const string Pipeline = "pipeline";
    public const string RotateAccess = "rotate-access";
}

/// <summary>Why a run was started, where something depends on it.</summary>
public static class RunReasons
{
    public const string Schedule = "schedule";

    /// <summary>A store value the guest's setup names changed its active version.</summary>
    public const string Secret = "secret";
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

    /// <summary><c>revision</c>, <c>manual</c>, <c>schedule</c>, or <c>secret</c>.</summary>
    public required string Reason { get; set; }

    /// <summary>What exactly started the run, such as which store values changed. Fixed when it is created.</summary>
    public string? ReasonDetail { get; set; }

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

    /// <summary>The pipeline stage this step belongs to; null on every other kind of run and on a pipeline's approval.</summary>
    public string? Stage { get; set; }

    /// <summary><c>plan</c>, <c>approval</c>, <c>unprotect</c>, <c>apply</c>, <c>reprotect</c>, <c>verify</c>, or <c>verify-absent</c>.</summary>
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

    /// <summary>
    /// For a pipeline's approval: the document <see cref="PlanSha256"/> names, byte for byte. It is plain text
    /// because a digest has to survive storage, which <see cref="Outputs"/>, being jsonb, does not promise.
    /// </summary>
    public string? Document { get; set; }

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

/// <summary>
/// One execution of work on an agent: a step of a run, where a retry is a new attempt, or a
/// background observation of an installation, which belongs to no run.
/// </summary>
public sealed class Attempt
{
    public Guid Id { get; set; }

    /// <summary>The step the attempt executes; null for an observation.</summary>
    public Guid? StepId { get; set; }

    public Step? Step { get; set; }

    public Guid? RunId { get; set; }

    /// <summary>For an observation, the installation it observes.</summary>
    public Guid? ResourceId { get; set; }

    /// <summary>For an observation, the revision its context was built from; it is sent only once that revision is trusted.</summary>
    public Guid? RevisionId { get; set; }

    /// <summary>For an observation, what its assignment is built from; a step's attempt uses its run's context.</summary>
    public string? Context { get; set; }

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

    /// <summary>The built-in state version this OpenTofu attempt started from; 0 for none yet.</summary>
    public int? StateVersion { get; set; }

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

    /// <summary>Keep mTLS connected while refusing all new workload assignments.</summary>
    public bool Draining { get; set; }

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

/// <summary>
/// The OpenTofu state Octopushy keeps for a guest, keyed by resource so a rename keeps it. The
/// lock names the apply that may write. It is freed by that apply's result and is taken over
/// once the holder is no longer running, so a crashed apply cannot wedge the stack.
/// </summary>
public sealed class TofuState
{
    public Guid ResourceId { get; set; }

    /// <summary>Which pipeline stage of the resource this state is; empty for a guest's own.</summary>
    public string Stage { get; set; } = "";

    /// <summary>The current <see cref="TofuStateVersion.Version"/>; 0 before the first write.</summary>
    public int HeadVersion { get; set; }

    public Guid? LockAttemptId { get; set; }

    public DateTimeOffset? LockedAt { get; set; }
}

/// <summary>One encrypted OpenTofu state. An apply's snapshots replace each other in one version.</summary>
public sealed class TofuStateVersion
{
    public Guid Id { get; set; }

    public Guid ResourceId { get; set; }

    /// <summary>See <see cref="TofuState.Stage"/>.</summary>
    public string Stage { get; set; } = "";

    public int Version { get; set; }

    public long Serial { get; set; }

    public required string Lineage { get; set; }

    /// <summary>How many resources the state tracks.</summary>
    public int ResourceCount { get; set; }

    public required string Sha256 { get; set; }

    public int Size { get; set; }

    public required byte[] Ciphertext { get; set; }

    public required byte[] WrappedKey { get; set; }

    /// <summary>The apply that wrote this version; null for a restored one.</summary>
    public Guid? AttemptId { get; set; }

    public int? RestoredFrom { get; set; }

    public string? RestoredBy { get; set; }

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

/// <summary>What an installation's Proxmox API reported at one time, gathered in the background rather than by a run.</summary>
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

/// <summary>A separately revocable operator grant. Only its SHA-256 hash is retained.</summary>
public sealed class OperatorToken
{
    public Guid Id { get; set; }
    public int UserId { get; set; }
    public required string Name { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
