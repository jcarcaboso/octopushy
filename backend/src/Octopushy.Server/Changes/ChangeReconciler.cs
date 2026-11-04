using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Changes;

/// <summary>
/// Checks changes against the branch after each fetch, the first sync at startup included. It watches two kinds:
/// <list type="bullet">
/// <item>Changes whose push was attempted and not confirmed: <c>committing</c> ones whose request ended mid-push, <c>uncertain</c>
/// and <c>unknown</c> ones, and <c>dismissed</c> ones, since a dismissal proves nothing. A change is found only by its own
/// candidate commits, recorded before each push attempt, never by a commit's message: one of them reachable from the branch head
/// makes it <c>committed</c> with that commit, and the tracker then compares its values with the branch's, as for any change.
/// One not found is <c>unknown</c>, looked for again later; a <c>committing</c> change with no candidate never reached a push,
/// so it is <c>refused</c>.</item>
/// <item>Changes committed and still being followed: one whose commit the branch no longer reaches, as after a force-push, is
/// <c>unknown</c> again, so it is not followed through a commit that is gone.</item>
/// </list>
/// Each sync checks a bounded batch, the longest unchecked first, so every change is reached in turn. The batch is picked under
/// the engine's gate <b>before</b> the branch is fetched, so every commit it holds was pushed before the fetch began, and a push
/// recorded while the sync waits is never judged against a head that predates it. The Git ancestry checks run outside the gate,
/// and the results are applied under it, unless a change moved meanwhile.
/// </summary>
public static class ChangeReconciler
{
    private static readonly string[] Unsettled =
        [ChangeStatuses.Committing, ChangeStatuses.Uncertain, ChangeStatuses.Unknown, ChangeStatuses.Dismissed];

    private static readonly string[] Following =
        [ChangeStatuses.Committed, ChangeStatuses.Synced, ChangeStatuses.Planning, ChangeStatuses.WaitingForApproval, ChangeStatuses.Applying];

    /// <summary>A change as it was when a sync picked it, before fetching.</summary>
    public sealed record Picked(Guid Id, string Status, string? Commit, List<string> Candidates);

    private sealed record Checked(Picked Change, bool OnBranch, string? Landed);

    /// <summary>Picks the batch to check, under the engine's gate. Call it before fetching the branch.</summary>
    public static Task<List<Picked>> SelectAsync(RunEngine engine, int batch, CancellationToken ct) =>
        engine.ExecuteAsync(async (db, _) => await db.Changes.AsNoTracking()
            .Where(c => Unsettled.Contains(c.Status) && (c.Status != ChangeStatuses.Dismissed || c.CandidateCommits.Count > 0)
                || Following.Contains(c.Status) && c.Commit != null)
            .OrderBy(c => c.CheckedAt != null).ThenBy(c => c.CheckedAt).ThenBy(c => c.CreatedAt)
            .Take(Math.Max(1, batch))
            .Select(c => new Picked(c.Id, c.Status, c.Commit, c.CandidateCommits))
            .ToListAsync(ct), ct);

    /// <summary>
    /// Checks <paramref name="picked"/>, selected before the fetch, against <paramref name="head"/>, what the fetch returned, and
    /// applies the results under the engine's gate to the changes that have not moved since they were picked.
    /// </summary>
    public static async Task ReconcileAsync(RunEngine engine, RepositoryMirror mirror, string branch, IReadOnlyList<Picked> picked, string head,
        TimeProvider clock, CancellationToken ct)
    {
        if (picked.Count == 0)
            return;

        var results = new List<Checked>();
        foreach (var change in picked)
        {
            if (change.Commit is { } commit && Following.Contains(change.Status))
                results.Add(new Checked(change, await ReachesAsync(mirror, commit, head, ct), null));
            else
                results.Add(new Checked(change, false, await LandedAsync(mirror, change.Candidates, head, ct)));
        }

        await engine.ExecuteAsync(async (db, _) =>
        {
            var now = clock.GetUtcNow();
            var ids = results.Select(r => r.Change.Id).ToList();
            var changes = await db.Changes.Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
            foreach (var result in results)
            {
                if (!changes.TryGetValue(result.Change.Id, out var change))
                    continue;
                change.CheckedAt = now;
                // It moved since it was picked, such as by a request or the tracker: what was checked is not about it any more.
                if (change.Status != result.Change.Status || change.Commit != result.Change.Commit
                    || change.CandidateCommits.Count != result.Change.Candidates.Count)
                    continue;
                Apply(change, result, branch, now);
            }

            await db.SaveChangesAsync(ct);
            return 0;
        }, ct);
    }

    private static void Apply(ResourceChange change, Checked result, string branch, DateTimeOffset now)
    {
        if (Following.Contains(change.Status))
        {
            if (result.OnBranch)
                return;
            var gone = change.Commit!;
            change.Commit = null;
            change.RevisionId = null;
            change.RunIds = [];
            change.SyncedAt = null;
            Settle(change, ChangeStatuses.Unknown,
                $"Its commit {gone[..Math.Min(10, gone.Length)]} is no longer on {branch}, as after a force-push. Each sync looks for its commits again.",
                now);
            return;
        }

        if (change.CandidateCommits.Count == 0)
        {
            if (change.Status == ChangeStatuses.Committing)
                Settle(change, ChangeStatuses.Refused, "The request ended before anything was pushed: nothing was committed.", now);
            return;
        }

        if (result.Landed is { } landed)
        {
            change.Commit = landed;
            Settle(change, ChangeStatuses.Committed, change.Status == ChangeStatuses.Dismissed
                ? $"Found on {branch} after {change.DismissedBy} dismissed it: it was committed, and is followed again."
                : $"Found on {branch}; waiting for the sync.", now);
        }
        else if (change.Status is ChangeStatuses.Committing or ChangeStatuses.Uncertain)
        {
            Settle(change, ChangeStatuses.Unknown,
                $"Not on {branch} (yet): none of the {change.CandidateCommits.Count} commit(s) its push prepared is there. Each sync "
                + "looks again; dismiss it to stop waiting.", now);
        }
    }

    /// <summary>The first of <paramref name="candidates"/> that <paramref name="head"/> reaches, or null.</summary>
    public static async Task<string?> LandedAsync(RepositoryMirror mirror, IEnumerable<string> candidates, string head, CancellationToken ct)
    {
        foreach (var candidate in candidates)
        {
            if (await ReachesAsync(mirror, candidate, head, ct))
                return candidate;
        }

        return null;
    }

    public static Task<string?> LandedAsync(RepositoryMirror mirror, ResourceChange change, string head, CancellationToken ct) =>
        LandedAsync(mirror, change.CandidateCommits, head, ct);

    private static async Task<bool> ReachesAsync(RepositoryMirror mirror, string commit, string head, CancellationToken ct)
    {
        try
        {
            return await mirror.IsAncestorAsync(commit, head, ct);
        }
        catch (GitException)
        {
            // A commit this mirror does not have cannot be on the branch it fetched.
            return false;
        }
    }

    private static void Settle(ResourceChange change, string status, string detail, DateTimeOffset now)
    {
        change.Status = status;
        change.Detail = detail;
        change.UpdatedAt = now;
    }
}
