using Microsoft.EntityFrameworkCore;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Inventory;

/// <summary>Pipeline runs of a service, under the same rules as a guest's.</summary>
public sealed partial class Planner
{
    /// <summary>Deploys a service at the latest revision, whatever its last run did, once its guest is ready.</summary>
    public Task<Guid> DeployServiceAsync(Guid resourceId, CancellationToken cancellationToken) =>
        engine.ExecuteAsync(async (db, _) =>
        {
            var (resource, revision, repository) = await LoadAsync(db, resourceId, ResourceKinds.Service, cancellationToken);
            var service = repository.Service(resource.Address) ?? throw new RunException("The service is not in the latest revision.");
            var guest = await db.Resources.FirstOrDefaultAsync(r => r.Address == service.GuestAddress && r.Kind == ResourceKinds.Guest && r.RetiredAt == null, cancellationToken)
                ?? throw new RunException("The service's guest is not known yet.");
            if (await GuestWork.WaitingForAsync(db, guest, repository, cancellationToken, service) is { } waiting)
                throw new RunException(waiting);
            var run = factory.Pipeline(repository, service, resource, guest, revision, "manual");
            if (await db.GuestConfigurations.FirstOrDefaultAsync(c => c.ResourceId == resource.Id, cancellationToken) is { } state)
                state.ProposedHash = run.InputsHash;
            await engine.AddRunsAsync(db, [run], cancellationToken);
            return run.Id;
        }, cancellationToken);

    /// <summary>
    /// Proposes a run of each service whose inputs changed and whose guest is ready. Each input hash gets one
    /// automatic run, so a failure waits for a new commit or a person instead of repeating.
    /// </summary>
    public Task SchedulePipelinesAsync(CancellationToken cancellationToken) =>
        engine.ExecuteAsync(async (db, _) =>
        {
            // The head, not the newest loaded, as for a guest's configuration. An invalid head proposes nothing.
            var revision = await RepositoryTrust.HeadRevisions(db).AsNoTracking().Where(r => r.RepositoryValid)
                .OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
            if (revision is null) return 0;

            var due = await db.GuestConfigurations
                .Where(c => c.DesiredHash != null && c.DesiredHash != c.ProposedHash)
                .Join(db.Resources.Where(r => r.Kind == ResourceKinds.Service && r.Valid && r.RetiredAt == null && r.MissingSince == null
                        && !db.Runs.Any(run => run.ResourceId == r.Id && run.Status != RunStatuses.Succeeded
                            && run.Status != RunStatuses.Failed && run.Status != RunStatuses.Cancelled)),
                    c => c.ResourceId, r => r.Id, (c, r) => new { State = c, Resource = r })
                .ToListAsync(cancellationToken);
            if (due.Count == 0) return 0;

            var repository = await revisions.GetAsync(revision.Commit, cancellationToken);
            var runs = new List<Run>();
            foreach (var item in due.OrderBy(i => i.Resource.Address, StringComparer.Ordinal))
            {
                if (repository.Resource(item.Resource.Address)?.Valid != true || repository.Service(item.Resource.Address) is not { } service)
                    continue;
                var guest = await db.Resources.FirstOrDefaultAsync(r => r.Address == service.GuestAddress && r.Kind == ResourceKinds.Guest && r.RetiredAt == null, cancellationToken);
                if (guest is null || await GuestWork.WaitingForAsync(db, guest, repository, cancellationToken, service) is not null)
                    continue;
                var run = factory.Pipeline(repository, service, item.Resource, guest, revision, "revision");
                item.State.DesiredHash = run.InputsHash;
                item.State.ProposedHash = run.InputsHash;
                runs.Add(run);
            }

            await engine.AddRunsAsync(db, runs, cancellationToken);
            return runs.Count;
        }, cancellationToken);
}
