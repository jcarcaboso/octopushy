using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Api;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Git;

/// <summary>Execution authority is attached to a loaded revision, never inferred from Git ancestry.</summary>
public static class RepositoryTrust
{
    public static bool IsCurrentSource(DefinitionRevision revision, RepositorySettings? settings) =>
        settings?.Url is not null && revision.SourceId is { } source && source == settings.SourceId;

    public static IQueryable<DefinitionRevision> CurrentRevisions(OctopushyDb db) =>
        db.Revisions.Where(r => r.SourceId != null && db.RepositorySettings.Any(s => s.Id == 1 && s.Url != null && s.SourceId == r.SourceId));

    public static IQueryable<DefinitionRevision> EligibleRevisions(OctopushyDb db) =>
        CurrentRevisions(db).Where(r => r.TrustedAt != null && r.TrustedBy != null && r.RepositoryValid);

    public static IQueryable<DefinitionRevision> HeadRevisions(OctopushyDb db) =>
        CurrentRevisions(db).Where(r => db.RepositorySettings.Any(s => s.Id == 1 && s.HeadCommit == r.Commit));

    public static async Task<string?> BlockedReasonAsync(OctopushyDb db, Run run, CancellationToken ct)
    {
        if (run.RevisionId is not { } id || run.Commit is null
            || !await EligibleRevisions(db).AnyAsync(r => r.Id == id && r.Commit == run.Commit, ct))
        {
            return "Waiting for trust: explicitly trust this exact revision of the current repository source. Legacy or previous-source work requires an explicit replan.";
        }
        return null;
    }

    /// <summary>Called under the engine gate. No repository code, credentials, or worker is involved.</summary>
    public static async Task<RevisionReviewDto> ReviewAsync(OctopushyDb db, RepositoryMirror mirror, Guid id, CancellationToken ct)
    {
        var revision = await db.Revisions.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new RunException("There is no such revision.");
        var settings = await db.RepositorySettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct);
        var current = IsCurrentSource(revision, settings);
        var dto = Views.Revision(revision, current);
        var basis = await EligibleRevisions(db).Where(r => r.Id != id).OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(ct);
        string? blocked = !current ? "This revision is not from the current repository source. Sync and review the current source."
            : !revision.RepositoryValid ? "The repository definition is invalid; fix it and sync again."
            : revision.TrustedAt is not null ? "This exact revision is already trusted."
            : null;
        if (!current)
        {
            return new(dto, null, "", false, false, blocked);
        }
        try
        {
            var diff = await mirror.ReviewDiffAsync(basis?.Commit, revision.Commit, ct);
            return new(dto, basis?.Commit, diff, false, blocked is null, blocked);
        }
        catch (GitException e)
        {
            return new(dto, basis?.Commit, "", e is ReviewTooLargeException, false, $"Review unavailable: {e.Message}");
        }
    }
}
