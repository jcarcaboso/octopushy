using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Inventory;

/// <summary>Starts runs and observations on request ("plan now", "observe now"), and observes installations on a schedule.</summary>
public sealed class Planner(RunEngine engine, RunFactory factory, RevisionCache revisions, IOptions<OctopushyOptions> options, TimeProvider clock)
{
    /// <summary>
    /// Plans a guest at the latest revision, or retires it when its definition says so. A guest
    /// whose folder was deleted is planned for removal.
    /// </summary>
    public Task<Guid> PlanGuestAsync(Guid resourceId, CancellationToken cancellationToken) =>
        engine.ExecuteAsync(async (db, _) =>
        {
            var missing = await db.Resources.FirstOrDefaultAsync(r => r.Id == resourceId && r.Kind == ResourceKinds.Guest
                && r.MissingSince != null && r.RetiredAt == null, cancellationToken);
            if (missing is not null)
            {
                var head = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken)
                    ?? throw new RunException("Sync the current repository source before planning this guest's removal.");
                var current = await revisions.GetAsync(head.Commit, cancellationToken);
                var removal = await GuestRemoval.ProposeAsync(db, revisions, factory, current, missing, head, force: true, cancellationToken)
                    ?? throw new RunException("A removal of this guest is already under way.");
                await engine.AddRunsAsync(db, [removal], cancellationToken);
                return removal.Id;
            }

            var (resource, revision, repository) = await LoadAsync(db, resourceId, ResourceKinds.Guest, cancellationToken);
            var guest = repository.Guest(resource.Address) ?? throw new RunException("The guest is not in the latest revision.");
            var run = factory.Guest(repository, guest, resource, revision, "manual");
            await engine.AddRunsAsync(db, [run], cancellationToken);
            return run.Id;
        }, cancellationToken);

    /// <summary>Plans a cluster at the latest revision: this always verifies every member, whatever the plan finds.</summary>
    public Task<Guid> PlanClusterAsync(Guid resourceId, CancellationToken cancellationToken) =>
        engine.ExecuteAsync(async (db, _) =>
        {
            var (resource, revision, repository) = await LoadAsync(db, resourceId, ResourceKinds.Cluster, cancellationToken);
            var cluster = repository.Cluster(resource.Address) ?? throw new RunException("The cluster is not in the latest revision.");
            var run = factory.Cluster(repository, cluster, resource, revision, "manual");
            await engine.AddRunsAsync(db, [run], cancellationToken);
            return run.Id;
        }, cancellationToken);

    /// <summary>
    /// Checks a cluster after a member guest changed. Replacing a container can leave an empty
    /// server that OpenTofu sees nothing wrong with, so the cluster's run looks at every member.
    /// </summary>
    public Task ScheduleClusterChecksAsync(CancellationToken cancellationToken) =>
        engine.ExecuteAsync(async (db, _) =>
        {
            var clusters = await db.Resources
                .Where(r => r.Kind == ResourceKinds.Cluster && r.RetiredAt == null && r.MissingSince == null && r.Valid && r.AppliedAt != null)
                .ToListAsync(cancellationToken);
            if (clusters.Count == 0) return 0;
            var revision = await RepositoryTrust.EligibleRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
            if (revision is null) return 0;
            var repository = await revisions.GetAsync(revision.Commit, cancellationToken);
            var proposed = 0;
            foreach (var resource in clusters)
            {
                if (repository.Cluster(resource.Address) is not { } cluster || repository.Resource(resource.Address)?.Valid != true)
                    continue;
                var runs = await db.Runs.AsNoTracking().Where(r => r.ResourceId == resource.Id)
                    .OrderByDescending(r => r.CreatedAt).Select(r => new { r.Status, r.CreatedAt, r.StartedAt }).Take(1).ToListAsync(cancellationToken);
                if (runs.Count == 1 && !RunStatuses.IsTerminal(runs[0].Status))
                    continue;
                // A run waits for its members to be ready, so it covers what they did until it started.
                var since = runs.Count == 1 ? runs[0].StartedAt ?? runs[0].CreatedAt : resource.AppliedAt!.Value;
                var members = cluster.Members.Select(m => m.Guest).ToList();
                var changed = await db.Resources.AsNoTracking()
                    .Where(r => r.Kind == ResourceKinds.Guest && members.Contains(r.Address) && r.AppliedAt > since)
                    .Select(r => r.Address).FirstOrDefaultAsync(cancellationToken);
                if (changed is null)
                    continue;
                await engine.AddRunsAsync(db, [factory.Cluster(repository, cluster, resource, revision, $"member {changed} changed")], cancellationToken);
                proposed++;
            }

            return proposed;
        }, cancellationToken);

    /// <summary>Observes an installation now, in the background, unless an observation of it is under way.</summary>
    public Task ObserveAsync(Guid resourceId, CancellationToken cancellationToken) =>
        engine.ExecuteAsync(async (db, _) =>
        {
            var (resource, revision, repository) = await LoadAsync(db, resourceId, ResourceKinds.Installation, cancellationToken);
            var context = RunFactory.Observation(repository, repository.Installation(resource.Address)!, revision);
            await engine.ObserveAsync(db, resource, revision, context, cancellationToken);
            return 0;
        }, cancellationToken);

    /// <summary>Observes every valid installation, and checks every formed cluster, last sent longer ago than the interval.</summary>
    public Task ScheduleObservesAsync(CancellationToken cancellationToken) =>
        engine.ExecuteAsync(async (db, _) =>
        {
            var eligible = RepositoryTrust.EligibleRevisions(db);
            var revision = await eligible.AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
            if (revision is null) return 0;

            var cutoff = clock.GetUtcNow().AddMinutes(-options.Value.Scheduling.ObserveMinutes);
            var due = await db.Resources
                .Where(r => r.RetiredAt == null && (r.Kind == ResourceKinds.Installation || r.Kind == ResourceKinds.Cluster && r.AppliedAt != null))
                .Where(r => r.ObserveRequestedAt == null || r.ObserveRequestedAt < cutoff)
                .ToListAsync(cancellationToken);
            if (due.Count == 0) return 0;

            var repository = await revisions.GetAsync(revision.Commit, cancellationToken);
            var observed = 0;
            foreach (var resource in due)
            {
                // The head may be invalid; scheduled work uses the latest valid snapshot only.
                if (repository.Resource(resource.Address)?.Valid != true)
                    continue;
                var context = repository.Installation(resource.Address) is { } installation ? RunFactory.Observation(repository, installation, revision)
                    : repository.Cluster(resource.Address) is { } cluster ? RunFactory.ClusterObservation(repository, cluster, revision)
                    : null;
                if (context is not null)
                {
                    await engine.ObserveAsync(db, resource, revision, context, cancellationToken);
                    observed++;
                }
            }

            return observed;
        }, cancellationToken);

    private async Task<(Resource Resource, DefinitionRevision Revision, Definitions.LabRepository Repository)> LoadAsync(
        OctopushyDb db,
        Guid resourceId,
        string kind,
        CancellationToken cancellationToken)
    {
        var resource = await db.Resources.FirstOrDefaultAsync(r => r.Id == resourceId && r.Kind == kind, cancellationToken)
            ?? throw new RunException("There is no such resource.");
        if (resource.RetiredAt is not null)
        {
            throw new RunException("The guest is retired.");
        }

        if (resource.MissingSince is not null)
        {
            throw new RunException("The definition is missing from the latest revision.");
        }

        if (!resource.Valid)
        {
            throw new RunException("The definition is invalid; fix the issues first.");
        }

        var running = await db.Runs.AsNoTracking()
            .Where(r => r.ResourceId == resourceId && r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled)
            .OrderBy(r => r.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (running is not null)
        {
            throw new RunException($"Run #{running.Number} is still {running.Status}.");
        }

        var revision = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken)
            ?? throw new RunException("Sync the current repository source before planning.");
        var repository = await revisions.GetAsync(revision.Commit, cancellationToken);
        return (resource, revision, repository);
    }
}
