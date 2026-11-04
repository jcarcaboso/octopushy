using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Definitions;
using Octopushy.Definitions.Yaml;
using Octopushy.Server.Api;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Changes;

/// <summary>
/// Follows a committed change: to the first revision that contains its commit and still carries its edit, then through the
/// runs that revision proposed for its resource, until they are applied or fail. A revision whose resource no longer has the
/// values the change wrote, or for a removal has the resource back, supersedes the change: its runs are another change's,
/// and the change is never shown applied by them. Nothing here starts work; it only reads where the normal process got to.
/// </summary>
public static class ChangeTracker
{
    public static async Task RefreshAsync(OctopushyDb db, RepositoryMirror mirror, RevisionCache revisions, ResourceChange change, DateTimeOffset now,
        CancellationToken ct)
    {
        // A change being pushed has no commit yet: reconciliation gives it one, or fails it.
        if (ChangeStatuses.IsFinal(change.Status) || change.Commit is not { } commit)
            return;
        var (status, detail) = (change.Status, change.Detail);
        if (change.RevisionId is null && await RevisionOfAsync(db, mirror, change, commit, ct) is { } found)
        {
            change.SyncedAt ??= now;
            // Always compared, on the change's own commit too: a commit found by reconciliation is held to what the change wrote.
            if (await LostAsync(revisions, change, found, ct) is { } lost)
            {
                Supersede(change, found.Commit, lost, now);
                return;
            }

            change.RevisionId = found.Id;
            (status, detail) = (ChangeStatuses.Synced, null);
        }

        if (change.RevisionId is { } revisionId && await db.Revisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == revisionId, ct) is { } revision)
        {
            var followed = await FollowAsync(db, mirror, revisions, change, revision, now, ct);
            if (followed is null)
                return;
            (status, detail) = followed.Value;
            // A revision whose inventory is not applied yet may still propose runs: nothing final is read from it, such as
            // no-change from an empty run list, until the next sync applies it and marks it.
            if (revision.InventoryAppliedAt is null && ChangeStatuses.IsFinal(status))
                (status, detail) = (ChangeStatuses.Synced,
                    $"Waiting for {Short(revision.Commit)} to be applied: its runs are proposed at the next sync.");
        }

        if (status != change.Status || detail != change.Detail)
        {
            change.Status = status;
            change.Detail = detail;
            change.UpdatedAt = now;
        }
    }

    private static void Supersede(ResourceChange change, string commit, string why, DateTimeOffset now)
    {
        change.SupersededBy = commit;
        change.Status = ChangeStatuses.Superseded;
        change.Detail = $"Superseded by {Short(commit)}: {why}";
        change.UpdatedAt = now;
    }

    /// <summary>
    /// The first revision of the current source whose commit is the change's or contains it on the way to the branch head: a
    /// commit pushed right after this one may be what the sync read. Looked up directly, however many revisions came between.
    /// </summary>
    private static async Task<DefinitionRevision?> RevisionOfAsync(OctopushyDb db, RepositoryMirror mirror, ResourceChange change, string commit,
        CancellationToken ct)
    {
        var containing = new List<string> { commit };
        var head = await db.RepositorySettings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.HeadCommit).FirstOrDefaultAsync(ct);
        if (head is not null && head != commit)
        {
            try
            {
                containing.AddRange(await mirror.DescendantsAsync(commit, head, ct));
            }
            catch (GitException)
            {
                // A commit no longer in the mirror has no descendants here.
            }
        }

        return await RepositoryTrust.CurrentRevisions(db).AsNoTracking()
            .Where(r => containing.Contains(r.Commit))
            .OrderBy(r => r.Sequence).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Why <paramref name="revision"/> no longer carries the change: its resource has other values in a section the change
    /// wrote, is gone, or, for a removal, is back. Null when the change is still there.
    /// </summary>
    private static async Task<string?> LostAsync(RevisionCache revisions, ResourceChange change, DefinitionRevision revision, CancellationToken ct)
    {
        LabRepository repository;
        try
        {
            repository = await revisions.GetAsync(revision, ct);
        }
        catch (GitException e)
        {
            return $"its definitions cannot be read ({e.Message}).";
        }

        var entry = repository.Resource(change.Address);
        if (change.Kind == ChangeKinds.Remove)
            return entry is null ? null : $"it defines {change.Address} again.";
        if (entry?.Json is not { } json)
            return entry is null ? $"it no longer defines {change.Address}." : $"its definition of {change.Address} cannot be read.";
        var edited = change.Edited is null ? null : JsonNode.Parse(change.Edited) as JsonObject;
        foreach (var (section, value) in edited ?? [])
        {
            if (!YamlEditor.Same(json[section], value))
                return $"it sets {section} otherwise.";
        }

        return null;
    }

    /// <summary>Where the change's runs are, or null when the change was superseded on the way.</summary>
    private static async Task<(string Status, string? Detail)?> FollowAsync(OctopushyDb db, RepositoryMirror mirror, RevisionCache revisions,
        ResourceChange change, DefinitionRevision revision, DateTimeOffset now, CancellationToken ct)
    {
        var resource = await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == change.ResourceId, ct);
        var candidates = await db.Runs.AsNoTracking().Include(r => r.Steps).ThenInclude(s => s.Attempts).AsSplitQuery()
            .Where(r => r.ResourceId == change.ResourceId && r.CreatedAt >= change.CreatedAt)
            .OrderBy(r => r.CreatedAt).ToListAsync(ct);
        // What the revision proposed for it: its own runs, or for a deleted folder the removal planned from the definition it
        // was last applied from. A run someone starts by hand at the same revision is theirs, not the change's.
        var runs = candidates.Where(r => r.Reason == "revision" && r.RevisionId == revision.Id
            || r.Operation == Operations.Retire && RunContext.Parse(r.Context).DefinitionRemovedIn == revision.Commit).ToList();
        if (runs.Count == 0 && change.Kind == ChangeKinds.Remove)
        {
            // A removal already under way when the folder was deleted is the one the change waits for.
            var retire = await db.Runs.AsNoTracking().Include(r => r.Steps).ThenInclude(s => s.Attempts).AsSplitQuery()
                .Where(r => r.ResourceId == change.ResourceId && r.Operation == Operations.Retire)
                .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
            if (retire is not null && (!RunStatuses.IsTerminal(retire.Status) || retire.Status == RunStatuses.Succeeded))
                runs.Add(retire);
        }

        if (runs.Any(r => !change.RunIds.Contains(r.Id)))
            change.RunIds = [.. change.RunIds, .. runs.Select(r => r.Id).Where(id => !change.RunIds.Contains(id))];

        // A run given up for a later revision: the change moves on with it while that revision still carries it.
        if (runs.Count > 0 && runs.All(r => RunStatuses.IsTerminal(r.Status))
            && runs.FirstOrDefault(r => r.Status == RunStatuses.Cancelled && r.Detail?.StartsWith("Superseded by ", StringComparison.Ordinal) == true) is not null
            && await NewerHeadAsync(db, mirror, change, revision, ct) is { } newer)
        {
            if (await LostAsync(revisions, change, newer, ct) is { } lost)
            {
                Supersede(change, newer.Commit, lost, now);
                return null;
            }

            change.RevisionId = newer.Id;
            change.RunIds = [];
            return await FollowAsync(db, mirror, revisions, change, newer, now, ct);
        }

        var owed = await OwedAsync(db, change, revision, ct);
        if (runs.Count == 0)
        {
            // An empty list is not "nothing to do" by itself: what the server recorded for the resource says why there is none.
            if (await ProblemAsync(db, change, revision, ct) is { } problem)
                return (ChangeStatuses.Failed, change.Kind == ChangeKinds.Remove ? problem : $"{problem} ({change.ResourceKind} at {Short(revision.Commit)})");
            if (owed is not null)
                return (ChangeStatuses.Planning, owed);
            if (change.Kind == ChangeKinds.Remove)
                return resource?.RetiredAt is not null
                    ? (ChangeStatuses.Applied, $"{change.Address} is retired.")
                    : (ChangeStatuses.Failed, $"{Short(revision.Commit)} proposed no removal of {change.Address}; use Plan removal on its page.");
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

    /// <summary>The head revision, when it is newer than <paramref name="revision"/> and comes after the change's commit.</summary>
    private static async Task<DefinitionRevision?> NewerHeadAsync(OctopushyDb db, RepositoryMirror mirror, ResourceChange change, DefinitionRevision revision,
        CancellationToken ct)
    {
        var head = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(ct);
        if (head is null || head.Id == revision.Id || head.Sequence < revision.Sequence)
            return null;
        try
        {
            return await mirror.IsAncestorAsync(change.Commit!, head.Commit, ct) ? head : null;
        }
        catch (GitException)
        {
            return null;
        }
    }

    /// <summary>
    /// The first error the server recorded for the resource when it applied <paramref name="revision"/>, such as a removal that
    /// cannot be planned. Errors of other revisions, earlier or later, are not this change's.
    /// </summary>
    private static async Task<string?> ProblemAsync(OctopushyDb db, ResourceChange change, DefinitionRevision revision, CancellationToken ct)
    {
        var recorded = await db.RevisionIssues.AsNoTracking().FirstOrDefaultAsync(i => i.ResourceId == change.ResourceId && i.RevisionId == revision.Id, ct);
        if (recorded is null)
            return null;
        var issues = JsonSerializer.Deserialize<List<DefinitionIssue>>(recorded.Issues, InventoryUpdater.IssueJson) ?? [];
        return issues.FirstOrDefault(i => i.Severity == IssueSeverity.Error)?.Message;
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
