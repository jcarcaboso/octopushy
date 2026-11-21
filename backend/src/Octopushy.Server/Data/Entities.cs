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

    /// <summary>The exact external source an operator confirmed; never learned from a commit alone.</summary>
    public string? ConfirmedWorkflowsRepository { get; set; }
    public string? ConfirmedWorkflowsCredentialKey { get; set; }
    public int? ConfirmedWorkflowsCredentialVersion { get; set; }
    public DateTimeOffset? WorkflowsConfirmedAt { get; set; }
    public string? WorkflowsConfirmedBy { get; set; }

    /// <summary>A declaration waiting for explicit confirmation, scoped to SourceId and its discovered commit.</summary>
    public string? PendingWorkflowsRepository { get; set; }
    public string? PendingWorkflowsCredentialKey { get; set; }
    public int? PendingWorkflowsCredentialVersion { get; set; }
    public string? PendingWorkflowsCommit { get; set; }

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
    /// <summary>Deletes the guest's folder, and Octopushy forgets the guest and leaves its machine as it is: no retire run.</summary>
    public const string Detach = "detach";
    /// <summary>Writes a new guest's folder, from the workflow wizard.</summary>
    public const string CreateGuest = "create-guest";
    /// <summary>Writes a new service's folder under a guest, from the workflow wizard.</summary>
    public const string CreateService = "create-service";

    public static bool IsCreate(string kind) => kind is CreateGuest or CreateService;
}

/// <summary>
/// Where a deploy's "approve the plan in the same click" stands. It covers the change's first plan only, and only when that
/// plan does nothing but create. See docs/changes-from-the-ui.md.
/// </summary>
public static class PreApprovals
{
    /// <summary>Asked for at deploy; no plan has been decided yet.</summary>
    public const string Requested = "requested";
    /// <summary>The first plan was approved at deploy, as the operator who deployed.</summary>
    public const string Approved = "approved";
    /// <summary>The first plan did not qualify, so it waits for approval as usual; the detail says why.</summary>
    public const string Declined = "declined";
    /// <summary>The policy approved the first plan on its own, so nothing waited.</summary>
    public const string NotNeeded = "not-needed";
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
    /// <summary>
    /// A person stopped waiting for an uncertain or unknown change, and is recorded with it. It proves nothing: a push was
    /// attempted, so each sync still looks for its commits, and one that arrives moves it on.
    /// </summary>
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

    public static bool IsFinal(string status) => status is Applied or Failed or Refused or NoChange or Superseded;

    /// <summary>The statuses <see cref="IsFinal"/> holds for, for queries.</summary>
    public static readonly string[] Final = [Applied, Failed, Refused, NoChange, Superseded];

    /// <summary>
    /// Definitely not committed: refused before any push was attempted. Nothing else proves it, a dismissal included. What a
    /// change prepared, such as generated values, can be cleaned up then and only then.
    /// </summary>
    public static bool IsNotCommitted(ResourceChange change) => change.Status == Refused && change.CandidateCommits.Count == 0;
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

    /// <summary>The guest or service; for a create, null until the revision that holds the new folder is loaded.</summary>
    public Guid? ResourceId { get; set; }

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

    /// <summary>
    /// Every store key the files this change writes reference, recorded before its first push. Each attempt's candidate writes
    /// exactly those files, so this is what any of its commits would bring to the branch; cleanup reads it when a candidate's
    /// objects are not in the mirror. Null on a change recorded before it was kept, which cleanup treats as unknown.
    /// </summary>
    public List<string>? StoreKeys { get; set; }

    /// <summary>When reconciliation last checked the change against the branch; it checks the longest unchecked first.</summary>
    public DateTimeOffset? CheckedAt { get; set; }

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

    /// <summary>For a new guest, the VMID it declares: held against other deploys until the change is settled.</summary>
    public int? Vmid { get; set; }

    /// <summary><see cref="PreApprovals"/>, or null when the deploy did not ask to approve its plan in the same click.</summary>
    public string? PreApproval { get; set; }

    /// <summary>What happened to the pre-approval, such as why the plan did not qualify.</summary>
    public string? PreApprovalDetail { get; set; }

    /// <summary>The run whose plan the pre-approval was decided for. It is decided once, for that run only.</summary>
    public Guid? PreApprovalRunId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? SyncedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Where a value a deploy generated stands. See <see cref="GeneratedValue"/>.</summary>
public static class GeneratedValueStates
{
    /// <summary>Recorded before it is made: nothing exists yet.</summary>
    public const string Planned = "planned";
    /// <summary>Made, and owned by its change: the store version it wrote, the token it created.</summary>
    public const string Created = "created";
    /// <summary>Removed by cleanup: its store version deleted and its unused token revoked.</summary>
    public const string Removed = "removed";
    /// <summary>Left in place for good, because someone else wrote the key since, or it has execution history.</summary>
    public const string Kept = "kept";
}

/// <summary>
/// One value a deploy generated, and exactly what it owns: the store version it wrote, which carries this value's id as its
/// <see cref="StoreVersion.GenerationId"/>, and the enrollment token it created. Each is recorded in the same save that makes
/// it. Cleanup removes only that version, while it is still the one present, and only an unused token; nothing someone else
/// wrote is ever touched.
/// </summary>
public sealed class GeneratedValue
{
    public Guid Id { get; set; }

    public Guid ChangeId { get; set; }

    /// <summary>The secret of the definition it fills.</summary>
    public required string Secret { get; set; }

    public required string Key { get; set; }

    public required string Generator { get; set; }

    /// <summary><see cref="GeneratedValueStates"/>.</summary>
    public string State { get; set; } = GeneratedValueStates.Planned;

    /// <summary>The store version it wrote; null until it is written.</summary>
    public int? StoreVersion { get; set; }

    /// <summary>The description it wrote the entry with, naming its change, for people. Ownership is the version's generation id.</summary>
    public required string StoreDescription { get; set; }

    /// <summary>The enrollment token it created, for <c>agent-enrollment-token</c>.</summary>
    public Guid? TokenId { get; set; }

    /// <summary>Cleanup was due and did not finish; each sync tries again.</summary>
    public bool CleanupPending { get; set; }

    public string? CleanupError { get; set; }

    public int CleanupAttempts { get; set; }

    /// <summary>What cleanup did, or why it left the value.</summary>
    public string? Detail { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

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

    /// <summary>
    /// The identity of the resource's current desired work: new each time new work is recorded, kept while the same work is
    /// proposed again. The run that stands for the work carries it.
    /// </summary>
    public Guid? DesiredOccurrence { get; set; }

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

    /// <summary>
    /// When a person detached the guest: Octopushy forgot it and left its machine as it was, with <see cref="RetiredAt"/>
    /// set at the same time. A guest retired without it was removed by a retire run, and its machine verified absent.
    /// </summary>
    public DateTimeOffset? DetachedAt { get; set; }

    /// <summary>Who detached the guest, or <c>repository</c> when a detach change's commit did.</summary>
    public string? DetachedBy { get; set; }

    /// <summary>
    /// When a person attached the guest to a machine Octopushy did not create (<c>machine: attached</c>), and who: the binding
    /// it made, to <see cref="BoundVmid"/>, comes from them and not from a confirmed creation.
    /// </summary>
    public DateTimeOffset? AttachedAt { get; set; }

    public string? AttachedBy { get; set; }

    /// <summary>When an installation was last sent to be observed; the schedule observes it again once this is older than the interval.</summary>
    public DateTimeOffset? ObserveRequestedAt { get; set; }

    /// <summary>Why the installation's latest observation did not succeed; cleared when one does. Read by people: see <see cref="ObserveBlockReason"/>.</summary>
    public string? ObserveError { get; set; }

    public DateTimeOffset? ObserveErrorAt { get; set; }

    /// <summary>
    /// Why the latest observation was not sent, rather than failed, where something depends on it: one of
    /// <see cref="ObserveBlockReasons"/>, or null. Set and cleared with <see cref="ObserveError"/>.
    /// </summary>
    public string? ObserveBlockReason { get; set; }

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

    /// <summary>
    /// Why the guest's desired provisioning has no run yet, such as the image of a built template that is not built: shown
    /// on the guest, and cleared once its run is proposed.
    /// </summary>
    public string? ProvisionWaiting { get; set; }
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
/// One version of a guest's SSH host key, when the key is Octopushy's: the public half, which configure runs pin. The
/// private half is the store version <see cref="Version"/> of the secret <c>system/guest-host-key/&lt;resource id&gt;</c>,
/// released only to the plan of a provision run, which puts it in the guest's cloud-init snippet. The store's current
/// version is the one a plan carries; the newest version an apply delivered is the pin.
/// </summary>
public sealed class GuestHostKey
{
    public Guid ResourceId { get; set; }

    /// <summary>The store version holding the private half.</summary>
    public int Version { get; set; }

    public required string PublicKey { get; set; }

    public required string Fingerprint { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When an apply carrying this version was first handed to an agent, and by which run. From then on a machine may hold
    /// it, on its disks, its cloud-init drive or a backup, whatever the apply's outcome, so no other machine is given it.
    /// It is set before the apply can run, and is not proof of anything; <see cref="DeliveredAt"/> is.
    /// </summary>
    public DateTimeOffset? SpentAt { get; set; }

    public Guid? SpentRunId { get; set; }

    /// <summary>
    /// When an apply carrying this version finished, and which run it was. From then on configure runs pin it, until a
    /// newer version is delivered, as when the machine is replaced.
    /// </summary>
    public DateTimeOffset? DeliveredAt { get; set; }

    public Guid? DeliveredRunId { get; set; }
}

/// <summary>
/// The key Octopushy uploads snippets to an installation's hosts with, through the SFTP-only account
/// <c>deploy/proxmox/snippets.sh</c> creates. The private half is the secret at
/// <c>system/installation-snippets/&lt;installation address&gt;</c>, released only to provision applies.
/// </summary>
public sealed class InstallationSnippetKey
{
    /// <summary>The installation's address, such as <c>home/pve01</c>.</summary>
    public required string Installation { get; set; }

    public required string PublicKey { get; set; }

    public required string Fingerprint { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
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

    /// <summary>
    /// JSON: what this guest's own setup playbooks reported, by report name, each with the address its run connected to, the
    /// run and when. Only names its workflow declares, matching their patterns. Cleared when its machine is created or replaced.
    /// See <see cref="Orchestration.GuestReports"/>.
    /// </summary>
    public string? Reports { get; set; }

    /// <summary>
    /// JSON: what this guest's peers had reported when its onChange action was last started, as that run got it. A different
    /// value starts that action once. Null until first recorded.
    /// </summary>
    public string? PeerReports { get; set; }
}

public static class Operations
{
    public const string Provision = "provision";
    public const string Retire = "retire";
    public const string Configure = "configure";
    /// <summary>A service's stages: everything planned, one approval, then everything applied in order.</summary>
    public const string Pipeline = "pipeline";
    public const string RotateAccess = "rotate-access";
    /// <summary>A person attaching a guest to a machine Octopushy did not create: verify it, protect it, bind to it.</summary>
    public const string Attach = "attach";
}

/// <summary>Why a run was started, where something depends on it.</summary>
public static class RunReasons
{
    /// <summary>A revision of the lab repository proposed it.</summary>
    public const string Revision = "revision";

    /// <summary>A person started it.</summary>
    public const string Manual = "manual";

    public const string Schedule = "schedule";

    /// <summary>A store value the guest's setup names changed its active version.</summary>
    public const string Secret = "secret";

    /// <summary>A guest this guest's workflow names among its peers reported a new value: see <see cref="Orchestration.GuestReports"/>.</summary>
    public const string Peer = "peer";
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

/// <summary>Why a run was ended, where something depends on it. See <see cref="Run.EndReason"/>.</summary>
public static class RunEndReasons
{
    /// <summary>Cancelled before it changed anything, because a later revision proposes the resource's work again.</summary>
    public const string Superseded = "superseded";
}

/// <summary>Why an installation's observation was not sent, rather than failed. See <see cref="Resource.ObserveBlockReason"/>.</summary>
public static class ObserveBlockReasons
{
    /// <summary>A credential it needs is unavailable: nothing was sent, and the fix is in Variables.</summary>
    public const string Credentials = "credentials";
}

/// <summary>Why a run is blocked, where something depends on it. See <see cref="Run.BlockReason"/>.</summary>
public static class RunBlockReasons
{
    /// <summary>Its installation, or the endpoint it would send credentials to, waits for an operator's trust.</summary>
    public const string InstallationTrust = "installation-trust";
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

    /// <summary>What happened, for a person to read. Nothing decides on it: see <see cref="EndReason"/> and <see cref="BlockReason"/>.</summary>
    public string? Detail { get; set; }

    /// <summary>Why the run was ended, where something depends on it: one of <see cref="RunEndReasons"/>, or null.</summary>
    public string? EndReason { get; set; }

    /// <summary>
    /// Why the run is blocked, where something depends on it: one of <see cref="RunBlockReasons"/>, or null. It holds only
    /// while the status is <see cref="RunStatuses.Blocked"/>: each way into that status sets it, and a detail written while it
    /// stays blocked clears it.
    /// </summary>
    public string? BlockReason { get; set; }

    public string? InputsHash { get; set; }

    /// <summary>For a provision or retire run proposed for a resource's desired work, that work's occurrence.</summary>
    public Guid? Occurrence { get; set; }

    /// <summary>JSON: what the run's steps are built from, without secret values.</summary>
    public string Context { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public List<Step> Steps { get; set; } = [];
}

/// <summary>What an approval step's <see cref="Step.Decision"/> is, and who made it when no person did.</summary>
public static class StepDecisions
{
    public const string Approved = "approved";
    public const string Rejected = "rejected";

    /// <summary>The <see cref="Step.DecidedBy"/> of a decision the policy made on its own.</summary>
    public const string ByPolicy = "policy";
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

    /// <summary>The result arrived after the lease had expired.</summary>
    public bool LateResult { get; set; }

    /// <summary>
    /// The server settled the attempt without its outputs, because they could not be kept: it had the attempt's result, and
    /// what it did with it stands. No other result is taken for it.
    /// </summary>
    public bool OutputsWithheld { get; set; }

    /// <summary>
    /// The SHA-256 of the result the server took, as the agent sent it. The same result sent again, as an agent does until it
    /// is acknowledged, changes nothing. For an attempt the server cancelled, the agent's report on it, which settles
    /// nothing and ends its log.
    /// </summary>
    public string? ResultSha256 { get; set; }
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

public static class ImageBuildStatuses
{
    public const string Queued = "queued";
    public const string Building = "building";
    public const string Built = "built";
    public const string Failed = "failed";
    /// <summary>Its file was removed by retention; a key that is needed again is built again.</summary>
    public const string Deleted = "deleted";

    /// <summary>Whether a build of the key is waiting for an agent or under way.</summary>
    public static bool InProgress(string status) => status is Queued or Building;
}

/// <summary>
/// The image of one build key of a built template: queued, built and kept on the server, failed, or deleted by retention. A key
/// is built once; its attempts are <c>nix</c> <c>build</c> attempts that belong to no run and no installation. See
/// docs/built-templates.md.
/// </summary>
public sealed class ImageBuild
{
    public Guid Id { get; set; }

    /// <summary>The build key, from <see cref="Definitions.BuildSource.KeyOf"/>.</summary>
    public required string Key { get; set; }

    /// <summary>The template that asked for it first; another with the same key shares it.</summary>
    public required string TemplateId { get; set; }

    public required string WorkflowId { get; set; }

    public required string WorkflowVersion { get; set; }

    /// <summary>The workflows repository's commit, or <c>tree:</c> and a hash for a workflow kept in the lab repository.</summary>
    public required string WorkflowCommit { get; set; }

    /// <summary>The workflows repository, or null for a workflow kept in the lab repository.</summary>
    public string? WorkflowRepository { get; set; }

    public required string Output { get; set; }

    /// <summary>The lab's <c>flake.lock</c>, repository-relative, or null for the workflow's own.</summary>
    public string? LockPath { get; set; }

    /// <summary>The revision whose files the build gets: the lab repository at its commit, with the workflow's release.</summary>
    public Guid RevisionId { get; set; }

    public required string RevisionCommit { get; set; }

    public string Status { get; set; } = ImageBuildStatuses.Queued;

    /// <summary>The current attempt.</summary>
    public Guid? AttemptId { get; set; }

    /// <summary>Attempts since the build was last queued, for retries after a lost agent.</summary>
    public int AttemptCount { get; set; }

    /// <summary>The image's SHA-256, once uploaded.</summary>
    public string? Sha256 { get; set; }

    public long? Size { get; set; }

    /// <summary>The image's file name as the agent reported it, a plain name.</summary>
    public string? FileName { get; set; }

    /// <summary>The attempt whose upload <see cref="Sha256"/>, <see cref="Size"/> and <see cref="FileName"/> are from.</summary>
    public Guid? UploadAttemptId { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? BuiltAt { get; set; }

    /// <summary>When a provision run last took it, or it was built.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
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

    /// <summary>
    /// JSON object: why each executor the agent knows and does not offer is missing, as it said last, cleaned (see
    /// <see cref="Agents.AgentAvailability"/>). Null when it gave none. Written only when it changes.
    /// </summary>
    public string? Unavailable { get; set; }

    public string? ToolRuntime { get; set; }

    public required string CertificateSerial { get; set; }

    /// <summary>Accepted until the agent connects with its renewed certificate.</summary>
    public string? PreviousCertificateSerial { get; set; }

    public DateTimeOffset CertificateExpiresAt { get; set; }

    /// <summary><see cref="AgentUpdateStates"/>: an update asked for and not settled, or one that failed. Null otherwise.</summary>
    public string? UpdateState { get; set; }

    /// <summary>When the update was asked for: the queue's order.</summary>
    public DateTimeOffset? UpdateRequestedAt { get; set; }

    /// <summary>The operator who asked: the change is committed and approved as them.</summary>
    public string? UpdateRequestedBy { get; set; }

    /// <summary>The version the update sets, the server's own when it was started.</summary>
    public string? UpdateTargetVersion { get; set; }

    /// <summary>The change that carries the update, once it is committed.</summary>
    public Guid? UpdateChangeId { get; set; }

    /// <summary>Why the update failed, for people.</summary>
    public string? UpdateDetail { get; set; }

    /// <summary>Whether the update paused the agent, so it lifts that pause and never one a person set.</summary>
    public bool UpdatePaused { get; set; }

    /// <summary>
    /// The version the agent reported in its Hello when its latest update started, kept once the update ends: what Revert
    /// brings back. Null when no update has started.
    /// </summary>
    public string? UpdatePreviousVersion { get; set; }

    /// <summary>The executors the agent offered when its update started, sorted: the new version must offer each of them.</summary>
    public string[]? UpdatePreviousExecutors { get; set; }

    /// <summary>Whether the update asked for brings back <see cref="UpdatePreviousVersion"/> instead of the server's version.</summary>
    public bool UpdateRevert { get; set; }

    /// <summary>
    /// The guest the update changes, recorded under the engine's lock when the update is claimed, before its change is
    /// committed: from then on dispatch holds every change to it until the agent is idle. Cleared with the update.
    /// </summary>
    public Guid? UpdateResourceId { get; set; }

    /// <summary>
    /// <see cref="AgentUpdateHolds"/>: why dispatch holds a change to the guest that may restart the agent, or null. Written by
    /// dispatch, only when it changes.
    /// </summary>
    public string? UpdateHold { get; set; }

    /// <summary>The attempt on the agent that <see cref="UpdateHold"/> waits for.</summary>
    public Guid? UpdateHoldAttemptId { get; set; }

    /// <summary>When the last step of the update's run that may restart the agent was handed out: the deadline counts from here.</summary>
    public DateTimeOffset? UpdateReleasedAt { get; set; }

    /// <summary>
    /// The agent's connection when its update was claimed, if it had one (<see cref="Agents.AgentSession.Id"/>): another
    /// connection since is a new process, whose Hello must offer every executor the agent offered then. Recorded once, so later
    /// steps handed out for the guest never move it.
    /// </summary>
    public Guid? UpdateBaselineConnection { get; set; }

    /// <summary><see cref="AgentUpdateFailures"/>: why the update failed, when it did. <see cref="UpdateDetail"/> says it for people.</summary>
    public string? UpdateFailure { get; set; }

    /// <summary>
    /// When it was told offline (<see cref="NotificationKinds.AgentOffline"/>); null once told online again, or never told.
    /// Written only by <see cref="Agents.AgentPresence"/>.
    /// </summary>
    public DateTimeOffset? OfflineNotifiedAt { get; set; }
}

/// <summary>Where an agent's update stands, when one was asked for. See docs/worker-routing.md.</summary>
public static class AgentUpdateStates
{
    public const string Queued = "queued";
    public const string Updating = "updating";
    public const string Failed = "failed";
}

/// <summary>Where an update in flight stands, shown with <see cref="AgentUpdateStates.Updating"/>. Derived, never stored.</summary>
public static class AgentUpdatePhases
{
    /// <summary>Its change is being committed, or its run works on the steps before the one that restarts the agent.</summary>
    public const string RollingOut = "rolling-out";
    /// <summary>A run of its change waits for a person's approval. No deadline runs.</summary>
    public const string AwaitingApproval = "awaiting-approval";
    /// <summary>A run of its change is blocked, by trust or a credential. No deadline runs.</summary>
    public const string Blocked = "blocked";
    /// <summary>The step that restarts the agent waits for the agent's other work to finish. No deadline runs.</summary>
    public const string WaitingForIdle = "waiting-for-idle";
    /// <summary>
    /// The step that restarts the agent waits for the agent to confirm that a change whose lease ran out no longer executes
    /// (<see cref="AgentUpdateHolds.UnconfirmedChange"/>). No deadline runs.
    /// </summary>
    public const string WaitingForUnconfirmedChange = "waiting-for-unconfirmed-change";
    /// <summary>The step that restarts the agent was handed out: the agent has until the deadline to come back on the target.</summary>
    public const string Restarting = "restarting";
}

/// <summary>Why dispatch holds a change to the guest of an agent being updated. See <see cref="AgentRecord.UpdateHold"/>.</summary>
public static class AgentUpdateHolds
{
    /// <summary>An attempt is assigned to the agent or running on it.</summary>
    public const string Busy = "busy";

    /// <summary>
    /// A change on the agent whose lease ran out, or that a person force stopped, and that the agent has not said it stopped:
    /// it may still execute, and one late result may still settle it. A Hello or heartbeat from the agent that does not list
    /// it, or its result, ends the wait; cancelling the update's run gives the update up.
    /// </summary>
    public const string UnconfirmedChange = "unconfirmed-change";
}

/// <summary>Why an agent's update failed. Stored on the agent and shown as is; its sentence is in <c>UpdateDetail</c>.</summary>
public static class AgentUpdateFailures
{
    /// <summary>The server stopped between claiming the update and recording its change.</summary>
    public const string ChangeNotRecorded = "change-not-recorded";
    /// <summary>When its turn came, the agent could no longer be updated from here.</summary>
    public const string Unavailable = "unavailable";
    /// <summary>Its change was not committed.</summary>
    public const string CommitRefused = "commit-refused";
    /// <summary>Its change is gone.</summary>
    public const string ChangeGone = "change-gone";
    /// <summary>A run of its change ended uncertain.</summary>
    public const string RunUncertain = "run-uncertain";
    /// <summary>Its change failed: a run failed or was cancelled.</summary>
    public const string ChangeFailed = "change-failed";
    /// <summary>Its change was refused or dismissed.</summary>
    public const string ChangeRefused = "change-refused";
    /// <summary>Another commit changed the guest first.</summary>
    public const string Superseded = "superseded";
    /// <summary>Its change proposed no run.</summary>
    public const string NoRun = "no-run";
    /// <summary>The agent did not connect again before the deadline.</summary>
    public const string DidNotComeBack = "did-not-come-back";
    /// <summary>The agent connected again before the deadline, on another version than the target.</summary>
    public const string OtherVersion = "other-version";
    /// <summary>The agent came back on the target and offers fewer executors than before. Nothing is rolled back.</summary>
    public const string LostExecutors = "lost-executors";
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

    /// <summary>
    /// The <see cref="GeneratedValue"/> that wrote this version, for a value a deploy generated: what its cleanup owns. Set when
    /// the version is created and never changed, so a key deleted and written again, whatever its description, is not its.
    /// </summary>
    public Guid? GenerationId { get; set; }

    public string? RevokedBy { get; set; }

    public string? RevocationNote { get; set; }
}

/// <summary>
/// The last version a store key had when it was deleted. A key written again continues after it, so a version number names one
/// value only: what <see cref="SecretResolution"/>, a run's pinned versions and a guest's followed versions recorded never comes
/// to point at a later value. It holds no secret material.
/// </summary>
public sealed class DeletedStoreKey
{
    public required string Key { get; set; }

    public int LatestVersion { get; set; }

    public DateTimeOffset DeletedAt { get; set; }

    /// <summary>The person who deleted it, or <c>cleanup</c> for a generated value removed by its cleanup.</summary>
    public required string DeletedBy { get; set; }
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

    /// <summary>
    /// Every session cookie carries the stamp it was issued with, and one with another stamp is refused. Sign-out
    /// replaces it, and so must anything that changes the password.
    /// </summary>
    public Guid SessionStamp { get; set; }
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

/// <summary>
/// Where one uptime check stands: the last answer an agent gave about it. See docs/uptime.md. Rows are written only by
/// <see cref="Uptime.UptimeMonitor"/>.
/// </summary>
public sealed class UptimeCheck
{
    /// <summary>What is checked and how, <see cref="Uptime.UptimeTarget.Key"/>: a new address or link is a new check.</summary>
    public required string Key { get; set; }

    public bool Up { get; set; }

    /// <summary>Since when <see cref="Up"/> has held, as this server saw it.</summary>
    public DateTimeOffset Since { get; set; }

    public DateTimeOffset CheckedAt { get; set; }

    public int? ElapsedMs { get; set; }

    /// <summary>What the agent found, for people: an HTTP status, or why it could not connect.</summary>
    public string? Detail { get; set; }

    /// <summary>The name of the agent that checked it last.</summary>
    public string? Agent { get; set; }
}

/// <summary>
/// The answers about one uptime check in one 15-minute bucket: how many said up and down, and the response time of those
/// that said up. Kept for 30 days.
/// </summary>
public sealed class UptimeBucket
{
    public required string CheckKey { get; set; }

    public DateTimeOffset StartsAt { get; set; }

    public int Up { get; set; }

    public int Down { get; set; }

    public long LatencyMsTotal { get; set; }
}

public static class NotificationKinds
{
    /// <summary>An uptime check that answered up answers down.</summary>
    public const string CheckDown = "check-down";

    /// <summary>An uptime check that answered down answers up.</summary>
    public const string CheckUp = "check-up";

    public const string RunFailed = "run-failed";

    public const string RunUncertain = "run-uncertain";

    /// <summary>A run stopped for a person to approve its plan.</summary>
    public const string RunApproval = "run-approval";

    /// <summary>An enabled agent has not been connected for <see cref="Agents.AgentPresence.OfflineAfter"/>.</summary>
    public const string AgentOffline = "agent-offline";

    /// <summary>An agent told offline is connected again. Told once for each <see cref="AgentOffline"/>.</summary>
    public const string AgentOnline = "agent-online";

    /// <summary>An agent's update to the server's version failed.</summary>
    public const string AgentUpdateFailed = "agent-update-failed";

    public static readonly IReadOnlyList<string> All = [CheckDown, CheckUp, RunFailed, RunUncertain, RunApproval, AgentOffline, AgentOnline, AgentUpdateFailed];
}

/// <summary>
/// Something that happened and that an operator is told once. Rows are added only by
/// <see cref="Notifications.NotificationHub"/> and never changed. See docs/notifications.md.
/// </summary>
public sealed class Notification
{
    /// <summary>Ascending in the order notifications were added, so a reader can keep its place by it.</summary>
    public long Id { get; set; }

    /// <summary>One of <see cref="NotificationKinds"/>.</summary>
    public required string Kind { get; set; }

    /// <summary>What happened, as text fixed when it happened.</summary>
    public required string Title { get; set; }

    public string? Body { get; set; }

    /// <summary>The resource it is about, if it still exists. Not a foreign key: a notification never fails the save it rides on.</summary>
    public Guid? ResourceId { get; set; }

    public Guid? RunId { get; set; }

    /// <summary>The agent it is about, for the agent kinds. Not a foreign key either.</summary>
    public Guid? AgentId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The one webhook notifications are sent to, and where its sending stands: a single row, id 1. Its URL and signing secret are
/// in the variable store under <see cref="Notifications.Webhook.UrlKey"/> and <see cref="Notifications.Webhook.SecretKey"/>.
/// Only the settings endpoint saves it; <see cref="Notifications.WebhookDelivery"/> writes its delivery state with conditional
/// bulk updates, which the engine does not count as saves. See docs/notifications.md.
/// </summary>
public sealed class WebhookState
{
    public int Id { get; set; } = 1;

    /// <summary>Whether the worker sends: a URL is set and the receiver has not answered 410 Gone since.</summary>
    public bool Enabled { get; set; }

    /// <summary>The URL's host, the one part of it the API shows.</summary>
    public string? Host { get; set; }

    /// <summary>The <see cref="NotificationKinds"/> it sends.</summary>
    public string[] Kinds { get; set; } = [];

    /// <summary>Moves on with every change of settings, so a delivery that was under way meanwhile records nothing.</summary>
    public int Generation { get; set; }

    /// <summary>The id of the last notification delivered, dropped or passed over; the next is sent after it.</summary>
    public long Position { get; set; }

    /// <summary>Failed attempts of the notification after <see cref="Position"/>: they set when it is tried again.</summary>
    public int Attempts { get; set; }

    /// <summary>
    /// Of those, the answers that refused it: a 4xx other than 408, 410 and 429. Enough of them give it up; a timeout or a
    /// server error never counts here.
    /// </summary>
    public int Refusals { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>Notifications given up: too old, too many waiting, or refused by the receiver.</summary>
    public long Dropped { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }

    public long? LastEventId { get; set; }

    public bool LastDelivered { get; set; }

    public int? LastStatus { get; set; }

    /// <summary>What the last attempt came to, in the server's own words: never anything of the receiver's answer.</summary>
    public string? LastResult { get; set; }

    public DateTimeOffset? LastDeliveredAt { get; set; }

    /// <summary>When the receiver answered 410 Gone and sending stopped.</summary>
    public DateTimeOffset? StoppedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The icon chosen for one service on the Services page: a file of the icons directory by name, or a picture uploaded for it.
/// Only shown, so it lives here and not in the lab repository; see docs/service-icons.md.
/// </summary>
public sealed class ServiceIcon
{
    public Guid ResourceId { get; set; }

    /// <summary>The chosen file of the icons directory; null when <see cref="Picture"/> is set.</summary>
    public string? Name { get; set; }

    /// <summary>The uploaded picture, as it was sent; null when <see cref="Name"/> is set.</summary>
    public byte[]? Picture { get; set; }

    /// <summary>The picture's media type, set with it.</summary>
    public string? ContentType { get; set; }

    public DateTimeOffset SetAt { get; set; }
}
