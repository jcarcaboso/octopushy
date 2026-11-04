using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Octopushy.Server.Api;

namespace Octopushy.Server.Changes;

/// <summary>
/// A change made in a form. <c>Kind</c> is <c>edit</c>, <c>upgrade</c> (the workflow reference to a newer major) or
/// <c>remove</c> (a guest's folder). For an edit or upgrade, each section given replaces that section of the definition;
/// one left out stays as it is. <c>BaseCommit</c> is the commit the form was loaded from.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChangeRequest(
    Guid ResourceId,
    string BaseCommit,
    string Kind = "edit",
    string? Workflow = null,
    JsonObject? Hardware = null,
    JsonObject? Network = null,
    JsonObject? Inputs = null);

/// <summary>A problem with one field, at a JSON pointer into <c>{ workflow, hardware, network, inputs }</c>; an empty pointer is the whole form.</summary>
public sealed record FieldIssueDto(string Pointer, string Message);

/// <summary>
/// What a form for a guest or service starts from: its definition at <c>BaseCommit</c>, the workflow its reference resolved
/// to with its inputs schema and <c>ui</c> hints, and, when a newer major is released, that version to upgrade to.
/// <c>BlockedReason</c> says why the form is read-only.
/// </summary>
public sealed record ChangeFormDto(
    Guid ResourceId,
    string ResourceKind,
    string Address,
    string Name,
    string? BaseCommit,
    string? File,
    string? Text,
    string? Workflow,
    WorkflowVersionDto? Resolved,
    JsonNode? Hardware,
    JsonNode? Network,
    JsonNode? Inputs,
    string? BlockedReason,
    bool CanRemove,
    WorkflowVersionDto? Upgrade,
    IReadOnlyList<FieldIssueDto> FieldIssues);

/// <summary>
/// What committing a change would do, without committing it: the exact diff, the commit message, and its validation against
/// the commit the form was loaded from. <c>Refused</c> says why it would be refused now, such as a file that has moved since.
/// </summary>
public sealed record ChangePreviewDto(
    string Kind,
    string Address,
    string Subject,
    string Message,
    string BaseCommit,
    IReadOnlyList<string> Files,
    string Diff,
    bool Valid,
    IReadOnlyList<IssueDto> Issues,
    IReadOnlyList<FieldIssueDto> FieldIssues,
    string? Refused,
    IReadOnlyList<GeneratedValueDto>? Generated = null,
    int? NextVmid = null,
    string? ReviewedRelease = null,
    string? ReviewDigest = null);

/// <summary>
/// A change and where it stands: <c>committing</c> (recorded, being pushed), <c>uncertain</c> or <c>unknown</c> (a push was
/// attempted and is not settled), <c>committed</c>, <c>synced</c>, <c>planning</c>, <c>waiting-for-approval</c>, <c>applying</c>,
/// then <c>applied</c>, <c>failed</c>, <c>no-change</c>, <c>superseded</c> (a later commit set the resource otherwise first;
/// <c>SupersededBy</c> names it), <c>refused</c> (nothing was pushed) or <c>dismissed</c> (a person settled it as not committed).
/// <c>Final</c> says the status will not change; <c>NotCommitted</c> says it is definitely not committed, which is
/// <c>refused</c> or <c>dismissed</c> only.
/// A create's <c>ResourceId</c> is null until the revision with its folder is loaded; <c>PreApproval</c> is where a deploy's
/// approval in the same click stands (<c>requested</c>, <c>approved</c>, <c>declined</c> or <c>not-needed</c>), with
/// <c>PreApprovalDetail</c> saying why.
/// </summary>
public sealed record ChangeDto(
    Guid Id,
    string Kind,
    string ResourceKind,
    Guid? ResourceId,
    string Address,
    string Author,
    string Message,
    string BaseCommit,
    string? Commit,
    string? CommitUrl,
    Guid? RevisionId,
    string Status,
    string? Detail,
    IReadOnlyList<string> Files,
    IReadOnlyList<RunSummaryDto> Runs,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SyncedAt,
    DateTimeOffset UpdatedAt,
    string? SupersededBy = null,
    string? SupersededByUrl = null,
    string? DismissedBy = null,
    DateTimeOffset? DismissedAt = null,
    bool Final = false,
    bool NotCommitted = false,
    int? Vmid = null,
    string? PreApproval = null,
    string? PreApprovalDetail = null,
    IReadOnlyList<GeneratedValueStateDto>? Generated = null,
    int CleanupPending = 0);

/// <summary>
/// A value a deploy generated and where it stands: <c>planned</c>, <c>created</c>, <c>removed</c> or <c>kept</c>.
/// <c>CleanupPending</c> says its removal was due and has not finished yet; <c>Detail</c> says what cleanup did or why it waits.
/// </summary>
public sealed record GeneratedValueStateDto(string Secret, string Key, string Generator, string State, bool CleanupPending, string? Detail,
    string? CleanupError);

/// <summary>
/// Why a change was refused (HTTP 409), with nothing committed: <c>Reason</c> is <c>disabled</c>, <c>observed-only</c>,
/// <c>unavailable</c>, <c>stale</c>, <c>invalid</c>, <c>no-change</c>, <c>unsupported</c>, <c>push-failed</c>, or for a deploy
/// <c>exists</c> or <c>vmid-taken</c>. A refusal once
/// the edit was made is recorded, as <c>ChangeId</c>; one refused before that, such as with committing off, is not.
/// A deploy refused because its VMID was taken meanwhile offers <c>NextVmid</c>, the next free one, to review again with.
/// </summary>
/// <param name="Status">The recorded change's status: <c>refused</c> when nothing was pushed, which is final; <c>uncertain</c> or
/// <c>unknown</c> when a push was attempted, which reconciliation or a person settles.</param>
public sealed record ChangeRefusedDto(Guid? ChangeId, string Reason, string Message, IReadOnlyList<IssueDto> Issues, IReadOnlyList<FieldIssueDto> FieldIssues,
    string? Status = null, int? NextVmid = null);
