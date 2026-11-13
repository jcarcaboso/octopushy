using System.Text.Json.Nodes;

namespace Octopushy.Server.Api;

public sealed record IssueDto(string Severity, string File, int? Line, string Message, string? Address);

public sealed record RevisionDto(
    Guid Id,
    string Commit,
    string? Subject,
    string? Author,
    DateTimeOffset? CommittedAt,
    DateTimeOffset LoadedAt,
    bool Valid,
    int ErrorCount,
    int WarningCount,
    bool IsCurrentSource,
    string? WorkflowsRepository = null,
    string? WorkflowsDigest = null);

public sealed record RevisionDetailDto(RevisionDto Revision, IReadOnlyList<IssueDto> Issues);

public sealed record RepositoryDto(
    string? Url,
    string Branch,
    int PollSeconds,
    string CredentialKind,
    string? CredentialUsername,
    bool CredentialSet,
    DateTimeOffset? LastFetchAt,
    string? LastFetchError,
    string? HeadCommit,
    RevisionDto? Revision,
    WorkflowsSourceDto? Workflows = null);

/// <summary>
/// Where the head revision's workflows come from: <c>local</c>, the lab repository's own <c>workflows/</c>, or <c>repository</c>,
/// a workflows repository with its releases. <see cref="Uses"/> is what each guest and service resolved to.
/// </summary>
public sealed record WorkflowsSourceDto(
    string Source,
    string? Repository,
    DateTimeOffset? LastFetchAt,
    string? LastFetchError,
    IReadOnlyList<WorkflowReleasesDto> Workflows,
    IReadOnlyList<string> Issues,
    IReadOnlyList<WorkflowUseDto> Uses);

/// <summary>The releases of one workflow, newest first.</summary>
public sealed record WorkflowReleasesDto(string Id, IReadOnlyList<WorkflowReleaseDto> Releases);

/// <summary>
/// One release: the commit its tag pointed to when Octopushy first saw it. <see cref="Usable"/> is false for a release that is
/// never resolved to, or whose commit is gone; <see cref="Issue"/> says why, or that its tag moved or was deleted.
/// </summary>
public sealed record WorkflowReleaseDto(
    string Version,
    string Commit,
    DateTimeOffset? FirstSeenAt,
    bool Usable,
    bool TagDeleted,
    string? MovedTo,
    string? Issue);

/// <summary>
/// What a guest's or service's reference resolved to in the head revision, and the newest major after it, which is offered
/// and never applied by itself.
/// </summary>
public sealed record WorkflowUseDto(
    string Address,
    string Kind,
    string Reference,
    string? Version,
    string? Commit,
    string? Problem,
    string? NewerMajor);

public sealed record RepositoryUpdate(
    string? Url,
    string Branch,
    int PollSeconds,
    string CredentialKind,
    string? CredentialUsername,
    string? CredentialSecret);

/// <summary>A run, briefly: enough to show it in a list or on a resource.</summary>
public sealed record RunSummaryDto(
    Guid Id,
    long Number,
    string Operation,
    Guid ResourceId,
    string ResourceKind,
    string Address,
    string Status,
    string? Detail,
    string Reason,
    string? Commit,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? CurrentStep,
    string? Waiting,
    IReadOnlyList<RunStepSummaryDto> Steps,
    bool CanCancel,
    string? ReasonDetail = null);

/// <summary>
/// A run's step as a list of runs shows it: its place in the run's progress, what the plan of an unfinished run would
/// change, and the fingerprint an approval step is decided against.
/// </summary>
public sealed record RunStepSummaryDto(Guid Id, string Name, string Kind, string Status, PlanCountsDto? Counts, string? PlanSha256);

public sealed record ApprovalDto(
    bool Required,
    string? Decision,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? Note,
    string? PlanSha256,
    JsonNode? Document = null);

public sealed record AttemptDto(
    Guid Id,
    int Number,
    string Executor,
    string Action,
    string Status,
    string? WaitingReason,
    Guid? AgentId,
    string? AgentName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AssignedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error,
    bool LateResult,
    long LogLines,
    bool Mutating);

/// <param name="NetworkAccess">Which connections an agent must be shown to reach for this step, or that they are not modeled.</param>
public sealed record StepDto(
    Guid Id,
    int Ordinal,
    string Name,
    string Kind,
    string Status,
    string? Detail,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    JsonNode? Outputs,
    PlanDto? Plan,
    ApprovalDto? Approval,
    IReadOnlyList<AttemptDto> Attempts,
    ConfigurePreviewDto? ConfigurePreview = null,
    string? Stage = null,
    string? NetworkAccess = null);

/// <summary>What a plan step found: the resource changes OpenTofu proposes.</summary>
public sealed record PlanDto(bool HasChanges, IReadOnlyList<PlanChangeDto> Changes, PlanCountsDto Counts, string? PlanSha256, string? ToolVersion);

/// <summary>What a configure run will do. It says what will run, not what will change: no tool can show that.</summary>
/// <param name="PreviousPrepared">
/// What the guest's last successful configure run prepared, as it was when this run was made, to compare
/// <paramref name="Prepared"/> with. Null when there was none.
/// </param>
public sealed record ConfigurePreviewDto(
    string Action,
    string Playbook,
    string? Commit,
    string? Image,
    string? Host,
    string? ToolVersion,
    bool Destructive,
    IReadOnlyList<string> Tasks,
    IReadOnlyList<string>? ChangedFiles,
    JsonNode? Vars,
    IReadOnlyList<ConfigureSecretDto> Secrets,
    JsonNode? Prepared,
    IReadOnlyList<string> HostKeyFingerprints,
    string? PreviewSha256,
    AccessRotationDto? AccessRotation = null,
    JsonNode? PreviousPrepared = null);

/// <summary>Which key goes on and which come off, by fingerprint. An approval names this with the rest.</summary>
public sealed record AccessRotationDto(string Current, string Candidate, IReadOnlyList<string> Remove);

public sealed record ConfigureSecretDto(string Name, string Reference);

public sealed record PlanChangeDto(string Address, string? Type, string? Name, IReadOnlyList<string> Actions, IReadOnlyList<PlanAttributeDto> Attributes);

/// <summary>
/// One allowlisted VM attribute a change sets, as the plan says it: the run's input, not a reading of the guest. A side
/// that does not exist is null; <paramref name="Unknown"/> when it is known only after apply, and a
/// <paramref name="Sensitive"/> one carries no values.
/// </summary>
public sealed record PlanAttributeDto(string Name, JsonNode? Before, JsonNode? After, bool Unknown, bool Sensitive);

public sealed record PlanCountsDto(int Create, int Update, int Delete, int Replace);

public sealed record RunPolicyDto(string Approval, string ApprovalSetBy, string Destructive, string DestructiveSetBy);

public sealed record RunDetailDto(
    RunSummaryDto Run,
    IReadOnlyList<StepDto> Steps,
    IReadOnlyList<string> AgentLabels,
    string AgentLabelsSetBy,
    RunPolicyDto? Policy,
    bool CanCancel,
    bool CanClose,
    RunWorkflowDto? Workflow = null,
    bool CanRunAgain = false,
    bool CanForceStop = false);

/// <summary>The workflow a run executed: which one, at which version, and the commit its files came from.</summary>
public sealed record RunWorkflowDto(string Id, string Version, string Reference, string Commit, string? Repository);

public sealed record DecisionRequest(string? PlanSha256, string? Note);

public sealed record StartedRun(Guid RunId);

public sealed record LogDto(IReadOnlyList<Agents.LogLineRecord> Lines, long Next, bool Finished);

/// <summary>What the installation's Proxmox API last reported about a guest.</summary>
public sealed record ObservedGuestDto(
    int Vmid,
    string? Name,
    string? Host,
    string? Status,
    bool Template,
    IReadOnlyList<string> Tags,
    double? MaxCpu,
    long? MaxMemoryBytes,
    long? UptimeSeconds,
    string? ManagedBy,
    Guid? ManagedById,
    /// <summary><c>qemu</c> for a VM, <c>lxc</c> for a container, as Proxmox lists it.</summary>
    string? Kind);

public sealed record ObservedHostDto(
    string Name,
    bool Declared,
    bool? Online,
    double? Cpu,
    double? MaxCpu,
    long? MemoryBytes,
    long? MaxMemoryBytes,
    long? UptimeSeconds,
    int? ConcurrentOperations,
    /// <summary>The Proxmox API endpoint the definition gives this host: its own, or else the installation's.</summary>
    string? Endpoint);

public sealed record GuestSummaryDto(
    Guid Id,
    string Address,
    string Name,
    string? Host,
    int? Vmid,
    string Lifecycle,
    string Status,
    string? StatusDetail,
    bool Valid,
    ObservedGuestDto? Observed,
    RunSummaryDto? ActiveRun,
    RunSummaryDto? LastRun,
    WorkflowDto? Workflow,
    /// <summary>The definition's <c>type</c>: <c>vm</c> or <c>container</c>; null when it has none that can be read.</summary>
    string? Type,
    /// <summary>Where a configure run connects: <c>access.address</c>, or the static or reserved address; null when unknown.</summary>
    string? AccessAddress,
    /// <summary>The services the repository shows on the guest.</summary>
    IReadOnlyList<ServiceSummaryDto> Services);

/// <summary>
/// The workflow a guest or service names, as written (<c>fedora-vm@2</c>), and the version the latest revision resolved
/// it to (<c>fedora-vm@2.3</c>); null when it resolved to none.
/// </summary>
public sealed record WorkflowDto(string Reference, string? Resolved);

/// <summary>
/// The endpoints an installation declares now, and those not trusted yet. Credentials are sent
/// only to trusted endpoints, so an installation with untrusted ones waits for an operator.
/// </summary>
/// <param name="ManagementChanged">Trusted while observed only, and now asking to be managed, so it has to be trusted again.</param>
public sealed record InstallationTrustDto(
    IReadOnlyList<string> Endpoints,
    IReadOnlyList<string> Untrusted,
    DateTimeOffset? TrustedAt,
    string? TrustedBy,
    bool ManagementChanged);

/// <summary>The endpoints the operator was shown; trust is refused when the installation declares others by now.</summary>
public sealed record TrustInstallationRequest(IReadOnlyList<string> Endpoints);

/// <param name="Managed">False for <c>manage: false</c>: Octopushy only observes the installation. See docs/safety-boundary.md.</param>
public sealed record InstallationSummaryDto(
    Guid Id,
    string Address,
    string Name,
    string? Type,
    string? Endpoint,
    bool Valid,
    bool Managed,
    int IssueCount,
    DateTimeOffset? ObservedAt,
    string? ObserveError,
    DateTimeOffset? ObserveErrorAt,
    bool Observing,
    string? ObserveWaiting,
    string? ProxmoxVersion,
    IReadOnlyList<ObservedHostDto> Hosts,
    IReadOnlyList<GuestSummaryDto> Guests,
    int UnmanagedCount,
    InstallationTrustDto Trust,
    /// <summary>Guests Proxmox lists that no definition manages, templates included, from the latest observation.</summary>
    IReadOnlyList<ObservedGuestDto> UnmanagedGuests);

public sealed record LabDto(
    Guid Id,
    string Address,
    string Name,
    bool Valid,
    IReadOnlyList<IssueDto> Issues,
    IReadOnlyList<InstallationSummaryDto> Installations);

public sealed record InventoryDto(RevisionDto? Revision, IReadOnlyList<LabDto> Labs);

/// <param name="Actions">For a guest, the actions of its setup stage that take their own approval, from its policy.actions or the workflow.</param>
public sealed record PolicyDto(string Approval, string ApprovalSetBy, string Destructive, string DestructiveSetBy, IReadOnlyList<ActionApprovalDto>? Actions = null);

/// <summary>The approval one action of a guest's setup stage takes, whatever starts it, and where it was set.</summary>
public sealed record ActionApprovalDto(string Action, string Approval, string SetBy);

public sealed record BindingDto(string Installation, int Vmid, DateTimeOffset? BoundAt);

/// <summary>A guest's setup, the Ansible stage of its workflow, and where it stands. <c>Workflow</c> is the workflow at its version.</summary>
public sealed record GuestConfigurationDto(
    string Workflow,
    IReadOnlyList<ConfigureActionDto> Actions,
    bool InSync,
    string? AppliedAction,
    string? AppliedCommit,
    DateTimeOffset? AppliedAt,
    bool HasSnapshot,
    IReadOnlyList<GuestScheduleDto> Schedules,
    IReadOnlyList<FollowedSecretDto> Secrets,
    /// <summary>Why no setup run starts, automatically or by hand, when it would log in with a key nothing shows reached the machine.</summary>
    string? Refused);

/// <summary>
/// A store value the guest's setup takes at its active version. <see cref="Pending"/> means a
/// newer active version has not reached a run of the onChange action yet, so one is owed.
/// </summary>
public sealed record FollowedSecretDto(string Key, int? ActiveVersion, int? StartedVersion, bool Pending);

/// <summary>A schedule in Git, when it next falls due, and what its last firing did.</summary>
public sealed record GuestScheduleDto(
    string Action,
    string Cron,
    string Timezone,
    DateTimeOffset? NextAt,
    Guid? LastRunId,
    long? LastRunNumber,
    string? LastRunStatus,
    string? LastRunDetail,
    string? Note,
    DateTimeOffset? NoteAt);

public sealed record ConfigureActionDto(string Id, string? Description, bool Destructive, bool OnChange);

public sealed record ConfigureGuestRequest(string? Action);

/// <summary>The key Octopushy logs in to a guest with. Only its public half exists here.</summary>
public sealed record ManagedAccessDto(
    string PublicKey,
    string Fingerprint,
    int Version,
    DateTimeOffset? ProvisionedAt,
    DateTimeOffset? LastUsedAt,
    IReadOnlyList<ManagedAccessKeyDto> Keys);

/// <summary>Where one version of the key stands on the guest. <c>State</c> is in-use, candidate, old or removed.</summary>
public sealed record ManagedAccessKeyDto(
    int Version,
    string Fingerprint,
    string State,
    DateTimeOffset? AuthorizedAt,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset? PromotedAt,
    string? PromotedBy,
    string? PromotionEvidence,
    DateTimeOffset? RemovedAt);

public sealed record GuestDetailDto(
    GuestSummaryDto Guest,
    string? DefinitionFile,
    string? DefinitionText,
    IReadOnlyList<IssueDto> Issues,
    PolicyDto? Policy,
    IReadOnlyList<string> AgentLabels,
    string? AgentLabelsSetBy,
    BindingDto? Binding,
    string? DesiredCommit,
    string? CheckedCommit,
    DateTimeOffset? CheckedAt,
    string? AppliedCommit,
    DateTimeOffset? AppliedAt,
    DateTimeOffset? VerifiedAt,
    bool InSync,
    JsonNode? Outputs,
    DateTimeOffset? MissingSince,
    DateTimeOffset? RetiredAt,
    IReadOnlyList<RunSummaryDto> Runs,
    GuestConfigurationDto? Configuration,
    ManagedAccessDto? ManagedAccess,
    IReadOnlyList<ServiceSummaryDto> Services,
    ManagedHostKeyDto? HostKey = null);

/// <summary>
/// A guest's SSH host key, when it is Octopushy's. <see cref="Pinned"/> is the version an apply delivered last, which
/// configure runs connect to; null until one did. <see cref="Next"/> is the version plans carry when it is another one,
/// as for a new machine a plan makes: it becomes the pin when the apply that makes the machine finishes.
/// </summary>
public sealed record ManagedHostKeyDto(ManagedHostKeyVersionDto? Pinned, ManagedHostKeyVersionDto? Next);

/// <summary>One version of a guest's host key: only its public half.</summary>
public sealed record ManagedHostKeyVersionDto(int Version, string PublicKey, string Fingerprint, DateTimeOffset CreatedAt, DateTimeOffset? DeliveredAt);

/// <summary>A service on a guest, read from the repository, with its own status: one failing says nothing about the guest or the others.</summary>
public sealed record ServiceSummaryDto(
    Guid Id,
    string Address,
    string Name,
    string Status,
    string? StatusDetail,
    bool Valid,
    RunSummaryDto? ActiveRun,
    RunSummaryDto? LastRun,
    WorkflowDto? Workflow,
    /// <summary>
    /// Where people open the service and what uptime checks: <c>links</c> in <c>service.yaml</c>, then the URL of each of
    /// <see cref="Names"/> that no link already opens.
    /// </summary>
    IReadOnlyList<string> Links,
    /// <summary>The host names the service's stages set, from the input each stage names with <c>names</c>.</summary>
    IReadOnlyList<ServiceNameDto> Names,
    /// <summary>The icon the Services page shows for the service; null when it has none. See docs/service-icons.md.</summary>
    ServiceIconDto? Icon);

/// <summary>A host name a service's stage sets, and the URL it is opened and checked at.</summary>
public sealed record ServiceNameDto(string Name, string Stage, string Url);

/// <summary>A service's icon and where it comes from.</summary>
public sealed record ServiceIconDto(
    /// <summary>Where the browser loads it: a path on this origin.</summary>
    string Url,
    /// <summary>
    /// <c>picture</c>: uploaded for the service. <c>chosen</c>: a file of the icons directory picked for it. <c>name</c>: the
    /// file of the icons directory named after the service.
    /// </summary>
    string Source);

/// <summary>Picks a file of the icons directory for a service.</summary>
public sealed record ServiceIconChoice(string Name);

/// <summary>The files of the icons directory, sorted.</summary>
public sealed record IconNamesDto(IReadOnlyList<string> Names);

public sealed record ServiceStageDto(string Name, string Executor);

public sealed record ServiceDetailDto(
    ServiceSummaryDto Service,
    Guid? GuestId,
    string? GuestAddress,
    string? DefinitionFile,
    string? DefinitionText,
    IReadOnlyList<IssueDto> Issues,
    PolicyDto? Policy,
    IReadOnlyList<string> AgentLabels,
    string? AgentLabelsSetBy,
    IReadOnlyList<ServiceStageDto> Stages,
    string? AppliedCommit,
    DateTimeOffset? AppliedAt,
    DateTimeOffset? MissingSince,
    DateTimeOffset? RetiredAt,
    IReadOnlyList<RunSummaryDto> Runs);

public sealed record InstallationDetailDto(
    InstallationSummaryDto Installation,
    string? DefinitionFile,
    string? DefinitionText,
    IReadOnlyList<IssueDto> Issues,
    string? TokenKey,
    bool? VerifyTls,
    IReadOnlyList<ObservedGuestDto> ObservedGuests,
    string? ClusterName,
    SnippetAccessDto? Snippets = null);

/// <summary>
/// An installation's snippet uploads: the public half of Octopushy's key for them, if one was made, what the
/// installation's definition declares, and the command that sets up one of its hosts.
/// </summary>
public sealed record SnippetAccessDto(
    string? PublicKey,
    string? Fingerprint,
    DateTimeOffset? CreatedAt,
    bool Declared,
    string Datastore,
    string User,
    IReadOnlyList<SnippetHostDto> Hosts,
    string? Command);

/// <summary>A host declared for uploads, and whether its own SSH host key is in Variables yet.</summary>
public sealed record SnippetHostDto(string Host, string Address, int Port, string HostKeyStore, bool HostKeySet);

/// <summary>An installation whose latest observation did not succeed.</summary>
public sealed record ObserveErrorDto(Guid InstallationId, string Address, string Name, string Error, DateTimeOffset? At);

public sealed record OverviewCounts(
    int Guests,
    int InSync,
    int Pending,
    int Running,
    int AwaitingApproval,
    int Blocked,
    int Failed,
    int Uncertain,
    int Invalid,
    int Missing);

public sealed record OverviewDto(
    RepositoryDto Repository,
    OverviewCounts Counts,
    int AgentsConnected,
    int AgentsTotal,
    IReadOnlyList<RunSummaryDto> Attention,
    IReadOnlyList<ObserveErrorDto> ObserveErrors,
    IReadOnlyList<RunSummaryDto> Recent,
    IReadOnlyList<RunSummaryDto> Failed,
    OverviewWeekDto Week);

/// <summary>The runs created in the last 7 days, by status. Unfinished holds every status that is not succeeded, failed or cancelled.</summary>
public sealed record OverviewWeekDto(int Total, int Succeeded, int Failed, int Cancelled, int Unfinished);

public sealed record AgentDto(
    Guid Id,
    string Name,
    IReadOnlyList<string> Labels,
    int MaxConcurrency,
    bool Disabled,
    bool Connected,
    DateTimeOffset EnrolledAt,
    DateTimeOffset? LastSeenAt,
    string? Version,
    IReadOnlyList<string> Executors,
    string? ToolRuntime,
    DateTimeOffset CertificateExpiresAt,
    int RunningAttempts,
    IReadOnlyList<AgentWork> Work,
    AgentGuest? Guest,
    AgentUpdateState Update,
    bool Draining = false,
    bool ReportsReachability = false,
    IReadOnlyList<Agents.ReachabilityEntry>? Reachability = null);

/// <summary>
/// An attempt assigned to or running on an agent. <c>Operation</c> and <c>Address</c> are as a run summary has them, <c>Step</c>
/// is the step's name, and <c>Since</c> is when the attempt started, or was assigned when it has not started yet.
/// </summary>
public sealed record AgentWork(Guid RunId, long RunNumber, string Operation, string Address, string Step, string Status, DateTimeOffset Since);

/// <summary>The guest an agent runs on, from the head revision: <c>DeclaredVersion</c> is its <c>inputs.worker_agent_version</c>.</summary>
public sealed record AgentGuest(Guid ResourceId, string Address, string? DeclaredVersion);

/// <summary>
/// Whether an agent can be updated to the server's version from here, and where an update stands: <c>current</c>,
/// <c>available</c>, <c>unavailable</c>, <c>queued</c>, <c>updating</c> or <c>failed</c>. <c>Reason</c> says why it is
/// unavailable or why it failed. <c>ChangeId</c> and <c>RunId</c> are the change carrying the update and its latest run.
/// See docs/worker-routing.md.
/// </summary>
public sealed record AgentUpdateState(string State, string? TargetVersion, string? Reason, Guid? ChangeId, Guid? RunId);

public sealed record AgentUpdate(IReadOnlyList<string>? Labels, int? MaxConcurrency, bool? Disabled, bool? Draining = null);

/// <summary>An enrollment token to create. <c>Labels</c> may be left out: the agent then has none.</summary>
public sealed record TokenRequest(string? Name, int? MaxConcurrency, int? ExpiresInMinutes, IReadOnlyList<string>? Labels = null);

public sealed record CreatedToken(Guid Id, string Token, string Command, DateTimeOffset ExpiresAt, string CaFingerprint);

public sealed record TokenDto(
    Guid Id,
    string? Name,
    IReadOnlyList<string> Labels,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? UsedAt,
    Guid? AgentId);

/// <summary>
/// An entry of the variable store. <c>Lines</c> is the number of lines in the active value, so a list can tell a file from a
/// one-line value without revealing it; null when there is no active value that may be used. <c>NewerVersion</c> is the newest
/// version after the active one that is not revoked, which could be made active; null when there is none. <c>LatestVersion</c>
/// is the last number given, whatever became of that version.
/// </summary>
public sealed record VariableDto(
    string Key,
    string Visibility,
    string? Description,
    int Version,
    DateTimeOffset UpdatedAt,
    string? Value,
    IReadOnlyList<string> UsedBy,
    int LatestVersion,
    bool Revoked,
    int? Lines,
    int? NewerVersion);

public sealed record MissingVariableDto(string Key, IReadOnlyList<string> UsedBy);

public sealed record SystemVariableDto(string Key, string Visibility, DateTimeOffset UpdatedAt);

public sealed record VariablesDto(IReadOnlyList<VariableDto> Entries, IReadOnlyList<MissingVariableDto> Missing, IReadOnlyList<SystemVariableDto> System);

/// <summary>
/// Sets a value, which becomes the active version at once, for a new key and for an existing one, secret or plain. Without a
/// <c>Value</c> only the description changes. <c>Note</c> is kept with the new version. With <c>ExpectedCurrentVersion</c> the
/// write is refused with a conflict when another version became active since the caller read the entry.
/// </summary>
public sealed record VariableUpdate(string Key, string? Value, string? Visibility, string? Description, string? Note = null, int? ExpectedCurrentVersion = null);

public sealed record VariableRevealRequest(IReadOnlyList<string> Keys);

/// <summary>A key's active value; null when it has none that may be used (revoked, or never promoted).</summary>
public sealed record RevealedVariableDto(string Key, string? Value);

public sealed record VariableRevealDto(IReadOnlyList<RevealedVariableDto> Values);

public sealed record VariableVersionRevealRequest(string Key, int Version);

/// <summary>One version's value, whatever its state; revealing it releases it to nothing.</summary>
public sealed record RevealedVariableVersionDto(string Key, int Version, string Value);

public sealed record VariableCandidateRequest(string Key, string Value, string? Description);

public sealed record VariableCandidateDto(int Version);

public sealed record VariablePromotionRequest(string Key, int Version, int ExpectedCurrentVersion, string? VerificationNote = null);

public sealed record VariableRevocationRequest(string Key, int Version, string Reason);

public sealed record VariableVersionDto(
    int Version, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt, string? ActivatedBy, string? ActivationNote,
    DateTimeOffset? RevokedAt, string? RevokedBy, string? RevocationNote,
    IReadOnlyList<string> UsedBy);

/// <summary>
/// A template of the head revision: its image is downloaded by Proxmox (<c>download</c>) or built by Octopushy (<c>built</c>).
/// </summary>
public sealed record TemplateDto(string Id, string? Os, string Kind, bool Valid, IReadOnlyList<string> AvailableOn, TemplateBuildDto? Build);

/// <summary>
/// A built template's image for its current key. <c>Status</c> is <c>queued</c>, <c>building</c>, <c>built</c>, <c>failed</c>,
/// <c>deleted</c>, or <c>not-built</c> while no guest needs it. <c>AttemptId</c> is the current attempt, whose log is at
/// <c>/api/attempts/{id}/log</c>.
/// </summary>
public sealed record TemplateBuildDto(
    string Key,
    string Workflow,
    string WorkflowReference,
    string Output,
    string? Lock,
    string Status,
    Guid? AttemptId,
    string? Sha256,
    long? Size,
    string? FileName,
    string? Error,
    DateTimeOffset? BuiltAt,
    DateTimeOffset? LastUsedAt);

/// <summary>
/// The uptime checks and what agents answered, from <c>GET /api/uptime</c>. <c>Checking</c> is whether a connected agent
/// answers uptime probes; without one, nothing is checked and the history has a gap. See docs/uptime.md.
/// </summary>
public sealed record UptimeDto(bool Checking, int IntervalSeconds, DateTimeOffset GeneratedAt, IReadOnlyList<UptimeCheckDto> Checks);

/// <summary>
/// One uptime check. <c>Kind</c> is <c>proxmox-api</c>, <c>guest-ssh</c>, <c>service-http</c> or <c>service-tcp</c>; <c>Host</c> the Proxmox host,
/// null for an API endpoint several hosts share; <c>Resource</c> the guest or service address. <c>Status</c> is <c>up</c>,
/// <c>down</c>, or <c>pending</c> until a first answer.
/// <para>
/// <c>Beats</c> is the last 24 hours in 96 quarter hours, oldest first, one character each: <c>u</c> all answers up,
/// <c>d</c> all down, <c>p</c> some of each, <c>-</c> none. <c>Latency</c> is the average response time of the up answers in
/// each of those, in milliseconds. <c>Daily</c> is the last 30 days, oldest first, each the percentage of answers that said
/// up in that day, counting back from now; null without answers.
/// </para>
/// </summary>
public sealed record UptimeCheckDto(
    string Key,
    string Kind,
    string Installation,
    string? Host,
    string? Resource,
    string Url,
    string Status,
    string? Detail,
    DateTimeOffset? Since,
    DateTimeOffset? CheckedAt,
    int? ElapsedMs,
    string? Agent,
    UptimePercentagesDto Uptime,
    string Beats,
    IReadOnlyList<int?> Latency,
    IReadOnlyList<double?> Daily);

/// <summary>The percentage of answers that said up over the last day, 7 days and 30 days; null without answers.</summary>
public sealed record UptimePercentagesDto(double? Day, double? Week, double? Month);
