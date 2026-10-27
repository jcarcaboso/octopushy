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

    /// <summary>Observes an installation now, in the background, unless an observation of it is under way.</summary>
    public Task ObserveAsync(Guid resourceId, CancellationToken cancellationToken) =>
        engine.ExecuteAsync(async (db, _) =>
        {
            var (resource, revision, repository) = await LoadAsync(db, resourceId, ResourceKinds.Installation, cancellationToken);
            var context = RunFactory.Observation(repository, repository.Installation(resource.Address)!, revision);
            await engine.ObserveAsync(db, resource, revision, context, cancellationToken);
            return 0;
        }, cancellationToken);

    /// <summary>Observes every valid installation that was last sent to be observed longer ago than the interval.</summary>
    public Task ScheduleObservesAsync(CancellationToken cancellationToken) =>
        engine.ExecuteAsync(async (db, _) =>
        {
            var eligible = RepositoryTrust.EligibleRevisions(db);
            var revision = await eligible.AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
            if (revision is null) return 0;

            var cutoff = clock.GetUtcNow().AddMinutes(-options.Value.Scheduling.ObserveMinutes);
            var due = await db.Resources
                .Where(r => r.Kind == ResourceKinds.Installation && r.RetiredAt == null)
                .Where(r => r.ObserveRequestedAt == null || r.ObserveRequestedAt < cutoff)
                .ToListAsync(cancellationToken);
            if (due.Count == 0) return 0;

            var repository = await revisions.GetAsync(revision.Commit, cancellationToken);
            var observed = 0;
            foreach (var resource in due)
            {
                // Discovery may have invalidated/removed an installation in an untrusted head.
                // Scheduled work uses the definition and validity of the eligible snapshot only.
                if (repository.Resource(resource.Address)?.Valid == true && repository.Installation(resource.Address) is { } installation)
                {
                    await engine.ObserveAsync(db, resource, revision, RunFactory.Observation(repository, installation, revision), cancellationToken);
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
