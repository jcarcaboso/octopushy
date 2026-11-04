using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
using Octopushy.Server.Git;

namespace Octopushy.Server.Changes;

/// <summary>
/// Settles changes whose push was attempted but not confirmed: <c>committing</c> ones whose request ended mid-push, and
/// <c>uncertain</c> and <c>unknown</c> ones. Each sync does this right after fetching the branch, the first sync at startup
/// included. A change is found only by its own candidate commits, recorded before each push attempt: one of them reachable
/// from the branch head makes it <c>committed</c> with that commit, and the tracker then compares its values with what the
/// branch holds, as for any change. A commit's message is never used to find a change. One not found is <c>unknown</c>,
/// looked for again at every sync, until it is found or a person dismisses it. A <c>committing</c> change with no candidate
/// never reached a push, so it is refused: nothing was committed.
/// </summary>
public static class ChangeReconciler
{
    /// <summary>Runs inside the engine's gate, after <paramref name="branch"/> was fetched to <paramref name="head"/>.</summary>
    public static async Task ReconcileAsync(OctopushyDb db, RepositoryMirror mirror, string branch, string head, DateTimeOffset now, CancellationToken ct)
    {
        var pending = await db.Changes
            .Where(c => c.Status == ChangeStatuses.Committing || c.Status == ChangeStatuses.Uncertain || c.Status == ChangeStatuses.Unknown)
            .ToListAsync(ct);
        foreach (var change in pending)
        {
            if (change.CandidateCommits.Count == 0)
            {
                if (change.Status == ChangeStatuses.Committing)
                    Settle(change, ChangeStatuses.Refused, "The request ended before anything was pushed: nothing was committed.", now);
                continue;
            }

            if (await LandedAsync(mirror, change, head, ct) is { } landed)
            {
                change.Commit = landed;
                Settle(change, ChangeStatuses.Committed, $"Found on {branch}; waiting for the sync.", now);
            }
            else if (change.Status != ChangeStatuses.Unknown)
            {
                Settle(change, ChangeStatuses.Unknown,
                    $"Not on {branch} (yet): none of the {change.CandidateCommits.Count} commit(s) its push prepared is there. Each sync "
                    + "looks again; dismiss it once you know it will not arrive.", now);
            }
        }

        if (pending.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>The first of the change's candidate commits that <paramref name="head"/> reaches, or null.</summary>
    public static async Task<string?> LandedAsync(RepositoryMirror mirror, ResourceChange change, string head, CancellationToken ct)
    {
        foreach (var candidate in change.CandidateCommits)
        {
            try
            {
                if (await mirror.IsAncestorAsync(candidate, head, ct))
                    return candidate;
            }
            catch (GitException)
            {
                // A candidate this mirror does not have cannot be on the branch it fetched.
            }
        }

        return null;
    }

    private static void Settle(ResourceChange change, string status, string detail, DateTimeOffset now)
    {
        change.Status = status;
        change.Detail = detail;
        change.UpdatedAt = now;
    }
}
