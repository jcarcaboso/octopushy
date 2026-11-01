using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Inventory;

/// <summary>Starts runs and observations on request ("plan now", "observe now"), and observes installations on a schedule.</summary>
public sealed class Planner(RunEngine engine, RunFactory factory, RevisionCache revisions, RepositoryMirror mirror, IOptions<OctopushyOptions> options, TimeProvider clock)
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
                var removal = await GuestRemoval.ProposeAsync(db, revisions, factory, missing, head, force: true, cancellationToken)
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

    /// <summary>Runs one action of a guest's setup recipe at the latest revision; by default the one that follows changes.</summary>
    public Task<Guid> ConfigureGuestAsync(Guid resourceId, string? action, CancellationToken cancellationToken) =>
        engine.ExecuteAsync(async (db, _) =>
        {
            var (resource, revision, repository) = await LoadAsync(db, resourceId, ResourceKinds.Guest, cancellationToken);
            var guest = repository.Guest(resource.Address) ?? throw new RunException("The guest is not in the latest revision.");
            if (guest.SetupRecipe is null)
                throw new RunException("This guest has no setup recipe.");
            if (resource.BoundVmid is null)
                throw new RunException("Provision this guest before configuring it.");
            var inputs = ConfigureInputs.Build(repository, guest);
            var chosen = action ?? inputs.Configure.OnChange?.Id
                ?? throw new RunException("Choose an action: the recipe marks none with onChange.");
            var run = await ConfigureRunAsync(db, repository, guest, resource, revision, chosen, "manual", cancellationToken);
            if (chosen == inputs.Configure.OnChange?.Id
                && await db.GuestConfigurations.FirstOrDefaultAsync(c => c.ResourceId == resource.Id, cancellationToken) is { } configuration)
                configuration.ProposedHash = inputs.Hash;
            await engine.AddRunsAsync(db, [run], cancellationToken);
            return run.Id;
        }, cancellationToken);

    /// <summary>
    /// Proposes the onChange action of each guest whose configure inputs have changed, once its
    /// provisioning has caught up. Each input hash gets one automatic run, so a failure waits for
    /// a new commit or a person instead of repeating.
    /// </summary>
    public Task ScheduleConfigurationsAsync(CancellationToken cancellationToken) =>
        engine.ExecuteAsync(async (db, _) =>
        {
            var revision = await RepositoryTrust.EligibleRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
            if (revision is null) return 0;

            var due = await db.GuestConfigurations
                .Where(c => c.DesiredHash != null && c.DesiredHash != c.ProposedHash)
                .Join(db.Resources.Where(r => r.Valid && r.RetiredAt == null && r.MissingSince == null && r.BoundVmid != null
                        && r.SyncedInputsHash == r.DesiredInputsHash
                        && db.Runs.Where(run => run.ResourceId == r.Id && run.Operation == Operations.Provision)
                            .OrderByDescending(run => run.CreatedAt).Select(run => run.Status).FirstOrDefault() == RunStatuses.Succeeded
                        && !db.Runs.Any(run => run.ResourceId == r.Id && run.Status != RunStatuses.Succeeded
                            && run.Status != RunStatuses.Failed && run.Status != RunStatuses.Cancelled)),
                    c => c.ResourceId, r => r.Id, (c, r) => new { Configuration = c, Resource = r })
                .ToListAsync(cancellationToken);
            if (due.Count == 0) return 0;

            var repository = await revisions.GetAsync(revision.Commit, cancellationToken);
            var runs = new List<Run>();
            foreach (var item in due)
            {
                if (repository.Resource(item.Resource.Address)?.Valid != true || repository.Guest(item.Resource.Address) is not { SetupRecipe: not null } guest)
                    continue;
                var inputs = ConfigureInputs.Build(repository, guest);
                item.Configuration.DesiredHash = inputs.Hash;
                item.Configuration.ProposedHash = inputs.Hash;
                if (inputs.Configure.OnChange is { } action)
                    runs.Add(await ConfigureRunAsync(db, repository, guest, item.Resource, revision, action.Id, "revision", cancellationToken));
            }

            await engine.AddRunsAsync(db, runs, cancellationToken);
            return runs.Count;
        }, cancellationToken);

    private async Task<Run> ConfigureRunAsync(OctopushyDb db, LabRepository repository, GuestDefinition guest, Resource resource, DefinitionRevision revision,
        string action, string reason, CancellationToken cancellationToken)
    {
        var configuration = await db.GuestConfigurations.FirstOrDefaultAsync(c => c.ResourceId == resource.Id, cancellationToken);
        var recipe = repository.Recipes[guest.SetupRecipe!];
        var applied = configuration?.AppliedRevisionId is { } id
            ? await db.Revisions.AsNoTracking().Where(r => r.Id == id).Select(r => r.Commit).FirstOrDefaultAsync(cancellationToken)
            : null;
        var changed = applied is null ? null
            : await mirror.ChangedFilesAsync(applied, revision.Commit, [Path.GetDirectoryName(recipe.File)!, recipe.Root, guest.File], cancellationToken);
        return factory.Configure(repository, guest, resource, revision, action, reason, configuration, changed);
    }

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
                // The head may be invalid; scheduled work uses the latest valid snapshot only.
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
