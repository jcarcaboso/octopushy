using Microsoft.EntityFrameworkCore;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Inventory;

/// <summary>Service runs: a configure run on a service's own resource, under the same rules as a guest's.</summary>
public sealed partial class Planner
{
    /// <summary>Runs one action of a service's recipe at the latest revision; by default the one that follows changes.</summary>
    public Task<Guid> RunServiceAsync(Guid resourceId, string? action, CancellationToken cancellationToken) =>
        engine.ExecuteAsync(async (db, _) =>
        {
            var missing = await db.Resources.FirstOrDefaultAsync(r => r.Id == resourceId && r.Kind == ResourceKinds.Service
                && r.MissingSince != null && r.RetiredAt == null, cancellationToken);
            if (missing is not null)
            {
                // What a person can ask of a service whose folder is gone: its removal, again.
                var head = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken)
                    ?? throw new RunException("Sync the current repository source first.");
                var current = await revisions.GetAsync(head.Commit, cancellationToken);
                var removal = await ServiceRemoval.ProposeAsync(db, revisions, factory, current, missing, head, clock.GetUtcNow(), force: true, cancellationToken)
                    ?? throw new RunException("There is no removal to run: the recipe declares none, one is under way, or the guest is going too.");
                await engine.AddRunsAsync(db, [removal], cancellationToken);
                return removal.Id;
            }

            var (resource, revision, repository) = await LoadAsync(db, resourceId, ResourceKinds.Service, cancellationToken);
            var service = repository.Service(resource.Address) ?? throw new RunException("The service is not in the latest revision.");
            var guest = await db.Resources.FirstAsync(r => r.Address == service.Guest && r.RetiredAt == null, cancellationToken);
            if (await GuestWork.ServicesWaitForAsync(db, guest, repository, cancellationToken) is { } waiting)
                throw new RunException(waiting);
            var inputs = ConfigureInputs.Build(repository, service);
            var chosen = action ?? inputs.Configure.OnChange?.Id
                ?? throw new RunException("Choose an action: the recipe marks none with onChange.");
            if (inputs.Configure.Actions.GetValueOrDefault(chosen)?.Removal == true)
                throw new RunException("Deleting the service's folder is what asks for its removal.");
            var run = await ServiceRunAsync(db, repository, service, resource, revision, chosen, "manual", cancellationToken);
            if (chosen == inputs.Configure.OnChange?.Id
                && await db.GuestConfigurations.FirstOrDefaultAsync(c => c.ResourceId == resource.Id, cancellationToken) is { } configuration)
                configuration.ProposedHash = inputs.Hash;
            await engine.AddRunsAsync(db, [run], cancellationToken);
            return run.Id;
        }, cancellationToken);

    /// <summary>
    /// Stops tracking a service that is missing and that nothing will remove. It changes nothing on the guest.
    /// </summary>
    public Task ForgetServiceAsync(Guid resourceId, CancellationToken cancellationToken) =>
        engine.ExecuteAsync(async (db, _) =>
        {
            var resource = await db.Resources.FirstOrDefaultAsync(r => r.Id == resourceId && r.Kind == ResourceKinds.Service && r.RetiredAt == null, cancellationToken)
                ?? throw new RunException("There is no such service.");
            if (resource.MissingSince is null)
                throw new RunException("Its folder is still in the repository: delete the folder to remove it.");
            var running = await db.Runs.AsNoTracking().Where(r => r.ResourceId == resourceId && r.Status != RunStatuses.Succeeded
                && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled).Select(r => (int?)r.Number).FirstOrDefaultAsync(cancellationToken);
            if (running is { } number)
                throw new RunException($"Run #{number} is still going. Finish or cancel it first.");
            resource.RetiredAt = clock.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            return 0;
        }, cancellationToken);

    /// <summary>
    /// Proposes the onChange action of each service whose inputs changed and whose guest is ready.
    /// Each input hash gets one automatic run, so a failure waits for a new commit or a person.
    /// </summary>
    public Task ScheduleServicesAsync(CancellationToken cancellationToken) =>
        engine.ExecuteAsync(async (db, _) =>
        {
            var revision = await RepositoryTrust.HeadRevisions(db).AsNoTracking().Where(r => r.RepositoryValid)
                .OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
            if (revision is null) return 0;

            // A removal that had to wait for a run to end: deleting the folder was the only other time anything looks.
            var removals = new List<Run>();
            var head = await revisions.GetAsync(revision.Commit, cancellationToken);
            foreach (var missing in await db.Resources.Where(r => r.Kind == ResourceKinds.Service && r.MissingSince != null && r.RetiredAt == null).ToListAsync(cancellationToken))
            {
                try
                {
                    if (await ServiceRemoval.ProposeAsync(db, revisions, factory, head, missing, revision, clock.GetUtcNow(), force: false, cancellationToken) is { } removal)
                        removals.Add(removal);
                }
                catch (RunException)
                {
                    // Said when the folder went; nothing here would add to it, every pass.
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await engine.AddRunsAsync(db, removals, cancellationToken);

            var due = await db.GuestConfigurations
                .Where(c => c.DesiredHash != null && c.DesiredHash != c.ProposedHash)
                .Join(db.Resources.Where(r => r.Kind == ResourceKinds.Service && r.Valid && r.RetiredAt == null && r.MissingSince == null
                        && !db.Runs.Any(run => run.ResourceId == r.Id && run.Status != RunStatuses.Succeeded
                            && run.Status != RunStatuses.Failed && run.Status != RunStatuses.Cancelled)),
                    c => c.ResourceId, r => r.Id, (c, r) => new { Configuration = c, Resource = r })
                .ToListAsync(cancellationToken);
            if (due.Count == 0) return removals.Count;

            var repository = head;
            var runs = new List<Run>();
            foreach (var item in due)
            {
                if (repository.Resource(item.Resource.Address)?.Valid != true || repository.Service(item.Resource.Address) is not { } service)
                    continue;
                var guest = await db.Resources.FirstOrDefaultAsync(r => r.Address == service.Guest && r.Kind == ResourceKinds.Guest && r.RetiredAt == null, cancellationToken);
                if (guest is null || await GuestWork.ServicesWaitForAsync(db, guest, repository, cancellationToken) is not null)
                    continue;
                var inputs = ConfigureInputs.Build(repository, service);
                item.Configuration.DesiredHash = inputs.Hash;
                item.Configuration.ProposedHash = inputs.Hash;
                if (inputs.Configure.OnChange is { } action)
                    runs.Add(await ServiceRunAsync(db, repository, service, item.Resource, revision, action.Id, "revision", cancellationToken));
            }

            await engine.AddRunsAsync(db, runs, cancellationToken);
            return runs.Count;
        }, cancellationToken);

    private async Task<Run> ServiceRunAsync(OctopushyDb db, LabRepository repository, ServiceDefinition service, Resource resource, DefinitionRevision revision,
        string action, string reason, CancellationToken cancellationToken)
    {
        var configuration = await db.GuestConfigurations.FirstOrDefaultAsync(c => c.ResourceId == resource.Id, cancellationToken);
        var recipe = repository.Recipes[service.Recipe];
        var applied = configuration?.AppliedRevisionId is { } id
            ? await db.Revisions.AsNoTracking().Where(r => r.Id == id).Select(r => r.Commit).FirstOrDefaultAsync(cancellationToken)
            : null;
        var changed = applied is null ? null
            : await mirror.ChangedFilesAsync(applied, revision.Commit, [Path.GetDirectoryName(recipe.File)!, recipe.Root, service.Directory], cancellationToken);
        return factory.Configure(repository, repository.Guest(service.Guest)!, resource, revision, action, reason, configuration, changed, service);
    }
}
