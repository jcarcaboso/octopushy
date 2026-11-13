using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Changes;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;

namespace Octopushy.Server.Api;

/// <summary>What uses each store key, for the Variables page, and what keeps one from being deleted.</summary>
public static class VariableUses
{
    /// <summary>Who uses each store key: the valid definitions of the head revision, and runs still in progress.</summary>
    public static async Task<Dictionary<string, List<string>>> ByKeyAsync(OctopushyDb db, RevisionCache revisions, CancellationToken ct)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string key, string consumer)
        {
            if (!result.TryGetValue(key, out var list))
            {
                result[key] = list = [];
            }

            if (!list.Contains(consumer))
            {
                list.Add(consumer);
            }
        }

        if (await HeadRevisionAsync(db, ct) is { } revision)
        {
            foreach (var use in (await revisions.GetAsync(revision, ct)).StoreUses)
            {
                Add(use.Key, use.Consumer);
            }
        }

        var active = await db.Runs.AsNoTracking()
            .Where(r => r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled)
            .Select(r => new { r.Number, r.Context })
            .ToListAsync(ct);
        foreach (var run in active)
        {
            foreach (var key in RunContext.Parse(run.Context).Secrets.Values)
            {
                Add(key, $"run #{run.Number}");
            }
        }

        return result;
    }

    /// <summary>The revision the branch head is, as the last sync loaded it; never merely the newest one recorded.</summary>
    public static Task<DefinitionRevision?> HeadRevisionAsync(OctopushyDb db, CancellationToken ct) =>
        RepositoryTrust.HeadRevisions(db).AsNoTracking().FirstOrDefaultAsync(ct);

    /// <summary>
    /// What still holds <paramref name="key"/> apart from runs and attempts, which <see cref="VariableStore.DeleteAsync"/> checks
    /// itself: the definitions of the head revision; every definition file at the head, as loaded and as last fetched, read as
    /// plain YAML whether it validates or not; the observations the scheduler sends; and changes that are not final. Throws when
    /// any of them cannot be read: what cannot be read is not taken to be unused.
    /// </summary>
    public static async Task<List<string>> HoldingAsync(OctopushyDb db, RevisionCache revisions, RepositoryMirror mirror, string key, CancellationToken ct)
    {
        var holders = new List<string>((await ByKeyAsync(db, revisions, ct)).GetValueOrDefault(key) ?? []);
        var settings = await db.RepositorySettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct);
        if (settings?.Url is not null)
        {
            var head = await HeadRevisionAsync(db, ct);
            string? fetched;
            try
            {
                fetched = await mirror.BranchHeadAsync(settings.Branch, ct);
            }
            catch (GitException)
            {
                throw Unreadable("the branch head");
            }

            var commits = new[] { head?.Commit, settings.HeadCommit, fetched }.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            if (commits.Count == 0)
            {
                throw new StoreException("The lab repository has not been read yet, so whether a definition uses this key is not known. Sync it first.");
            }

            foreach (var commit in commits)
            {
                var at = $"commit {commit[..Math.Min(12, commit.Length)]} of the branch";
                List<string>? files;
                string? unreadable = null;
                try
                {
                    files = GeneratedValues.FilesReferencing(await mirror.SnapshotAsync(commit, ct), key, out unreadable);
                }
                catch (GitException)
                {
                    files = null;
                }

                foreach (var file in files ?? throw (unreadable is null ? Unreadable(at)
                    : new StoreException($"{unreadable} at {at} is not YAML that can be read, so whether it uses this key is not known. Nothing was deleted; fix that file, then try again.")))
                {
                    holders.Add($"{file} at the branch head");
                }
            }
        }

        // The scheduler keeps observing from the latest valid revision while the head is invalid: what it would send is held too.
        if (await Planner.ScheduledObservationRevisionAsync(db, ct) is { } observing)
        {
            var installations = await db.Resources.AsNoTracking()
                .Where(r => r.Kind == ResourceKinds.Installation && r.RetiredAt == null).OrderBy(r => r.Address).ToListAsync(ct);
            foreach (var (resource, context) in Planner.ScheduledObservations(await revisions.GetAsync(observing, ct), observing, installations))
            {
                if (context.Secrets.ContainsValue(key))
                {
                    holders.Add($"scheduled observations of {resource.Address}");
                }
            }
        }

        // A change that may still land a definition that uses the key keeps it, as it keeps a generated value.
        holders.AddRange(await GeneratedValues.OpenChangesUsingAsync(db, mirror, key)
            ?? throw new StoreException("A commit of an open change could not be read, so whether it uses this key is not known. Delete it once that change is settled."));
        return holders.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The definitions that pin <paramref name="version"/> of <paramref name="key"/>, in the head revision and in the one the
    /// scheduler observes from: what keeps that one version from being deleted, apart from runs and attempts.
    /// </summary>
    public static async Task<List<string>> PinningAsync(OctopushyDb db, RevisionCache revisions, string key, int version, CancellationToken ct)
    {
        var holders = new List<string>();
        var head = await HeadRevisionAsync(db, ct);
        var observing = await Planner.ScheduledObservationRevisionAsync(db, ct);
        foreach (var revision in new[] { head, observing }.OfType<DefinitionRevision>().DistinctBy(r => r.Id))
        {
            holders.AddRange((await revisions.GetAsync(revision, ct)).StoreUses
                .Where(u => u.Key == key && u.Version == version).Select(u => $"definition {u.Consumer}"));
        }

        return holders.Distinct(StringComparer.Ordinal).ToList();
    }

    private static StoreException Unreadable(string what) =>
        new($"The definitions at {what} could not be read, so whether one uses this key is not known. Nothing was deleted; fix the repository or sync it, then try again.");
}
