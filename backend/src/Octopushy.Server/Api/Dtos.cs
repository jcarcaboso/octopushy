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
    string? ReasonDetail = null);

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
    int? ConcurrentOperations);

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
    string? Type);

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
    InstallationTrustDto Trust);

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
    WorkflowDto? Workflow);

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
    IReadOnlyList<RunSummaryDto> Recent);

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
    bool Draining = false,
    bool ReportsReachability = false,
    IReadOnlyList<Agents.ReachabilityEntry>? Reachability = null);

public sealed record AgentUpdate(IReadOnlyList<string>? Labels, int? MaxConcurrency, bool? Disabled, bool? Draining = null);

public sealed record TokenRequest(string? Name, IReadOnlyList<string> Labels, int? MaxConcurrency, int? ExpiresInMinutes);

public sealed record CreatedToken(Guid Id, string Token, string Command, DateTimeOffset ExpiresAt, string CaFingerprint);

public sealed record TokenDto(
    Guid Id,
    string? Name,
    IReadOnlyList<string> Labels,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? UsedAt,
    Guid? AgentId);

public sealed record VariableDto(
    string Key,
    string Visibility,
    string? Description,
    int Version,
    DateTimeOffset UpdatedAt,
    string? Value,
    IReadOnlyList<string> UsedBy,
    int LatestVersion,
    bool Revoked);

public sealed record MissingVariableDto(string Key, IReadOnlyList<string> UsedBy);

public sealed record SystemVariableDto(string Key, string Visibility, DateTimeOffset UpdatedAt);

public sealed record VariablesDto(IReadOnlyList<VariableDto> Entries, IReadOnlyList<MissingVariableDto> Missing, IReadOnlyList<SystemVariableDto> System);

public sealed record VariableUpdate(string Key, string? Value, string? Visibility, string? Description);

public sealed record VariableRevealRequest(IReadOnlyList<string> Keys);

/// <summary>A key's active value; null when it has none that may be used (revoked, or never promoted).</summary>
public sealed record RevealedVariableDto(string Key, string? Value);

public sealed record VariableRevealDto(IReadOnlyList<RevealedVariableDto> Values);

public sealed record VariableCandidateRequest(string Key, string Value, string? Description);

public sealed record VariableCandidateDto(int Version);

public sealed record VariablePromotionRequest(string Key, int Version, int ExpectedCurrentVersion, string VerificationNote);

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
