using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Octopushy.Server.Api;

/// <summary><c>open</c>, <c>conflicted</c>, <c>stale</c> (the repository source changed) or <c>published</c>.</summary>
public sealed record DraftSummaryDto(Guid Id, string Title, string Status, string BaseCommit, string HeadCommit,
    int? ChangedFiles, bool Behind, IReadOnlyList<string> ConflictPaths, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? PublishedCommit, DateTimeOffset? PublishedAt, string? PublishedBy);

/// <summary>A file of the draft; <c>Kind</c> is <c>file</c>, <c>executable</c>, <c>link</c> or <c>submodule</c>.</summary>
public sealed record DraftFileDto(string Path, long? Size, string Kind);

public sealed record DraftDetailDto(DraftSummaryDto Draft, string Branch, string? EditBlockedReason, string? PublishBlockedReason,
    IReadOnlyList<ChangedPath> Changes, IReadOnlyList<DraftFileDto> Files);

/// <summary>A file in the draft and at its base. Content is null when the file is missing, binary, or too large.</summary>
public sealed record DraftFileContentDto(string Path, string HeadCommit, bool Exists, string? Kind, long? Size, string? Content,
    bool BaseExists, string? BaseContent, bool Editable);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DraftCreateRequest(string Title);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DraftChangesRequest(string ExpectedHead, IReadOnlyList<DraftFileChange> Changes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DraftUpdateRequest(string ExpectedHead);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DraftPublishRequest(string ExpectedHead, string Message, bool ConfirmRepositoryWrite);

/// <summary>What the draft adds to the issues of the commit it is compared with, plus format errors in other files.</summary>
public sealed record DraftValidationDto(string ComparedWith, bool Valid, IReadOnlyList<IssueDto> NewIssues, IReadOnlyList<IssueDto> FormatIssues);

public sealed record DraftPublishedDto(string Commit, DraftSummaryDto Draft);

public static partial class RepositoryEditor
{
    private const int PushAttempts = 3;

    public static void MapRepositoryEditor(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/repository/drafts").WithTags("Repository");

        group.MapGet("", async (OctopushyDb db, RepositoryMirror mirror, CancellationToken ct) =>
        {
            var settings = await db.RepositorySettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct);
            var drafts = await db.RepositoryDrafts.AsNoTracking().OrderByDescending(d => d.UpdatedAt).Take(100).ToListAsync(ct);
            var result = new List<DraftSummaryDto>();
            foreach (var draft in drafts)
            {
                result.Add(Summary(draft, settings, await CountChangesAsync(mirror, draft, ct)));
            }
            return TypedResults.Ok(result);
        }).WithName("listDrafts");

        group.MapPost("", async Task<Results<Ok<DraftDetailDto>, ProblemHttpResult>> (DraftCreateRequest request, RunEngine engine,
            RepositoryMirror mirror, IOptions<OctopushyOptions> options, TimeProvider clock, CancellationToken ct) =>
        {
            var title = request.Title?.Trim() ?? "";
            if (title.Length is < 1 or > 120)
                return TypedResults.Problem(statusCode: 400, title: "Give the draft a title of up to 120 characters.");
            try
            {
                return TypedResults.Ok(await engine.ExecuteAsync(async (db, _) =>
                {
                    var settings = await db.RepositorySettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct);
                    if (settings?.Url is null || settings.HeadCommit is null)
                        throw new GitException("Connect the lab repository and wait for its first sync before starting a draft.");
                    var now = clock.GetUtcNow();
                    var draft = new RepositoryDraft
                    {
                        Id = Guid.CreateVersion7(),
                        SourceId = settings.SourceId,
                        Title = title,
                        BaseCommit = settings.HeadCommit,
                        HeadCommit = settings.HeadCommit,
                        CreatedAt = now,
                        UpdatedAt = now,
                    };
                    await mirror.CreateDraftAsync(draft.Id, draft.BaseCommit, ct);
                    db.RepositoryDrafts.Add(draft);
                    await db.SaveChangesAsync(ct);
                    return await DetailAsync(db, mirror, options.Value, draft, ct);
                }, ct));
            }
            catch (GitException e) { return TypedResults.Problem(statusCode: 409, title: e.Message); }
        }).WithName("createDraft");

        group.MapGet("/{id:guid}", async Task<Results<Ok<DraftDetailDto>, NotFound>> (Guid id, OctopushyDb db, RepositoryMirror mirror,
            IOptions<OctopushyOptions> options, CancellationToken ct) =>
        {
            var draft = await db.RepositoryDrafts.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
            return draft is null ? TypedResults.NotFound() : TypedResults.Ok(await DetailAsync(db, mirror, options.Value, draft, ct));
        }).WithName("getDraft");

        group.MapDelete("/{id:guid}", async Task<Results<NoContent, NotFound>> (Guid id, RunEngine engine, RepositoryMirror mirror, CancellationToken ct) =>
            await engine.ExecuteAsync<Results<NoContent, NotFound>>(async (db, _) =>
            {
                var draft = await db.RepositoryDrafts.SingleOrDefaultAsync(d => d.Id == id, ct);
                if (draft is null) return TypedResults.NotFound();
                await mirror.DeleteDraftAsync(id, ct);
                db.RepositoryDrafts.Remove(draft);
                await db.SaveChangesAsync(ct);
                return TypedResults.NoContent();
            }, ct)).WithName("discardDraft");

        group.MapGet("/{id:guid}/file", async Task<Results<Ok<DraftFileContentDto>, NotFound, ProblemHttpResult>> (Guid id, string path,
            OctopushyDb db, RepositoryMirror mirror, CancellationToken ct) =>
        {
            var draft = await db.RepositoryDrafts.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
            if (draft is null) return TypedResults.NotFound();
            try
            {
                var head = await mirror.ReadFileAsync(draft.HeadCommit, path, ct);
                var basis = await mirror.ReadFileAsync(draft.BaseCommit, path, ct);
                return TypedResults.Ok(new DraftFileContentDto(path, draft.HeadCommit, head is not null, head is null ? null : Kind(head.File),
                    head?.File.Size, head?.Text, basis is not null, basis?.Text,
                    head is null || head.Text is not null));
            }
            catch (GitException e) { return TypedResults.Problem(statusCode: 409, title: e.Message); }
        }).WithName("getDraftFile");

        group.MapPost("/{id:guid}/changes", async Task<Results<Ok<DraftDetailDto>, NotFound, ProblemHttpResult>> (Guid id,
            DraftChangesRequest request, RunEngine engine, RepositoryMirror mirror, IOptions<OctopushyOptions> options,
            TimeProvider clock, CancellationToken ct) =>
        {
            if (request.Changes is null || request.ExpectedHead is null)
                return TypedResults.Problem(statusCode: 400, title: "Send the draft's head and at least one change.");
            try
            {
                return await engine.ExecuteAsync<Results<Ok<DraftDetailDto>, NotFound, ProblemHttpResult>>(async (db, _) =>
                {
                    var draft = await db.RepositoryDrafts.SingleOrDefaultAsync(d => d.Id == id, ct);
                    if (draft is null) return TypedResults.NotFound();
                    var settings = await db.RepositorySettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct);
                    if (EditBlockedReason(draft, settings) is { } blocked) throw new GitException(blocked);
                    if (draft.HeadCommit != request.ExpectedHead) throw new DraftChangedException();
                    var paths = string.Join(", ", request.Changes.Take(3).Select(c => c.Path));
                    var message = request.Changes.Count == 1
                        ? $"{(request.Changes[0].Content is null ? "Delete" : "Edit")} {request.Changes[0].Path}"
                        : $"Edit {request.Changes.Count} files: {paths}";
                    draft.HeadCommit = await mirror.WriteDraftAsync(id, draft.HeadCommit, request.Changes, message, ct);
                    // A conflicted file counts as resolved once it is saved without conflict markers or deleted.
                    draft.ConflictPaths = draft.ConflictPaths.Where(path => request.Changes.SingleOrDefault(c => c.Path == path) is not { } change
                        || change.Content is not null && HasConflictMarkers(change.Content)).ToList();
                    draft.UpdatedAt = clock.GetUtcNow();
                    await db.SaveChangesAsync(ct);
                    return TypedResults.Ok(await DetailAsync(db, mirror, options.Value, draft, ct));
                }, ct);
            }
            catch (GitException e) { return TypedResults.Problem(statusCode: 409, title: e.Message); }
        }).WithName("changeDraft");

        group.MapPost("/{id:guid}/update", async Task<Results<Ok<DraftDetailDto>, NotFound, ProblemHttpResult>> (Guid id,
            DraftUpdateRequest request, RunEngine engine, RepositoryMirror mirror, IOptions<OctopushyOptions> options,
            TimeProvider clock, CancellationToken ct) =>
        {
            try
            {
                return await engine.ExecuteAsync<Results<Ok<DraftDetailDto>, NotFound, ProblemHttpResult>>(async (db, _) =>
                {
                    var draft = await db.RepositoryDrafts.SingleOrDefaultAsync(d => d.Id == id, ct);
                    if (draft is null) return TypedResults.NotFound();
                    var settings = await db.RepositorySettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct);
                    if (EditBlockedReason(draft, settings) is { } blocked) throw new GitException(blocked);
                    if (draft.HeadCommit != request.ExpectedHead) throw new DraftChangedException();
                    if (settings!.HeadCommit is not { } onto || onto == draft.BaseCommit)
                        throw new GitException("The draft is already based on the latest synced commit.");
                    var updated = await mirror.UpdateDraftAsync(id, draft.HeadCommit, draft.BaseCommit, onto, ct);
                    draft.BaseCommit = onto;
                    draft.HeadCommit = updated.Head;
                    draft.ConflictPaths = draft.ConflictPaths.Union(updated.Conflicts, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                    draft.UpdatedAt = clock.GetUtcNow();
                    await db.SaveChangesAsync(ct);
                    return TypedResults.Ok(await DetailAsync(db, mirror, options.Value, draft, ct));
                }, ct);
            }
            catch (GitException e) { return TypedResults.Problem(statusCode: 409, title: e.Message); }
        }).WithName("updateDraft");

        group.MapPost("/{id:guid}/validate", async Task<Results<Ok<DraftValidationDto>, NotFound, ProblemHttpResult>> (Guid id,
            OctopushyDb db, RepositoryMirror mirror, RevisionCache revisions, CancellationToken ct) =>
        {
            var draft = await db.RepositoryDrafts.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
            if (draft is null) return TypedResults.NotFound();
            try { return TypedResults.Ok(await ValidateAsync(mirror, revisions, draft.BaseCommit, draft.HeadCommit, ct)); }
            catch (GitException e) { return TypedResults.Problem(statusCode: 409, title: e.Message); }
        }).WithName("validateDraft");

        group.MapPost("/{id:guid}/publish", async Task<Results<Ok<DraftPublishedDto>, NotFound, ProblemHttpResult>> (Guid id,
            DraftPublishRequest request, HttpContext http, RunEngine engine, RepositoryMirror mirror, RepositorySync sync,
            IOptions<OctopushyOptions> options, TimeProvider clock, CancellationToken ct) =>
        {
            var message = request.Message?.Trim() ?? "";
            if (!request.ConfirmRepositoryWrite)
                return TypedResults.Problem(statusCode: 400, title: "Explicit confirmation is required before pushing to the configured repository branch.");
            if (message.Length is < 1 or > 4000)
                return TypedResults.Problem(statusCode: 400, title: "Write a commit message of up to 4000 characters.");
            var actor = http.User.Identity?.Name;
            if (string.IsNullOrWhiteSpace(actor))
                return TypedResults.Problem(statusCode: 400, title: "An authenticated operator is required.");
            try
            {
                return await engine.ExecuteAsync<Results<Ok<DraftPublishedDto>, NotFound, ProblemHttpResult>>(async (db, services) =>
                {
                    var draft = await db.RepositoryDrafts.SingleOrDefaultAsync(d => d.Id == id, ct);
                    if (draft is null) return TypedResults.NotFound();
                    var settings = await db.RepositorySettings.AsNoTracking().SingleAsync(s => s.Id == 1, ct);
                    if (draft.HeadCommit != request.ExpectedHead) throw new DraftChangedException();
                    if (PublishBlockedReason(draft, settings, options.Value, changedFiles: null) is { } blocked) throw new GitException(blocked);
                    var credential = GitCredential.None;
                    if (settings.CredentialKind != "none")
                    {
                        var stored = await services.GetRequiredService<VariableStore>().GetAsync(RepositorySync.CredentialKey, ct)
                            ?? throw new GitException("The configured repository credential is missing.");
                        credential = new(settings.CredentialKind, settings.CredentialUsername, stored.Value);
                    }
                    var author = new CommitAuthor(actor, $"{EmailUnsafe().Replace(actor.ToLowerInvariant(), "-")}@octopushy.invalid");
                    var revisions = services.GetRequiredService<RevisionCache>();
                    // ponytail: one bounded fetch/merge/validate/push loop under the engine gate, like the
                    // worker wizard; a branch that keeps moving fails after a few attempts and keeps the draft.
                    for (var attempt = 1; ; attempt++)
                    {
                        var candidate = await mirror.PreparePublishAsync(settings.Url!, settings.Branch, credential,
                            draft.BaseCommit, draft.HeadCommit, message, author, ct);
                        if (candidate.Conflicts.Count > 0)
                            throw new GitException($"The branch changed and {candidate.Conflicts.Count} file(s) conflict with the draft: "
                                + $"{string.Join(", ", candidate.Conflicts.Take(5))}. Sync the repository, then update the draft to resolve them.");
                        var validation = await ValidateAsync(mirror, revisions, candidate.Remote, candidate.Commit!, ct);
                        if (!validation.Valid)
                        {
                            var first = validation.NewIssues.Concat(validation.FormatIssues).Where(i => i.Severity == "error")
                                .Take(3).Select(i => $"{i.File}{(i.Line is { } line ? $":{line}" : "")}: {i.Message}");
                            throw new GitException($"The draft does not validate: {string.Join("; ", first)}");
                        }
                        try
                        {
                            await mirror.PushAsync(settings.Url!, settings.Branch, credential, candidate.Remote, candidate.Commit!, ct);
                        }
                        catch (GitException) when (attempt < PushAttempts)
                        {
                            // Retry only when the branch moved under the push; any other failure is reported.
                            if (await mirror.FetchAsync(settings.Url!, settings.Branch, credential, ct) == candidate.Remote) throw;
                            continue;
                        }
                        // The published commit and its parent are on the branch, so the draft keeps
                        // showing exactly what it published after its own ref is gone.
                        draft.BaseCommit = candidate.Remote;
                        draft.HeadCommit = candidate.Commit!;
                        draft.PublishedCommit = candidate.Commit;
                        draft.PublishedAt = draft.UpdatedAt = clock.GetUtcNow();
                        draft.PublishedBy = actor;
                        await db.SaveChangesAsync(ct);
                        await mirror.DeleteDraftAsync(id, ct);
                        sync.Trigger();
                        return TypedResults.Ok(new DraftPublishedDto(candidate.Commit!, Summary(draft, settings, await CountChangesAsync(mirror, draft, ct))));
                    }
                }, ct);
            }
            catch (GitException e) { return TypedResults.Problem(statusCode: 409, title: e.Message); }
        }).WithName("publishDraft");
    }

    internal static string Status(RepositoryDraft draft, RepositorySettings? settings) =>
        draft.PublishedCommit is not null ? "published"
        : settings is null || settings.Url is null || settings.SourceId != draft.SourceId ? "stale"
        : draft.ConflictPaths.Count > 0 ? "conflicted"
        : "open";

    private static DraftSummaryDto Summary(RepositoryDraft draft, RepositorySettings? settings, int? changedFiles)
    {
        var status = Status(draft, settings);
        return new(draft.Id, draft.Title, status, draft.BaseCommit, draft.HeadCommit, changedFiles,
            status is "open" or "conflicted" && settings?.HeadCommit is { } head && head != draft.BaseCommit,
            draft.ConflictPaths, draft.CreatedAt, draft.UpdatedAt, draft.PublishedCommit, draft.PublishedAt, draft.PublishedBy);
    }

    private static string? EditBlockedReason(RepositoryDraft draft, RepositorySettings? settings) => Status(draft, settings) switch
    {
        "published" => "This draft has been published; start a new draft to make more changes.",
        "stale" => "The repository source changed since this draft was started. Discard it and start a new draft.",
        _ => null,
    };

    private static string? PublishBlockedReason(RepositoryDraft draft, RepositorySettings? settings, OctopushyOptions options, int? changedFiles)
    {
        if (EditBlockedReason(draft, settings) is { } blocked) return blocked;
        if (!options.RepositoryEditor.PublishEnabled)
            return "The operator must enable RepositoryEditor.PublishEnabled before drafts can be pushed to Git.";
        if (draft.ConflictPaths.Count > 0)
            return $"Resolve the conflicts in {string.Join(", ", draft.ConflictPaths.Take(5))} and save each file first.";
        if (changedFiles == 0) return "The draft has no changes yet.";
        return null;
    }

    private static async Task<int?> CountChangesAsync(RepositoryMirror mirror, RepositoryDraft draft, CancellationToken ct)
    {
        try { return (await mirror.ChangesAsync(draft.BaseCommit, draft.HeadCommit, ct)).Count; }
        catch (GitException) { return null; }
    }

    private static async Task<DraftDetailDto> DetailAsync(OctopushyDb db, RepositoryMirror mirror, OctopushyOptions options,
        RepositoryDraft draft, CancellationToken ct)
    {
        var settings = await db.RepositorySettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct);
        var changes = await mirror.ChangesAsync(draft.BaseCommit, draft.HeadCommit, ct);
        var files = (await mirror.TreeAsync(draft.HeadCommit, ct)).Select(f => new DraftFileDto(f.Path, f.Size, Kind(f))).ToList();
        return new(Summary(draft, settings, changes.Count), settings?.Branch ?? "main", EditBlockedReason(draft, settings),
            PublishBlockedReason(draft, settings, options, changes.Count), changes, files);
    }

    private static string Kind(TreeFile file) => file.Mode switch
    {
        "100755" => "executable",
        "120000" => "link",
        "160000" => "submodule",
        _ => "file",
    };

    internal static bool HasConflictMarkers(string content) =>
        content.Split('\n').Any(line => line.StartsWith("<<<<<<< ", StringComparison.Ordinal) || line.StartsWith(">>>>>>> ", StringComparison.Ordinal));

    /// <summary>
    /// Compares the definitions of <paramref name="candidate"/> with those of <paramref name="baseCommit"/>.
    /// Only new errors fail validation, so an existing problem elsewhere never blocks an unrelated edit.
    /// Changed JSON and YAML files that are not definitions are checked for well-formedness.
    /// </summary>
    internal static async Task<DraftValidationDto> ValidateAsync(RepositoryMirror mirror, RevisionCache revisions,
        string baseCommit, string candidate, CancellationToken ct)
    {
        var before = await revisions.GetAsync(baseCommit, ct);
        var after = await revisions.GetAsync(candidate, ct);
        var known = before.Issues.Select(i => (i.Severity, i.File, i.Message, i.Address)).ToHashSet();
        var newIssues = after.Issues.Where(i => !known.Contains((i.Severity, i.File, i.Message, i.Address)))
            .Select(i => new IssueDto(i.Severity == IssueSeverity.Error ? "error" : "warning", i.File, i.Line, i.Message, i.Address))
            .ToList();
        var reported = after.Issues.Select(i => i.File).ToHashSet(StringComparer.Ordinal);
        var formatIssues = new List<IssueDto>();
        foreach (var change in await mirror.ChangesAsync(baseCommit, candidate, ct))
        {
            var extension = Path.GetExtension(change.Path).ToLowerInvariant();
            if (change.Change == "deleted" || extension is not (".json" or ".yaml" or ".yml") || reported.Contains(change.Path))
                continue;
            if (await mirror.ReadFileAsync(candidate, change.Path, ct) is not { Text: { } text }) continue;
            if (FormatIssue(change.Path, extension, text) is { } issue) formatIssues.Add(issue);
        }
        var valid = (after.RepositoryValid || !before.RepositoryValid)
            && !newIssues.Concat(formatIssues).Any(i => i.Severity == "error");
        return new(baseCommit, valid, newIssues, formatIssues);
    }

    private static IssueDto? FormatIssue(string path, string extension, string text)
    {
        try
        {
            if (extension == ".json")
            {
                using var _ = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            else
            {
                new YamlStream().Load(new StringReader(text));
            }
            return null;
        }
        catch (JsonException e)
        {
            return new IssueDto("error", path, e.LineNumber is { } line ? (int)line + 1 : null, $"Not valid JSON: {e.Message}", null);
        }
        catch (YamlException e)
        {
            return new IssueDto("error", path, (int)e.Start.Line, $"Not valid YAML: {e.Message}", null);
        }
    }

    [GeneratedRegex("[^a-z0-9._-]+")]
    private static partial Regex EmailUnsafe();
}
