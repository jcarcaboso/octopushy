using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;

namespace Octopushy.Server.Inventory;

/// <summary>Starts runs and observations on request ("plan now", "observe now"), and observes installations on a schedule.</summary>
public sealed partial class Planner(RunEngine engine, RunFactory factory, RevisionCache revisions, RepositoryMirror mirror, WorkflowsMirrors workflowMirrors,
    IOptions<OctopushyOptions> options, TimeProvider clock)
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
            // The latest observation may be newer than the sync that found this guest valid.
            if (await SafetyBoundary.VmidCollisionAsync(db, resource, guest.InstallationAddress, guest.Vmid, cancellationToken) is { } collision)
                throw new RunException(collision);
            var run = factory.Guest(repository, guest, resource, revision, "manual", await GuestAccessCredentials.IssuedAsync(db, resource.Id, cancellationToken));
            // Planned by hand, it is still the run of the work it plans: the desired work already recorded keeps its occurrence,
            // so cancelling it on purpose is not undone by the next sync.
            GuestRemoval.Record(resource, run.InputsHash!, revision.Id, run, sameWork: resource.DesiredInputsHash == run.InputsHash);
            await engine.AddRunsAsync(db, [run], cancellationToken);
            return run.Id;
        }, cancellationToken);

    /// <summary>
    /// Runs one action of a guest's workflow at the latest revision; by default the one that
    /// follows changes. A person's run queues behind what is running. A schedule's does not: it
    /// is refused, so nothing piles up behind a run that is waiting or uncertain.
    /// </summary>
    public Task<Guid> ConfigureGuestAsync(Guid resourceId, string? action, CancellationToken cancellationToken, string reason = "manual", DateTimeOffset? scheduledFor = null) =>
        engine.ExecuteAsync(async (db, _) =>
        {
            var (resource, revision, repository) = await LoadAsync(db, resourceId, ResourceKinds.Guest, cancellationToken);
            var guest = repository.Guest(resource.Address) ?? throw new RunException("The guest is not in the latest revision.");
            if (guest.Setup is null)
                throw new RunException($"This guest's workflow, {guest.Workflow?.Label ?? guest.WorkflowReference.ToString()}, has no setup stage.");
            if (reason == RunReasons.Schedule
                && await db.Runs.AsNoTracking().Where(r => r.ResourceId == resource.Id
                        && r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled)
                    .OrderBy(r => r.CreatedAt).FirstOrDefaultAsync(cancellationToken) is { } active)
                throw new RunException($"Run #{active.Number} is {active.Status}.");
            var lastProvision = await db.Runs.AsNoTracking()
                .Where(r => r.ResourceId == resource.Id && r.Operation == Operations.Provision)
                .OrderByDescending(r => r.CreatedAt).Select(r => r.Status).FirstOrDefaultAsync(cancellationToken);
            if (resource.BoundVmid is null || resource.SyncedInputsHash != resource.DesiredInputsHash || lastProvision != RunStatuses.Succeeded)
                throw new RunException("This guest's provisioning has not caught up with its definition. Finish that before configuring it.");
            // By hand or on a schedule alike: a schedule's refusal is noted on the guest, and nothing starts.
            if (await GuestWork.OwnKeyRefusedAsync(db, resource.Id, guest, cancellationToken) is { } refused)
                throw new RunException(refused);
            var inputs = ConfigureInputs.Build(repository, guest);
            var chosen = action ?? inputs.Configure.OnChange?.Id
                ?? throw new RunException("Choose an action: the workflow's setup stage runs none on change.");
            var run = await ConfigureRunAsync(db, repository, guest, resource, revision, chosen, reason, cancellationToken, scheduledFor);
            // However it is started, a run of the onChange action delivers the current inputs and
            // store versions, so neither is owed another automatic run.
            if (chosen == inputs.Configure.OnChange?.Id
                && await db.GuestConfigurations.FirstOrDefaultAsync(c => c.ResourceId == resource.Id, cancellationToken) is { } configuration)
            {
                configuration.ProposedHash = inputs.Hash;
                await FollowedSecrets.RecordAsync(db, configuration, inputs.FollowedSecrets, cancellationToken);
            }
            await engine.AddRunsAsync(db, [run], cancellationToken);
            return run.Id;
        }, cancellationToken);

    /// <summary>
    /// Starts a staged rotation of a guest's own key. Its first step proves the key in use with a
    /// login, because it adds the next key with that one, so it cannot be how a guest gets its first.
    /// </summary>
    public Task<Guid> RotateAccessAsync(Guid resourceId, CancellationToken cancellationToken) =>
        engine.ExecuteAsync(async (db, services) =>
        {
            var (resource, revision, repository) = await LoadAsync(db, resourceId, ResourceKinds.Guest, cancellationToken);
            var guest = repository.Guest(resource.Address) ?? throw new RunException("The guest is not in the latest revision.");
            if (!ManagedAccess.Wanted(repository, guest, await GuestAccessCredentials.IssuedAsync(db, resource.Id, cancellationToken)))
                throw new RunException("This guest has no key of Octopushy's to rotate.");
            if (await db.Runs.AsNoTracking().Where(r => r.ResourceId == resource.Id
                        && r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled)
                    .FirstOrDefaultAsync(cancellationToken) is { } active)
                throw new RunException($"Run #{active.Number} is {active.Status}.");
            var name = GuestAccessCredentials.Key(resource.Id);
            var current = await db.StoreEntries.AsNoTracking().Where(e => e.Key == name).Select(e => (int?)e.CurrentVersion).FirstOrDefaultAsync(cancellationToken);
            var inUse = current is null ? null
                : await db.GuestAccessKeys.AsNoTracking().FirstOrDefaultAsync(k => k.ResourceId == resource.Id && k.Version == current, cancellationToken);
            if (inUse is null)
                throw new RunException("This guest has no key of Octopushy's yet, so there is nothing to log in with.");

            var candidate = await GuestAccessCredentials.StageAsync(
                db, services.GetRequiredService<VariableStore>(), resource.Id, guest.Id, inUse.Version, clock.GetUtcNow(), cancellationToken);
            // Every version of Octopushy's own that may still be on the guest, and nothing else.
            var remove = await db.GuestAccessKeys.AsNoTracking()
                .Where(k => k.ResourceId == resource.Id && k.RemovedAt == null && k.Version != candidate.Version)
                .OrderBy(k => k.Version).Select(k => k.Version).ToListAsync(cancellationToken);
            var run = factory.RotateAccess(repository, guest, resource, revision, new AccessRotationContext(inUse.Version, candidate.Version, remove));
            await engine.AddRunsAsync(db, [run], cancellationToken);
            return run.Id;
        }, cancellationToken);

    /// <summary>
    /// Proposes the onChange action of each guest whose configure inputs have changed, once its
    /// provisioning has caught up. Each input hash gets one automatic run, so a failure waits for
    /// a new commit or a person instead of repeating. A new active version of a store value the
    /// guest's setup follows is a change too, and gets one run the same way.
    /// </summary>
    public Task ScheduleConfigurationsAsync(CancellationToken cancellationToken) =>
        engine.ExecuteAsync(async (db, _) =>
        {
            // The head, not the newest loaded: a branch rolled back to an earlier commit must not
            // configure the one it left. An invalid head proposes nothing until it is fixed.
            var revision = await RepositoryTrust.HeadRevisions(db).AsNoTracking().Where(r => r.RepositoryValid)
                .OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
            if (revision is null) return 0;

            // A guest whose provisioning has caught up and that nothing holds: a run that is
            // active, waiting or uncertain keeps every automatic run off it.
            var ready = db.Resources.Where(r => r.Valid && r.RetiredAt == null && r.MissingSince == null && r.BoundVmid != null
                && r.SyncedInputsHash == r.DesiredInputsHash
                && db.Runs.Where(run => run.ResourceId == r.Id && run.Operation == Operations.Provision)
                    .OrderByDescending(run => run.CreatedAt).Select(run => run.Status).FirstOrDefault() == RunStatuses.Succeeded
                && !db.Runs.Any(run => run.ResourceId == r.Id && run.Status != RunStatuses.Succeeded
                    && run.Status != RunStatuses.Failed && run.Status != RunStatuses.Cancelled));
            var due = await db.GuestConfigurations
                .Where(c => c.DesiredHash != null && c.DesiredHash != c.ProposedHash)
                .Join(ready, c => c.ResourceId, r => r.Id, (c, r) => new { Configuration = c, Resource = r })
                .ToListAsync(cancellationToken);
            var following = await db.GuestConfigurations
                .Where(c => c.DesiredHash != null && c.DesiredHash == c.ProposedHash)
                .Join(ready, c => c.ResourceId, r => r.Id, (c, r) => new { Configuration = c, Resource = r })
                .ToListAsync(cancellationToken);
            if (due.Count == 0 && following.Count == 0) return 0;

            var repository = await revisions.GetAsync(revision, cancellationToken);
            // Nothing that would log in with Octopushy's key starts where there is no record it reached the machine,
            // as GuestWork.OwnKeyRefusedAsync says. Such a guest stays due, so it is configured once that changes.
            var delivered = await GuestAccessCredentials.DeliveredAmongAsync(db,
                due.Select(i => i.Resource.Id).Concat(following.Select(i => i.Resource.Id)).ToList(), cancellationToken);
            bool Refused(Resource resource, GuestDefinition guest) => !GuestWork.NamesOwnKey(guest) && !delivered.Contains(resource.Id);
            var runs = new List<Run>();
            foreach (var item in due)
            {
                if (repository.Resource(item.Resource.Address)?.Valid != true || repository.Guest(item.Resource.Address) is not { Setup: not null } guest
                    || Refused(item.Resource, guest))
                    continue;
                var inputs = ConfigureInputs.Build(repository, guest);
                item.Configuration.DesiredHash = inputs.Hash;
                item.Configuration.ProposedHash = inputs.Hash;
                await FollowedSecrets.RecordAsync(db, item.Configuration, inputs.FollowedSecrets, cancellationToken);
                if (inputs.Configure.OnChange is { } action)
                    runs.Add(await ConfigureRunAsync(db, repository, guest, item.Resource, revision, action.Id, "revision", cancellationToken));
            }

            // Read without building the inputs, which hashes the workflow's files: this runs every
            // tick, for every guest, and almost always finds nothing.
            var followed = new List<(GuestConfiguration Configuration, Resource Resource, GuestDefinition Guest, IReadOnlyList<string> Keys)>();
            foreach (var item in following)
            {
                // A refused guest's change of a followed value stays owed, as its other changes do.
                if (repository.Resource(item.Resource.Address)?.Valid == true && repository.Guest(item.Resource.Address) is { Setup: not null } guest
                    && !Refused(item.Resource, guest))
                    followed.Add((item.Configuration, item.Resource, guest, ConfigureInputs.FollowedSecretsOf(guest)));
            }

            var active = await FollowedSecrets.UsableAsync(db, followed.SelectMany(f => f.Keys).ToHashSet(StringComparer.Ordinal), cancellationToken);
            foreach (var (configuration, resource, guest, keys) in followed)
            {
                var usable = keys.ToDictionary(k => k, k => active[k], StringComparer.Ordinal);
                if (configuration.SecretVersions is null)
                {
                    // Recorded once, from what is active now: the first record starts no run.
                    configuration.SecretVersions = FollowedSecrets.Serialize(usable);
                    continue;
                }

                if (guest.Setup!.Configure?.OnChange is not { } onChange)
                    continue;
                // What the newest run of the action actually took counts, not what was active when
                // it was created: it pins at its first dispatch, which can follow a promotion. That
                // is read before comparing, so a run that took a newer version than was recorded is
                // owed a run when the recorded one is promoted again. Nothing holds this guest, so
                // that run has finished.
                var recorded = FollowedSecrets.Parse(configuration.SecretVersions);
                if (await FollowedSecrets.PinnedByNewestAsync(db, resource.Id, onChange.Id, cancellationToken) is { } pinned)
                    foreach (var key in keys)
                        if (pinned.TryGetValue(key, out var took))
                            recorded[key] = took;
                var changes = FollowedSecrets.Changes(recorded, usable);
                configuration.SecretVersions = FollowedSecrets.Serialize(FollowedSecrets.Started(recorded, usable));
                if (changes.Count == 0)
                    continue;
                var run = await ConfigureRunAsync(db, repository, guest, resource, revision, onChange.Id, RunReasons.Secret, cancellationToken);
                run.ReasonDetail = FollowedSecrets.Describe(changes);
                runs.Add(run);
            }

            await engine.AddRunsAsync(db, runs, cancellationToken);
            return runs.Count;
        }, cancellationToken);

    /// <summary>
    /// How long after its time a schedule may still start. A schedule says when an action
    /// should run, so a time well past is skipped rather than run at some other hour.
    /// </summary>
    public static readonly TimeSpan ScheduleLateLimit = TimeSpan.FromHours(1);

    /// <summary>
    /// Fires what has fallen due. There is no backlog: only the newest time counts, and each
    /// comes up once whatever happens to it, so a failure waits for the next one.
    /// </summary>
    public async Task FireSchedulesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var due = await engine.ExecuteAsync(async (db, _) =>
        {
            var revision = await RepositoryTrust.HeadRevisions(db).AsNoTracking().Where(r => r.RepositoryValid)
                .OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
            if (revision is null) return [];
            var repository = await revisions.GetAsync(revision, cancellationToken);
            var states = await db.GuestSchedules.ToListAsync(cancellationToken);
            var resources = await db.Resources.AsNoTracking()
                .Where(r => r.Kind == ResourceKinds.Guest && r.Valid && r.RetiredAt == null && r.MissingSince == null)
                .ToListAsync(cancellationToken);
            var fire = new List<(GuestScheduleState State, DateTimeOffset Slot)>();
            var declared = new HashSet<GuestScheduleState>();
            foreach (var resource in resources)
            {
                if (repository.Guest(resource.Address) is not { } guest || !repository.IsValid(resource.Address))
                    continue;
                // Only a guest with a schedule reads the zone: its lab's zone is validated only then.
                var schedules = GuestSchedules.Of(guest);
                if (schedules.Count == 0)
                    continue;
                var zone = GuestSchedules.ZoneOf(repository.Lab(guest.Lab)?.Timezone);
                foreach (var schedule in schedules)
                {
                    var state = states.Find(s => s.ResourceId == resource.Id && s.Action == schedule.Action && s.Cron == schedule.Cron);
                    if (state is null)
                    {
                        // A new schedule starts from now: a time it only just missed is not owed.
                        state = new GuestScheduleState { ResourceId = resource.Id, Action = schedule.Action, Cron = schedule.Cron, LastSlot = now };
                        db.GuestSchedules.Add(state);
                        declared.Add(state);
                        continue;
                    }

                    declared.Add(state);
                    if (GuestSchedules.Next(schedule.Cron, zone, state.LastSlot) is not { } next || next > now)
                        continue;
                    // Recorded before the run starts: a crash between the two loses a firing, not doubles one.
                    if (GuestSchedules.Latest(schedule.Cron, zone, now - ScheduleLateLimit, now) is { } slot)
                    {
                        state.LastSlot = slot;
                        fire.Add((state, slot));
                    }
                    else
                    {
                        state.LastSlot = now;
                        state.Note = $"Missed {next:u}: nothing was running to start it. The next time will.";
                        state.NoteAt = now;
                    }
                }
            }

            // Only guests this revision still knows: an invalid one keeps its place.
            var known = resources.Select(r => r.Id).ToHashSet();
            db.GuestSchedules.RemoveRange(states.Where(s => known.Contains(s.ResourceId) && !declared.Contains(s)));
            await db.SaveChangesAsync(cancellationToken);
            return fire;
        }, cancellationToken);

        foreach (var (state, slot) in due)
        {
            var (run, note) = await StartScheduledAsync(state, slot, cancellationToken);
            await engine.ExecuteAsync(async (db, _) =>
            {
                var current = await db.GuestSchedules.FirstOrDefaultAsync(
                    s => s.ResourceId == state.ResourceId && s.Action == state.Action && s.Cron == state.Cron, cancellationToken);
                if (current is null) return 0;
                current.LastRunId = run ?? current.LastRunId;
                current.Note = note;
                current.NoteAt = note is null ? null : now;
                return await db.SaveChangesAsync(cancellationToken);
            }, cancellationToken);
        }
    }

    private async Task<(Guid? Run, string? Note)> StartScheduledAsync(GuestScheduleState state, DateTimeOffset slot, CancellationToken cancellationToken)
    {
        try
        {
            return (await ConfigureGuestAsync(state.ResourceId, state.Action, cancellationToken, RunReasons.Schedule, slot), null);
        }
        catch (RunException e)
        {
            return (null, $"Skipped: {e.Message}");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // One guest's trouble is not another's: their times are already recorded as dealt with.
            return (null, $"Could not start: {e.GetType().Name}. The next time tries again.");
        }
    }

    /// <summary>
    /// Stops scheduled runs left waiting past their limit, which would otherwise hold the guest.
    /// This is tidying: what keeps a late run off the guest is the check at each release.
    /// </summary>
    public Task ExpireScheduledAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        engine.ExecuteAsync(async (db, _) =>
        {
            var waiting = await db.Runs.Include(r => r.Steps).ThenInclude(s => s.Attempts).AsSplitQuery()
                .Where(r => r.Reason == RunReasons.Schedule
                    && (r.Status == RunStatuses.Queued || r.Status == RunStatuses.Running || r.Status == RunStatuses.Blocked))
                .ToListAsync(cancellationToken);
            foreach (var run in waiting)
                await engine.StopIfLateAsync(db, run, now, cancellationToken);
            return 0;
        }, cancellationToken);

    private async Task<Run> ConfigureRunAsync(OctopushyDb db, LabRepository repository, GuestDefinition guest, Resource resource, DefinitionRevision revision,
        string action, string reason, CancellationToken cancellationToken, DateTimeOffset? scheduledFor = null)
    {
        var configuration = await db.GuestConfigurations.FirstOrDefaultAsync(c => c.ResourceId == resource.Id, cancellationToken);
        var applied = configuration?.AppliedRevisionId is { } id
            ? await db.Revisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            : null;
        var changed = applied is null ? null : await ChangedSinceAsync(applied, revision, guest, cancellationToken);
        return factory.Configure(repository, guest, resource, revision, action, reason, configuration, changed, scheduledFor);
    }

    /// <summary>
    /// What changed under the guest's folder, its workflow's folder and its setup stage's root since <paramref name="applied"/>,
    /// each in the repository it lives in: the guest's folder in the lab repository, between the two config commits, and the
    /// workflow between the commits it resolved to in each revision. A new release with no new config commit shows as what
    /// changed in the workflow.
    /// </summary>
    private async Task<IReadOnlyList<string>> ChangedSinceAsync(DefinitionRevision applied, DefinitionRevision revision, GuestDefinition guest,
        CancellationToken cancellationToken)
    {
        var workflow = guest.Workflow!;
        string[] paths = [workflow.Directory, workflow.Setup!.Root];
        if (workflow.Repository is null && applied.WorkflowsRepository is null)
            return await mirror.ChangedFilesAsync(applied.Commit, revision.Commit, [.. paths, guest.Directory], cancellationToken);

        var changed = (await mirror.ChangedFilesAsync(applied.Commit, revision.Commit, [guest.Directory], cancellationToken)).ToList();
        var resolutions = RevisionCache.Resolutions(applied.Resolutions);
        var before = (resolutions.FirstOrDefault(r => r.Reference == guest.WorkflowReference.ToString() && r.Commit is not null)
            ?? resolutions.FirstOrDefault(r => r.WorkflowId == workflow.Id && r.Commit is not null))?.Commit;
        if (workflow.Repository is { } repository && applied.WorkflowsRepository == repository && before is not null)
        {
            if (before != workflow.Commit)
                changed.AddRange(await workflowMirrors.For(repository).ChangedFilesAsync(before, workflow.Commit!, paths, cancellationToken));
        }
        else
        {
            changed.Add($"M {workflow.Directory} (now {workflow.Label} from {workflow.Repository ?? "this repository"})");
        }

        return changed.Take(200).ToList();
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

            var repository = await revisions.GetAsync(revision, cancellationToken);
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
        var repository = await revisions.GetAsync(revision, cancellationToken);
        return (resource, revision, repository);
    }
}
