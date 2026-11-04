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
    string? Refused);

/// <summary>
/// A change and where it stands: <c>committed</c>, <c>synced</c>, <c>planning</c>, <c>waiting-for-approval</c>,
/// <c>applying</c>, then <c>applied</c>, <c>failed</c>, <c>no-change</c>, or <c>refused</c> before anything was committed.
/// </summary>
public sealed record ChangeDto(
    Guid Id,
    string Kind,
    string ResourceKind,
    Guid ResourceId,
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
    DateTimeOffset UpdatedAt);

/// <summary>
/// Why a change was refused (HTTP 409), with nothing committed: <c>Reason</c> is <c>disabled</c>, <c>observed-only</c>,
/// <c>unavailable</c>, <c>stale</c>, <c>invalid</c>, <c>no-change</c>, <c>unsupported</c> or <c>push-failed</c>. A refusal of
/// a known resource is recorded, as <c>ChangeId</c>.
/// </summary>
public sealed record ChangeRefusedDto(Guid? ChangeId, string Reason, string Message, IReadOnlyList<IssueDto> Issues, IReadOnlyList<FieldIssueDto> FieldIssues);
