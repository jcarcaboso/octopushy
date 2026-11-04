using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Api;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Changes;

/// <summary>
/// Follows a committed change: to the first revision that contains its commit, then through the runs that revision proposed
/// for its resource, until they are applied or fail. Nothing here starts work; it only reads where the normal process got to.
/// </summary>
public static class ChangeTracker
{
    public static async Task RefreshAsync(OctopushyDb db, RepositoryMirror mirror, ResourceChange change, DateTimeOffset now, CancellationToken ct)
    {
        if (ChangeStatuses.IsFinal(change.Status) || change.Commit is not { } commit)
            return;
        var (status, detail) = (change.Status, change.Detail);
        if (change.RevisionId is null && await RevisionOfAsync(db, mirror, change, commit, ct) is { } found)
        {
            change.RevisionId = found.Id;
            change.SyncedAt = now;
            (status, detail) = (ChangeStatuses.Synced, null);
        }

        if (change.RevisionId is { } revisionId && await db.Revisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == revisionId, ct) is { } revision)
            (status, detail) = await FollowAsync(db, change, revision, ct);

        if (status != change.Status || detail != change.Detail)
        {
            change.Status = status;
            change.Detail = detail;
            change.UpdatedAt = now;
        }
    }

    /// <summary>
    /// The first revision of the current source, loaded since the change, whose commit is the change's or comes after it: a
    /// commit pushed right after this one may be what the sync read.
    /// </summary>
    private static async Task<DefinitionRevision?> RevisionOfAsync(OctopushyDb db, RepositoryMirror mirror, ResourceChange change, string commit,
        CancellationToken ct)
    {
        var candidates = await RepositoryTrust.CurrentRevisions(db).AsNoTracking()
            .Where(r => r.Commit == commit || r.LoadedAt >= change.CreatedAt)
            .OrderBy(r => r.Sequence).Take(20).ToListAsync(ct);
        foreach (var revision in candidates)
        {
            if (revision.Commit == commit)
                return revision;
            try
            {
                if (await mirror.IsAncestorAsync(commit, revision.Commit, ct))
                    return revision;
            }
            catch (GitException)
            {
                // A commit no longer in the mirror is not this one's descendant as far as anything here can tell.
            }
        }

        return null;
    }

    private static async Task<(string Status, string? Detail)> FollowAsync(OctopushyDb db, ResourceChange change, DefinitionRevision revision, CancellationToken ct)
    {
        var resource = await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == change.ResourceId, ct);
        var candidates = await db.Runs.AsNoTracking().Include(r => r.Steps).ThenInclude(s => s.Attempts).AsSplitQuery()
            .Where(r => r.ResourceId == change.ResourceId && r.CreatedAt >= change.CreatedAt)
            .OrderBy(r => r.CreatedAt).ToListAsync(ct);
        // What the revision proposed for it: its own runs, or for a deleted folder the removal planned from the definition it
        // was last applied from. A run someone starts by hand at the same revision is theirs, not the change's.
        var runs = candidates.Where(r => r.Reason == "revision" && r.RevisionId == revision.Id
            || r.Operation == Operations.Retire && RunContext.Parse(r.Context).DefinitionRemovedIn == revision.Commit).ToList();
        if (runs.Any(r => !change.RunIds.Contains(r.Id)))
            change.RunIds = [.. change.RunIds, .. runs.Select(r => r.Id).Where(id => !change.RunIds.Contains(id))];

        var owed = await OwedAsync(db, change, revision, ct);
        if (runs.Count == 0)
        {
            if (owed is not null)
                return (ChangeStatuses.Planning, owed);
            if (resource is { Valid: false } && change.Kind != ChangeKinds.Remove)
                return (ChangeStatuses.Failed, $"The definition is invalid in {Short(revision.Commit)}; see the {change.ResourceKind}'s issues.");
            return (ChangeStatuses.NoChange, $"{Short(revision.Commit)} proposes no run for {change.Address}: nothing a run is built from changed.");
        }

        if (runs.FirstOrDefault(r => !RunStatuses.IsTerminal(r.Status)) is { } active)
        {
            var label = $"Run #{active.Number}";
            if (active.Status == RunStatuses.AwaitingApproval)
                return (ChangeStatuses.WaitingForApproval, $"{label} waits for approval.");
            if (active.Status == RunStatuses.Uncertain)
                return (ChangeStatuses.Applying, $"{label} is uncertain: someone has to look at it and close it.");
            var approved = active.Steps.Any(s => s.Kind == StepKinds.Approval && s.Decision == "approved");
            var waiting = Views.Waiting(active) ?? active.Detail;
            return approved
                ? (ChangeStatuses.Applying, waiting is null ? $"{label} is applying." : $"{label}: {waiting}")
                : (ChangeStatuses.Planning, waiting is null ? $"{label} is planning." : $"{label}: {waiting}");
        }

        if (runs.FirstOrDefault(r => r.Status != RunStatuses.Succeeded) is { } failed)
            return (ChangeStatuses.Failed, $"Run #{failed.Number} {(failed.Status == RunStatuses.Cancelled ? "was cancelled" : "failed")}"
                + (failed.Detail is null ? "." : $": {failed.Detail}"));
        if (owed is not null)
            return (ChangeStatuses.Applying, owed);
        return (ChangeStatuses.Applied, runs.Count == 1 ? $"Run #{runs[0].Number} succeeded." : $"Runs {string.Join(", ", runs.Select(r => $"#{r.Number}"))} succeeded.");
    }

    /// <summary>
    /// A run the revision wants for the resource that the orchestrator has not proposed yet: a guest's setup, which waits for
    /// its provisioning, or a service's pipeline, which waits for its guest.
    /// </summary>
    private static async Task<string?> OwedAsync(OctopushyDb db, ResourceChange change, DefinitionRevision revision, CancellationToken ct)
    {
        var state = await db.GuestConfigurations.AsNoTracking().FirstOrDefaultAsync(c => c.ResourceId == change.ResourceId, ct);
        if (state is null || state.DesiredRevisionId != revision.Id || state.DesiredHash is null || state.DesiredHash == state.ProposedHash
            || state.DesiredHash == state.AppliedHash)
            return null;
        return change.ResourceKind == ResourceKinds.Guest
            ? "Setup is due: it is proposed once the guest's provisioning has caught up."
            : "The pipeline is due: it is proposed once the guest is ready for it.";
    }

    private static string Short(string commit) => commit[..Math.Min(10, commit.Length)];
}
