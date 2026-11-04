using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
using Octopushy.Server.Git;

namespace Octopushy.Server.Changes;

/// <summary>
/// Settles changes left <c>committing</c>: recorded before their push, whose request ended without saying how the push went,
/// such as a request cancelled mid-push or a server stopped during it. Each sync does this right after fetching the branch, the
/// first sync at startup included: a change whose trailer, <c>Octopushy-Change: &lt;id&gt;</c>, is on the branch is
/// <c>committed</c> with that commit; one still absent after <see cref="Timeout"/> never reached it and is <c>failed</c>.
/// </summary>
public static class ChangeReconciler
{
    /// <summary>How long a push can still land: longer than any push, retries included, takes.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);

    public static string Trailer(Guid id) => $"Octopushy-Change: {id}";

    /// <summary>Runs inside the engine's gate, after <paramref name="branch"/> was fetched.</summary>
    public static async Task ReconcileAsync(OctopushyDb db, RepositoryMirror mirror, string branch, DateTimeOffset now, CancellationToken ct)
    {
        var pending = await db.Changes.Where(c => c.Status == ChangeStatuses.Committing).ToListAsync(ct);
        foreach (var change in pending)
        {
            if (await mirror.FindCommitWithAsync(branch, Trailer(change.Id), ct) is { } commit)
            {
                change.Commit = commit;
                change.Status = ChangeStatuses.Committed;
                change.Detail = "Waiting for the sync.";
                change.UpdatedAt = now;
            }
            else if (now - change.CreatedAt > Timeout)
            {
                change.Status = ChangeStatuses.Failed;
                change.Detail = $"Its commit never reached {branch}: the push did not finish, and nothing was committed.";
                change.UpdatedAt = now;
            }
        }

        if (pending.Count > 0)
            await db.SaveChangesAsync(ct);
    }
}
