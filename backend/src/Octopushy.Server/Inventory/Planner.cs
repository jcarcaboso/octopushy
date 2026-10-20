using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Inventory;

/// <summary>Starts runs on request ("plan now", "observe now") and observes installations on a schedule.</summary>
public sealed class Planner(RunEngine engine, RunFactory factory, RevisionCache revisions, IOptions<OctopushyOptions> options, TimeProvider clock)
{
    /// <summary>Plans a guest at the latest revision, or retires it when its definition says so.</summary>
    public Task<Guid> PlanGuestAsync(Guid resourceId, CancellationToken cancellationToken) =>
        engine.ExecuteAsync(async (db, _) =>
        {
            var (resource, revision, repository) = await LoadAsync(db, resourceId, ResourceKinds.Guest, cancellationToken);
            var guest = repository.Guest(resource.Address) ?? throw new RunException("The guest is not in the latest revision.");
            var run = factory.Guest(repository, guest, resource, revision, "manual");
            await engine.AddRunsAsync(db, [run], cancellationToken);
            return run.Id;
        }, cancellationToken);

    public Task<Guid> ObserveAsync(Guid resourceId, CancellationToken cancellationToken) =>
        engine.ExecuteAsync(async (db, _) =>
        {
            var (resource, revision, repository) = await LoadAsync(db, resourceId, ResourceKinds.Installation, cancellationToken);
            var run = factory.Observe(repository, repository.Installation(resource.Address)!, resource, revision, "manual");
            await engine.AddRunsAsync(db, [run], cancellationToken);
            return run.Id;
        }, cancellationToken);

    /// <summary>Observes every valid installation whose last observation is older than the interval.</summary>
    public Task ScheduleObservesAsync(CancellationToken cancellationToken) =>
        engine.ExecuteAsync(async (db, _) =>
        {
            var revision = await db.Revisions.AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
            if (revision is null || !revision.RepositoryValid)
            {
                return 0;
            }

            var cutoff = clock.GetUtcNow().AddMinutes(-options.Value.Scheduling.ObserveMinutes);
            var due = await db.Resources
                .Where(r => r.Kind == ResourceKinds.Installation && r.Valid && r.MissingSince == null)
                .Where(r => !db.Runs.Any(run => run.ResourceId == r.Id
                                                && (run.CreatedAt > cutoff
                                                    || (run.Status != RunStatuses.Succeeded && run.Status != RunStatuses.Failed && run.Status != RunStatuses.Cancelled))))
                .ToListAsync(cancellationToken);
            if (due.Count == 0)
            {
                return 0;
            }

            var repository = await revisions.GetAsync(revision.Commit, cancellationToken);
            var runs = due
                .Select(r => (Resource: r, Definition: repository.Installation(r.Address)))
                .Where(x => x.Definition is not null)
                .Select(x => factory.Observe(repository, x.Definition!, x.Resource, revision, "schedule"))
                .ToList();
            await engine.AddRunsAsync(db, runs, cancellationToken);
            return runs.Count;
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

        var revision = await db.Revisions.AsNoTracking().OrderByDescending(r => r.Sequence).FirstAsync(cancellationToken);
        var repository = await revisions.GetAsync(revision.Commit, cancellationToken);
        return (resource, revision, repository);
    }
}
