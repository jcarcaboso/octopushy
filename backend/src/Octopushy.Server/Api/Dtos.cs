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
    DateTimeOffset? TrustedAt,
    string? TrustedBy,
    bool IsCurrentSource);

public sealed record RevisionDetailDto(RevisionDto Revision, IReadOnlyList<IssueDto> Issues);

public sealed record RevisionReviewDto(RevisionDto Revision, string? BaseCommit, string Diff, bool Truncated, bool CanTrust, string? BlockedReason);

public sealed record TrustRevisionRequest(string Commit, string? BaseCommit);

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
    RevisionDto? Revision);

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
    string? Waiting);

public sealed record ApprovalDto(
    bool Required,
    string? Decision,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? Note,
    string? PlanSha256,
    string? WorkerUpdateSha256 = null);

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
    IReadOnlyList<AttemptDto> Attempts);

/// <summary>What a plan step found: the resource changes OpenTofu proposes.</summary>
public sealed record PlanDto(bool HasChanges, IReadOnlyList<PlanChangeDto> Changes, PlanCountsDto Counts, string? PlanSha256, string? ToolVersion);

public sealed record PlanChangeDto(string Address, string? Type, string? Name, IReadOnlyList<string> Actions);

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
    WorkerUpdateView? WorkerUpdate = null);

public sealed record DecisionRequest(string? PlanSha256, string? Note, string? WorkerUpdateSha256 = null);

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
    Guid? ManagedById);

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
    RunSummaryDto? LastRun);

public sealed record InstallationSummaryDto(
    Guid Id,
    string Address,
    string Name,
    string? Type,
    string? Endpoint,
    bool Valid,
    int IssueCount,
    DateTimeOffset? ObservedAt,
    string? ObserveError,
    DateTimeOffset? ObserveErrorAt,
    bool Observing,
    string? ObserveWaiting,
    string? ProxmoxVersion,
    IReadOnlyList<ObservedHostDto> Hosts,
    IReadOnlyList<GuestSummaryDto> Guests,
    int UnmanagedCount);

public sealed record LabDto(
    Guid Id,
    string Address,
    string Name,
    bool Valid,
    IReadOnlyList<IssueDto> Issues,
    IReadOnlyList<InstallationSummaryDto> Installations);

public sealed record InventoryDto(RevisionDto? Revision, IReadOnlyList<LabDto> Labs);

public sealed record PolicyDto(string Approval, string ApprovalSetBy, string Destructive, string DestructiveSetBy);

public sealed record BindingDto(string Installation, int Vmid, DateTimeOffset? BoundAt);

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
    GuestDiskUsageDto DiskUsage);

public sealed record InstallationDetailDto(
    InstallationSummaryDto Installation,
    string? DefinitionFile,
    string? DefinitionText,
    IReadOnlyList<IssueDto> Issues,
    string? TokenKey,
    bool? VerifyTls,
    IReadOnlyList<ObservedGuestDto> ObservedGuests,
    string? ClusterName);

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
    bool Draining = false);

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

public sealed record VariableCandidateRequest(string Key, string Value, string? Description);

public sealed record VariableCandidateDto(int Version);

public sealed record VariablePromotionRequest(string Key, int Version, int ExpectedCurrentVersion, string VerificationNote);

public sealed record VariableRevocationRequest(string Key, int Version, string Reason);

public sealed record VariableVersionDto(
    int Version, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt, string? ActivatedBy, string? ActivationNote,
    DateTimeOffset? RevokedAt, string? RevokedBy, string? RevocationNote,
    IReadOnlyList<string> UsedBy);
