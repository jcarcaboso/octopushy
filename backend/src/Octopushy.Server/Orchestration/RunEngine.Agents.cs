using Google.Protobuf;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Agent.V1;
using Octopushy.Definitions;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Store;

namespace Octopushy.Server.Orchestration;

public sealed partial class RunEngine
{
    private readonly Dictionary<Guid, int> _resultFailures = [];

    /// <summary>
    /// Keeps a log batch of the agent's own attempt, while it takes logs (<see cref="TakesLogsAsync"/>). Not under the engine's
    /// gate, so a batch never holds back the agent's heartbeats or other messages. The attempt is looked up again under its
    /// log's own gate, which deletion takes after its rows are gone: a batch either lands before the log is removed or finds
    /// the attempt gone. A batch refused is dropped: logs are never acknowledged, so the agent does not send it again.
    /// </summary>
    public async Task OnLogAsync(Guid agentId, Guid attemptId, long firstSequence,
        IReadOnlyList<(long UnixMs, string Stream, string Text)> lines, CancellationToken ct)
    {
        if (!await TakesLogsAsync(agentId, attemptId, ct))
        {
            logger.LogInformation("Dropped {Count} log line(s) from agent {Agent} for attempt {Attempt}: it is not the agent's, or no longer takes logs.",
                lines.Count, agentId, attemptId);
            return;
        }

        using (await masks.EnsureAsync(attemptId, ct))
            await logs.AppendAsync(attemptId, firstSequence, lines, ct, () => TakesLogsAsync(agentId, attemptId, ct));
    }

    /// <summary>
    /// Whether an attempt of <paramref name="agentId"/> takes log lines: while it is assigned or running; while it is uncertain
    /// and still takes a late result, whose log comes before it; and while the server cancelled it and the agent has not
    /// reported on it yet, however long the agent is away. An agent sends an attempt's lines before its result, and they are
    /// handled before it, so an attempt the agent has reported on has them all.
    /// </summary>
    private async Task<bool> TakesLogsAsync(Guid agentId, Guid attemptId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OctopushyDb>().Attempts.AnyAsync(a => a.Id == attemptId && a.AgentId == agentId
            && (a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running
                || (a.Status == AttemptStatuses.Uncertain && !a.LateResult && !a.OutputsWithheld)
                || (a.Status == AttemptStatuses.Cancelled && a.ResultSha256 == null)), ct);
    }

    /// <summary>
    /// Holds <paramref name="attempt"/>, a change to the guest of agent <paramref name="agentId"/> while that agent is being
    /// updated, until restarting the agent can interrupt nothing: no attempt is assigned to it or running on it, and no change
    /// of it whose lease ran out may still execute (<see cref="UnconfirmedAsync"/>). Under the engine's gate, and dispatch
    /// assigns nothing to an agent being updated, so nothing lands on it between this check and the hand-off. Returns the
    /// agent, tracked, when the change may go; null when it waits. Writes only what changes, so a tick that waits saves nothing.
    /// </summary>
    private async Task<AgentRecord?> UpdateHoldAsync(OctopushyDb db, Guid agentId, Attempt attempt, CancellationToken ct)
    {
        var agent = await db.Agents.SingleAsync(a => a.Id == agentId, ct);
        var busy = await db.Attempts.Include(a => a.Step).ThenInclude(s => s!.Run)
            .Where(a => a.AgentId == agentId && a.Id != attempt.Id && (a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running))
            .OrderBy(a => a.AssignedAt).FirstOrDefaultAsync(ct);
        var unconfirmed = busy is null ? await UnconfirmedAsync(db, agentId, ct) : null;
        var waited = busy ?? unconfirmed;
        agent.UpdateHold = busy is not null ? AgentUpdateHolds.Busy : unconfirmed is not null ? AgentUpdateHolds.UnconfirmedChange : null;
        agent.UpdateHoldAttemptId = waited?.Id;
        if (waited is null)
            return agent;
        var what = waited.Step?.Run is { } run ? $"{waited.Action} of run #{run.Number}" : $"attempt {waited.Id}";
        attempt.WaitingReason = busy is not null
            ? $"Waiting for agent {agent.Name}, which this guest runs, to finish {what}: its update restarts it, and nothing it runs is interrupted."
            : $"Waiting for agent {agent.Name}, which this guest runs, to say that {what} no longer runs: its lease ran out, and the update "
              + "would restart the agent under it. It ends when the agent connects or reports, or cancel this run to give the update up.";
        return null;
    }

    /// <summary>
    /// A change of <paramref name="agentId"/> that may still execute though it is no longer assigned or running: uncertain, its
    /// lease run out or force stopped, with no result taken for it, and not ruled out by the agent. The agent rules it out with a
    /// Hello or heartbeat on its current connection, said after the attempt became uncertain, that does not list it: those list
    /// every attempt it still executes. A result it has not sent yet survives a restart in its outbox, so only execution counts.
    /// </summary>
    private async Task<Attempt?> UnconfirmedAsync(OctopushyDb db, Guid agentId, CancellationToken ct)
    {
        var uncertain = await db.Attempts.Include(a => a.Step).ThenInclude(s => s!.Run)
            .Where(a => a.AgentId == agentId && a.Mutating && a.Status == AttemptStatuses.Uncertain
                        && !a.LateResult && !a.OutputsWithheld && a.ResultSha256 == null)
            .OrderBy(a => a.FinishedAt).ToListAsync(ct);
        var said = connections.Get(agentId)?.LastRunning;
        return uncertain.FirstOrDefault(a => said is null || a.FinishedAt is not { } stopped || said.At <= stopped || said.AttemptIds.Contains(a.Id.ToString()));
    }

    /// <summary>Assigns queued attempts to connected agents that are allowed and have room.</summary>
    public async Task DispatchAsync(CancellationToken cancellationToken)
    {
        await WithDbAsync(async (snapshot, _) =>
        {
            var eligible = RepositoryTrust.EligibleRevisions(snapshot);
            var pending = snapshot.Attempts.Include(a => a.Step).ThenInclude(s => s!.Run)
                .Where(a => a.Status == AttemptStatuses.Queued);
            // An observation has no step or run; it records its own revision and context.
            var queued = await pending
                .Where(a => a.StepId == null
                    ? eligible.Any(r => r.Id == a.RevisionId)
                    : eligible.Any(r => r.Id == a.Step!.Run!.RevisionId && r.Commit == a.Step.Run.Commit))
                .OrderBy(a => a.CreatedAt)
                .Take(200)
                .ToListAsync(cancellationToken);
            // Keep reporting on previous-source work bounded without spending eligible execution slots.
            queued.AddRange(await pending
                .Where(a => a.StepId == null
                    ? !eligible.Any(r => r.Id == a.RevisionId)
                    : !eligible.Any(r => r.Id == a.Step!.Run!.RevisionId && r.Commit == a.Step.Run.Commit))
                .OrderBy(a => a.CreatedAt)
                .Take(200)
                .ToListAsync(cancellationToken));
            var sent = 0;
            if (queued.Count == 0)
            {
                return 0;
            }

            var active = await snapshot.Attempts.AsNoTracking()
                .Where(a => a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running)
                .Select(a => new { a.AgentId, a.HostKey })
                .ToListAsync(cancellationToken);
            var perAgent = active.Where(a => a.AgentId is not null).GroupBy(a => a.AgentId!.Value).ToDictionary(g => g.Key, g => g.Count());
            var perHost = active.Where(a => a.HostKey is not null).GroupBy(a => a.HostKey!).ToDictionary(g => g.Key, g => g.Count());
            var connectedIds = connections.All.Select(s => s.AgentId).ToList();
            // An agent being updated takes nothing, whatever its pause says: a person may lift that during the update, and work
            // handed to it after the hold let its restart go would be interrupted.
            var agents = await snapshot.Agents.AsNoTracking()
                .Where(a => !a.Disabled && !a.Draining && a.UpdateState != AgentUpdateStates.Updating && connectedIds.Contains(a.Id))
                .ToListAsync(cancellationToken);
            // The guests of agents being updated, recorded when the update is claimed, before its change is committed: a change
            // to one may restart its agent. See UpdateHoldAsync.
            var updatingGuests = await snapshot.Agents.AsNoTracking()
                .Where(a => a.UpdateState == AgentUpdateStates.Updating && a.UpdateResourceId != null)
                .Select(a => new { AgentId = a.Id, ResourceId = a.UpdateResourceId })
                .ToListAsync(cancellationToken);
            // Guests already handed work during this pass.
            var changing = new HashSet<Guid>();
            var observedOnly = await SafetyBoundary.ObservedOnlyInstallationsAsync(snapshot, cancellationToken);

            foreach (var queuedAttempt in queued)
            {
                var handedOff = false;
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
                    var builder = scope.ServiceProvider.GetRequiredService<AssignmentBuilder>();
                    var attempt = await db.Attempts.Include(a => a.Step).ThenInclude(step => step!.Run)
                        .SingleAsync(a => a.Id == queuedAttempt.Id, cancellationToken);
                    if (attempt.Status != AttemptStatuses.Queued) continue;
                    (AgentSession Session, ServerMessage Message, Guid AttemptId)? prepared = null;
                    async Task PrepareAsync()
                    {
                        if (attempt is { Mutating: true, Step.Run.Reason: RunReasons.Schedule }
                            && await StopIfLateAsync(db, await LoadRunAsync(db, attempt.Step.RunId, cancellationToken), clock.GetUtcNow(), cancellationToken))
                            return;
                        // A build of a template's image: no run, no installation, no secrets. See RunEngine.Images.
                        if (Images.TemplateImages.IsBuild(attempt))
                        {
                            if (await DispatchImageBuildAsync(db, attempt, agents, perAgent, builder, cancellationToken) is { } build)
                            {
                                logs.RegisterSecrets(attempt.Id, []);
                                prepared = (build.Session, build.Message, attempt.Id);
                            }
                            return;
                        }

                        var step = attempt.Step;
                        var run = step?.Run;
                        var context = RunContext.Parse(run?.Context ?? attempt.Context!);
                        // Before anything that would make it wait: work in an installation Octopushy only observes fails.
                        if (run is not null && await SafetyBoundary.ObservedOnlyRefusalAsync(db, run, context, cancellationToken, observedOnly) is { } refused)
                        {
                            attempt.Status = AttemptStatuses.Failed;
                            attempt.Error = refused;
                            attempt.FinishedAt = clock.GetUtcNow();
                            attempt.WaitingReason = null;
                            step!.Status = StepStatuses.Failed;
                            step.Detail = refused;
                            signal.Wake();
                            return;
                        }

                        if (await (run is null
                                ? RepositoryTrust.BlockAsync(db, attempt.RevisionId, context.Commit, context, cancellationToken)
                                : RepositoryTrust.BlockAsync(db, run, cancellationToken)) is { } block)
                        {
                            attempt.WaitingReason = block.Detail;
                            if (run is not null)
                            {
                                run.Status = RunStatuses.Blocked;
                                run.Detail = block.Detail;
                                run.BlockReason = block.Reason;
                            }
                            return;
                        }
                        // Asked again here, not only when the attempt was queued: others may have started since, and a
                        // service's guest may have stopped being ready for it.
                        var family = run is not null && attempt.Mutating ? await GuestWork.FamilyAsync(db, run.ResourceId, cancellationToken) : [];
                        if (family.Count > 1 && family.Any(changing.Contains))
                        {
                            attempt.WaitingReason = "Waiting for another run that is changing the same guest.";
                            return;
                        }

                        if (run is not null && attempt.Mutating && await ChangeHeldAsync(db, run, context, dispatching: true, cancellationToken) is { } held)
                        {
                            if (context.Kind == RunKinds.Pipeline)
                                TakeBack(attempt, step!, run, held);
                            else
                                attempt.WaitingReason = held;
                            return;
                        }

                        AgentRecord? restarting = null;
                        if (run is not null && attempt.Mutating && updatingGuests.FirstOrDefault(u => u.ResourceId == run.ResourceId) is { } updating)
                        {
                            restarting = await UpdateHoldAsync(db, updating.AgentId, attempt, cancellationToken);
                            if (restarting is null)
                                return;
                        }

                        var resourceId = run?.ResourceId ?? attempt.ResourceId;
                        var needsBuiltInState = attempt.Executor == Executors.OpenTofu;
                        var candidates = agents
                            .Where(a => connections.Get(a.Id) is { } s && s.Executors.Contains(attempt.Executor)
                                        && attempt.RequiredLabels.All(a.Labels.Contains)
                                        && (!needsBuiltInState || s.Capabilities.Contains(TofuStateStore.AgentCapability)))
                            .ToList();
                        string? waiting = null;
                        // Only agents with fresh evidence of every connection the step makes; the rest are asked, never waited for.
                        // Labels, executors and capabilities above, and capacity, preference and every check of the assignment
                        // below, still apply to those that remain.
                        var reachable = candidates.Count == 0 ? default : await ReachableAsync(db, step, context, candidates, attempt.RequiredLabels, cancellationToken);
                        if (candidates.Count == 0)
                        {
                            var labelled = agents.Where(a => attempt.RequiredLabels.All(a.Labels.Contains))
                                .Select(a => connections.Get(a.Id)).OfType<AgentSession>().ToList();
                            // The agents that lack the executor, with each one's reason; one that lacks only built-in state
                            // keeps the wording below.
                            var lacking = AgentAvailability.Lacking(attempt.Executor, labelled);
                            waiting = labelled.Count > 0
                                ? needsBuiltInState
                                    ? $"Waiting for a connected agent advertising executor {attempt.Executor} with the required labels and built-in OpenTofu state; update the agent if it is older.{lacking}"
                                    : $"Waiting for a connected agent advertising executor {attempt.Executor} with the required labels.{lacking}"
                                : attempt.RequiredLabels.Length == 0
                                    ? "Waiting for a connected agent."
                                    : $"Waiting for a connected agent with the labels {string.Join(", ", attempt.RequiredLabels)}.";
                        }
                        else if ((candidates = reachable.Agents).Count == 0)
                        {
                            waiting = reachable.Waiting;
                        }
                        else if (attempt.HostKey is { } hostKey && attempt.HostLimit is { } limit && perHost.GetValueOrDefault(hostKey) >= limit)
                        {
                            waiting = $"Waiting for capacity on host {hostKey} (at most {limit} at once).";
                        }

                        var free = candidates.Where(a => perAgent.GetValueOrDefault(a.Id) < a.MaxConcurrency).ToList();
                        if (waiting is null && free.Count == 0)
                        {
                            waiting = "Waiting for agent capacity.";
                        }

                        if (waiting is not null)
                        {
                            attempt.WaitingReason = waiting;
                            return;
                        }

                        var agent = free.FirstOrDefault(a => a.Id == attempt.PreferredAgentId)
                                    ?? free.MinBy(a => perAgent.GetValueOrDefault(a.Id))!;
                        // Only through the connection whose evidence was checked: one that replaced it since has none. A replaced
                        // connection is closed, so the hand-off below requeues the attempt.
                        var session = reachable.Sessions is { } checkedOn ? checkedOn[agent.Id] : connections.Get(agent.Id);
                        if (session is null)
                        {
                            attempt.WaitingReason = "The agent disconnected before assignment.";
                            return;
                        }
                        Assignment assignment;
                        try
                        {
                            assignment = await builder.BuildAsync(attempt, step, run, context, agent.Id, cancellationToken);
                        }
                        catch (Exception e) when (e is CredentialUnavailableException or ExecutionUntrustedException && run is not null)
                        {
                            attempt.WaitingReason = e.Message;
                            run.Status = RunStatuses.Blocked;
                            run.Detail = e.Message;
                            run.BlockReason = (e as ExecutionUntrustedException)?.BlockReason;
                            return;
                        }
                        catch (Exception e) when (e is ExecutionUntrustedException or StateBusyException)
                        {
                            attempt.WaitingReason = e.Message;
                            return;
                        }
                        // An observation is not replanned: unavailable credentials are its error until the next one.
                        catch (Exception e) when (e is AssignmentException or CredentialUnavailableException or IOException or Git.GitException)
                        {
                            logger.LogWarning("Attempt {Attempt} cannot be assigned: {Error}", attempt.Id, e.Message);
                            attempt.Status = AttemptStatuses.Failed;
                            attempt.Error = e.Message;
                            attempt.FinishedAt = clock.GetUtcNow();
                            if (step is null)
                            {
                                await FinishObservationAsync(db, attempt, null, cancellationToken,
                                    e is CredentialUnavailableException ? ObserveBlockReasons.Credentials : null);
                            }
                            else
                            {
                                step.Status = StepStatuses.Failed;
                                step.Detail = e.Message;
                            }

                            signal.Wake();
                            return;
                        }

                        // Building an assignment takes time, so the limit is decided at the time this one records.
                        var now = clock.GetUtcNow();
                        if (attempt.Mutating && run is not null && await StopIfLateAsync(db, run, now, cancellationToken))
                            return;

                        attempt.Status = AttemptStatuses.Assigned;
                        attempt.AgentId = agent.Id;
                        attempt.AssignedAt = now;
                        attempt.LeaseExpiresAt = now.AddSeconds(options.Value.AgentChannel.LeaseSeconds);
                        attempt.WaitingReason = null;
                        if (step is not null)
                        {
                            step.Status = StepStatuses.Running;
                            step.StartedAt ??= now;
                        }

                        // The agent's deadline to come back counts from the last step that may restart it.
                        if (restarting is not null)
                            restarting.UpdateReleasedAt = now;
                        changing.UnionWith(family);
                        perAgent[agent.Id] = perAgent.GetValueOrDefault(agent.Id) + 1;
                        if (attempt.HostKey is { } key)
                        {
                            perHost[key] = perHost.GetValueOrDefault(key) + 1;
                        }

                        logs.RegisterSecrets(attempt.Id, assignment.Secrets.Values);
                        prepared = (session, new ServerMessage { Assignment = assignment }, attempt.Id);
                    }
                    await PrepareAsync();
                    // Secrets are recorded as released, and a reservation changed, in the save that assigns the attempt: never
                    // before it, and never for an attempt that is not handed off.
                    var reservation = ReservationChanged(db);
                    if (prepared is null)
                    {
                        DropReleases(db);
                        if (reservation is { } unchanged)
                            Restore(db.ChangeTracker.Entries<Resource>().Single(e => e.Entity.Id == unchanged.ResourceId).Entity, unchanged.Before);
                    }
                    await db.SaveChangesAsync(cancellationToken);
                    // Hand off each saved assignment before preparing the next, under the same gate as revocation.
                    if (prepared is { } ready)
                    {
                        var handOff = new HandOff(ready.Session, ++_handOffNumber,
                            db.ChangeTracker.Entries<SecretResolution>().Where(e => e.Entity.AttemptId == attempt.Id).Select(e => e.Entity.Id).ToArray(),
                            reservation);
                        if (ready.Session.TrySend(ready.Message))
                        {
                            handedOff = true;
                            sent++;
                            _handOffs[(attempt.Id, ready.Session.Id)] = handOff;
                        }
                        else
                        {
                            await UndoHandOffAsync(db, attempt, handOff, cancellationToken);
                            await db.SaveChangesAsync(cancellationToken);
                        }
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    if (handedOff)
                    {
                        logger.LogError(e, "Dispatch cleanup failed after attempt {Attempt} was sent; its result or lease still decides it.", queuedAttempt.Id);
                        continue;
                    }
                    try
                    {
                        await FailUnsentAttemptAsync(queuedAttempt.Id, e, cancellationToken);
                    }
                    catch (Exception recording) when (recording is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        logger.LogError(recording, "Recording dispatch failure for attempt {Attempt} failed.", queuedAttempt.Id);
                    }
                }
            }
            return sent;
        }, cancellationToken);
    }

    /// <summary>What a guest's reservation held, field by field.</summary>
    private sealed record ReservationValues(string? Installation, string? Node, int? Vmid, DateTimeOffset? At, DateTimeOffset? AppliedAt)
    {
        public static ReservationValues Of(Resource r) => new(r.ReservedInstallation, r.ReservedNode, r.ReservedVmid, r.ReservedAt, r.ReservationAppliedAt);

        /// <summary>Whether these are <paramref name="other"/> as the database keeps them: times to the microsecond.</summary>
        public bool Matches(ReservationValues other) =>
            this with { At = Micro(At), AppliedAt = Micro(AppliedAt) } == other with { At = Micro(other.At), AppliedAt = Micro(other.AppliedAt) };

        private static DateTimeOffset? Micro(DateTimeOffset? time) => time?.AddTicks(-(time.Value.Ticks % 10));
    }

    /// <summary>A reservation a hand-off changed, from what to what.</summary>
    private sealed record ReservationChange(Guid ResourceId, ReservationValues Before, ReservationValues After);

    /// <summary>
    /// An assignment put on a session's queue, with what was recorded for it: the secrets released and the reservation made.
    /// The session is the one it was handed to: only that session can say it never wrote it.
    /// </summary>
    private sealed record HandOff(AgentSession Session, long Number, long[] Releases, ReservationChange? Reservation)
    {
        /// <summary>Its session closed and said it never wrote it. What it recorded is still to be taken back.</summary>
        public bool NeverWritten { get; set; }
    }

    // Protected by the gate. In memory only: after a restart nothing is put back, and a lost assignment is decided by its lease.
    // By attempt and session: a read its agent's next Hello queued again can be handed off anew before the session that never
    // wrote it has closed, and that session's record must still be there to take back what it recorded.
    // Nothing an agent says removes a record: one goes when its session says it never wrote it and what it recorded has been
    // taken back, or once it is of no more use (PruneHandOffsAsync). So while an attempt is assigned and not started, the
    // hand-off it is assigned by has its record, and that is the one with the highest number.
    // A session is handed an attempt at most once: only a Hello queues an assigned attempt again, and it does so in the step
    // that closes the agent's earlier session (RegisterAsync).
    private readonly Dictionary<(Guid Attempt, Guid Session), HandOff> _handOffs = [];
    private long _handOffNumber;
    private int _handOffsAfterPrune;

    /// <summary>
    /// How many hand-off records may be added before those of no more use are looked for again: one query for that many
    /// hand-offs. Zero in tests that look every pass.
    /// </summary>
    internal int HandOffsBetweenPrunes { get; set; } = 64;

    /// <summary>Whether a hand-off record of an attempt is kept, for tests.</summary>
    internal bool KeepsHandOff(Guid attemptId) => _handOffs.Keys.Any(k => k.Attempt == attemptId);

    /// <summary>
    /// Drops the hand-off records of no more use, so a connection that stays for weeks does not keep one for every attempt
    /// it ever took. A record is of use for two things. While its attempt is saved as assigned and not started, it shows that
    /// attempt was handed to its session, which keeps an earlier session's unwritten hand-off of the same attempt from putting
    /// it back. And while its own write has not begun, it holds what to take back if it never does. Neither is read from
    /// anything but the database and this server's own writer.
    /// </summary>
    private async Task PruneHandOffsAsync(OctopushyDb db, CancellationToken cancellationToken)
    {
        if (_handOffs.Count == 0 || _handOffs.Count < _handOffsAfterPrune + HandOffsBetweenPrunes)
            return;
        var attempts = _handOffs.Keys.Select(k => k.Attempt).Distinct().ToList();
        var unstarted = (await db.Attempts
            .Where(a => attempts.Contains(a.Id) && a.Status == AttemptStatuses.Assigned && a.StartedAt == null)
            .Select(a => a.Id).ToListAsync(cancellationToken)).ToHashSet();
        foreach (var (key, handOff) in _handOffs.ToList())
        {
            if (!unstarted.Contains(key.Attempt) && handOff.Session.WriteBegan(key.Attempt))
                _handOffs.Remove(key);
        }
        _handOffsAfterPrune = _handOffs.Count;
    }

    /// <summary>The reservation building an assignment changed, if it did.</summary>
    private static ReservationChange? ReservationChanged(OctopushyDb db) =>
        db.ChangeTracker.Entries<Resource>()
            .Where(e => e.State == EntityState.Modified)
            .Select(e => new ReservationChange(e.Entity.Id, new ReservationValues(
                    e.OriginalValues.GetValue<string?>(nameof(Resource.ReservedInstallation)), e.OriginalValues.GetValue<string?>(nameof(Resource.ReservedNode)),
                    e.OriginalValues.GetValue<int?>(nameof(Resource.ReservedVmid)), e.OriginalValues.GetValue<DateTimeOffset?>(nameof(Resource.ReservedAt)),
                    e.OriginalValues.GetValue<DateTimeOffset?>(nameof(Resource.ReservationAppliedAt))),
                ReservationValues.Of(e.Entity)))
            .FirstOrDefault(c => c.Before != c.After);

    private static void Restore(Resource resource, ReservationValues values) =>
        (resource.ReservedInstallation, resource.ReservedNode, resource.ReservedVmid, resource.ReservedAt, resource.ReservationAppliedAt) =
        (values.Installation, values.Node, values.Vmid, values.At, values.AppliedAt);

    /// <summary>Forgets the releases recorded while building an assignment that is not handed off.</summary>
    private static void DropReleases(OctopushyDb db)
    {
        foreach (var release in db.ChangeTracker.Entries<SecretResolution>().Where(e => e.State == EntityState.Added).ToList())
            release.State = EntityState.Detached;
    }

    /// <summary>
    /// Puts an attempt whose assignment certainly never left this process back in the queue, as if it had not been handed off:
    /// the secrets it recorded as released and the reservation it made are taken back, the reservation only while it is still
    /// what the hand-off made it. The caller saves.
    /// </summary>
    private static async Task UndoHandOffAsync(OctopushyDb db, Attempt attempt, HandOff handOff, CancellationToken cancellationToken)
    {
        db.SecretResolutions.RemoveRange(await db.SecretResolutions.Where(r => handOff.Releases.Contains(r.Id)).ToListAsync(cancellationToken));
        if (handOff.Reservation is { } change
            && await db.Resources.FindAsync([change.ResourceId], cancellationToken) is { } guest
            && ReservationValues.Of(guest).Matches(change.After))
            Restore(guest, change.Before);
        Requeue(attempt, "The agent channel is full or disconnected; waiting to reassign.");
        if (attempt.Step is { } step) step.Status = StepStatuses.Queued;
    }

    /// <summary>
    /// A session closed, with these assignments never written: none of it left this process, so the agent never had them. Each
    /// that is still assigned to the agent through that very hand-off, and never started, goes back in the queue as when the
    /// hand-off itself fails (<see cref="UndoHandOffAsync"/>). That is not a retry: nothing was sent. An assignment whose write
    /// began is never reported here, and is decided by its result or its lease. See docs/safety-boundary.md.
    /// </summary>
    public Task OnSessionClosedAsync(Guid agentId, Guid sessionId, IReadOnlyList<Guid> attemptIds, CancellationToken cancellationToken) =>
        WithDbAsync(async db =>
        {
            // Noted first, in memory: if taking back fails below, a later pass does it (TakeBackNeverWrittenAgainAsync).
            foreach (var attemptId in attemptIds)
            {
                if (_handOffs.TryGetValue((attemptId, sessionId), out var handOff) && handOff.Session.AgentId == agentId)
                    handOff.NeverWritten = true;
            }
            await TakeBackNeverWrittenAsync(db, cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// Takes back what every hand-off noted as never written recorded, in one save, and retires their records only once that
    /// save succeeded. Safe to run again after a failure, whatever the failed save did: rows already gone are not found, an
    /// attempt already queued is left, and a reservation is restored only while it is still what the hand-off made it.
    /// </summary>
    private async Task TakeBackNeverWrittenAsync(OctopushyDb db, CancellationToken cancellationToken)
    {
        var neverWritten = _handOffs.Where(h => h.Value.NeverWritten).OrderBy(h => h.Value.Number).ToList();
        if (neverWritten.Count == 0)
            return;
        var requeued = 0;
        foreach (var (key, handOff) in neverWritten)
        {
            // Never written: the secrets this hand-off recorded as released were not, whatever became of the attempt since.
            // A read may already be queued again by its agent's next Hello, or handed off anew.
            db.SecretResolutions.RemoveRange(await db.SecretResolutions.Where(r => handOff.Releases.Contains(r.Id)).ToListAsync(cancellationToken));
            // The attempt itself goes back only while this hand-off is still the one it is assigned by. A later record says
            // it was handed off again since, and that one may have been written, or goes back itself.
            if (_handOffs.Any(other => other.Key.Attempt == key.Attempt && other.Value.Number > handOff.Number))
                continue;
            var agentId = handOff.Session.AgentId;
            var attempt = await db.Attempts.Include(a => a.Step)
                .FirstOrDefaultAsync(a => a.Id == key.Attempt && a.AgentId == agentId, cancellationToken);
            if (attempt is not { Status: AttemptStatuses.Assigned, StartedAt: null })
                continue;
            await UndoHandOffAsync(db, attempt, handOff, cancellationToken);
            logger.LogInformation("The assignment of attempt {Attempt} was never written to agent {Agent}; it is queued again.", key.Attempt, agentId);
            requeued++;
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var (key, _) in neverWritten)
            _handOffs.Remove(key);
        if (requeued > 0)
            signal.Wake();
    }

    /// <summary>
    /// Tries again what a closed session's taking back could not save, each pass until it is saved. With its own context, so
    /// that a failure here leaves nothing behind in the pass's.
    /// </summary>
    private async Task TakeBackNeverWrittenAgainAsync(CancellationToken cancellationToken)
    {
        if (!_handOffs.Values.Any(h => h.NeverWritten))
            return;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await TakeBackNeverWrittenAsync(scope.ServiceProvider.GetRequiredService<OctopushyDb>(), cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(e, "Taking back what was recorded for assignments never written failed again; the next pass tries.");
        }
    }

    private async Task FailUnsentAttemptAsync(Guid attemptId, Exception error, CancellationToken cancellationToken)
    {
        // Do not save the failed assignment builder's tracked entities.
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
        var attempt = await db.Attempts.Include(a => a.Step).SingleAsync(a => a.Id == attemptId, cancellationToken);
        using var held = await masks.EnsureAsync(attemptId, cancellationToken);
        attempt.Status = AttemptStatuses.Failed;
        attempt.Error = logs.MaskDiagnostic(attemptId, error.Message);
        attempt.WaitingReason = null;
        attempt.LeaseExpiresAt = null;
        attempt.FinishedAt = clock.GetUtcNow();
        if (attempt.Step is { } step)
        {
            step.Status = StepStatuses.Failed;
            step.Detail = attempt.Error;
        }
        else if (Images.TemplateImages.IsBuild(attempt))
            await FailImageRecordingAsync(db, attempt, cancellationToken);
        else
            await FinishObservationAsync(db, attempt, null, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogError("Attempt {Attempt} could not be dispatched: {Error}", attemptId, attempt.Error);
        signal.Wake();
    }

    /// <summary>After repeated recording failures, stop replaying a poison result without ever retrying a change.</summary>
    public Task<bool> OnResultRecordingFailedAsync(Guid agentId, AttemptResult result, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(result.AttemptId, out var attemptId)) return Task.FromResult(true);
        var received = ArtifactStore.Sha256(result.ToByteArray());
        return WithDbAsync(async (db, _) =>
        {
            var attempt = await db.Attempts.Include(a => a.Step).ThenInclude(s => s!.Run)
                .FirstOrDefaultAsync(a => a.Id == attemptId && a.AgentId == agentId, cancellationToken);
            if (attempt is null) return true;
            if (attempt.ResultSha256 == received
                || AttemptStatuses.IsTerminal(attempt.Status) && attempt.Status != AttemptStatuses.Uncertain)
            {
                _resultFailures.Remove(attemptId);
                return true;
            }
            _resultFailures[attemptId] = _resultFailures.GetValueOrDefault(attemptId) + 1;
            if (_resultFailures[attemptId] < 3) return false;

            const string detail = "The result could not be recorded after three attempts. Inspect the work before planning again.";
            attempt.Status = attempt.Mutating ? AttemptStatuses.Uncertain : AttemptStatuses.Failed;
            attempt.Error = detail;
            // Remember the discarded result too: a lost acknowledgement must not consume a later, different result.
            attempt.ResultSha256 = received;
            attempt.Outputs = null;
            attempt.LeaseExpiresAt = null;
            attempt.FinishedAt = clock.GetUtcNow();
            if (attempt.Step is { } step)
            {
                var current = await db.Attempts.Where(a => a.StepId == step.Id).OrderByDescending(a => a.Number).FirstAsync(cancellationToken);
                if (current.Id == attempt.Id && !RunStatuses.IsTerminal(step.Run!.Status)
                    && step.Status is StepStatuses.Running or StepStatuses.Queued or StepStatuses.Uncertain)
                {
                    step.Status = attempt.Mutating ? StepStatuses.Uncertain : StepStatuses.Failed;
                    step.Detail = detail;
                    step.FinishedAt = clock.GetUtcNow();
                    step.Run.Detail = detail;
                    if (attempt.Mutating) step.Run.Status = RunStatuses.Uncertain;
                }
            }
            else if (Images.TemplateImages.IsBuild(attempt))
                await FailImageRecordingAsync(db, attempt, cancellationToken);
            else
                await FinishObservationAsync(db, attempt, null, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            _resultFailures.Remove(attemptId);
            signal.Wake();
            return true;
        }, cancellationToken);
    }

    private async Task FailImageRecordingAsync(OctopushyDb db, Attempt attempt, CancellationToken cancellationToken)
    {
        images.Release(attempt.Id);
        if (await db.ImageBuilds.FirstOrDefaultAsync(b => b.AttemptId == attempt.Id, cancellationToken) is { } build)
        {
            build.Status = ImageBuildStatuses.Failed;
            build.Error = attempt.Error;
        }
    }

    /// <summary>
    /// The candidates that may get the attempt as far as its connections go, and what the rest wait for. A step whose
    /// connections are not modeled, or that makes none, keeps them all. Evidence only chooses between agents: the one agent
    /// that could ever take the work (<see cref="OnlyAgentAsync"/>) gets it without, unless it runs on the guest the step
    /// configures.
    /// </summary>
    private async Task<(List<AgentRecord> Agents, IReadOnlyDictionary<Guid, AgentSession>? Sessions, string Waiting)> ReachableAsync(
        OctopushyDb db, Step? step, RunContext context, List<AgentRecord> candidates, string[] requiredLabels, CancellationToken cancellationToken)
    {
        var needs = await NetworkRequirements.ForAsync(db, step?.Name ?? StepNames.Observe, context.ForStep(step), cancellationToken);
        if (needs.Targets.Count == 0)
            return (candidates, null, "");
        if (candidates is [var lone] && connections.Get(lone.Id) is { } alone
            && !needs.Targets.Any(t => NetworkRequirements.RunsOn(t, alone.RemoteAddress))
            && await OnlyAgentAsync(db, lone.Id, requiredLabels, cancellationToken))
            return (candidates, new Dictionary<Guid, AgentSession> { [lone.Id] = alone }, "");
        var able = new List<AgentRecord>();
        var sessions = new Dictionary<Guid, AgentSession>();
        var reasons = new List<string>();
        foreach (var candidate in candidates)
        {
            if (connections.Get(candidate.Id) is not { } session)
                continue;
            if (needs.Targets.FirstOrDefault(t => NetworkRequirements.RunsOn(t, session.RemoteAddress)) is { } own)
            {
                reasons.Add($"{candidate.Name}: runs on {own.Endpoint}, the guest this step configures, and would wait for itself if the step restarted it");
                continue;
            }
            var verdict = await reachability.CheckAsync(session, needs.Targets, cancellationToken);
            if (verdict.Ready)
            {
                able.Add(candidate);
                sessions[candidate.Id] = session;
            }
            else
                reasons.Add($"{candidate.Name}: {verdict.Reason}");
        }

        var waiting = $"Waiting for an agent with fresh evidence that it reaches {string.Join(" and ", needs.Targets.Select(t => t.Label))}. "
                      + string.Join("; ", reasons) + ".";
        return (able, sessions, waiting.Length <= 2000 ? waiting : waiting[..1999] + "…");
    }

    /// <summary>
    /// Whether <paramref name="agentId"/> is the only enrolled agent, connected or not, that is not disabled and has every label
    /// the work requires: the only one that could take it while it waits, whatever executors it offers now. Waiting for its
    /// evidence then cannot change which agent gets the work.
    /// </summary>
    private static async Task<bool> OnlyAgentAsync(OctopushyDb db, Guid agentId, string[] requiredLabels, CancellationToken cancellationToken)
    {
        var enrolled = await db.Agents.AsNoTracking().Where(a => !a.Disabled).Select(a => new { a.Id, a.Labels }).ToListAsync(cancellationToken);
        return enrolled.Where(a => requiredLabels.All(a.Labels.Contains)).Select(a => a.Id).ToList() is [var only] && only == agentId;
    }

    public Task OnHelloAsync(Guid agentId, Hello hello, CancellationToken cancellationToken) =>
        WithDbAsync(async db =>
        {
            await ReconcileHelloAsync(db, agentId, hello, cancellationToken);
            signal.Wake();
        }, cancellationToken);

    /// <summary>What a Hello settles about the attempts its agent holds: leases renewed, and reads it does not have queued again.</summary>
    private async Task ReconcileHelloAsync(OctopushyDb db, Guid agentId, Hello hello, CancellationToken cancellationToken)
    {
        var running = hello.RunningAttemptIds.Select(Guid.Parse).ToHashSet();
        var mine = await db.Attempts
            .Where(a => a.AgentId == agentId && (a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running))
            .ToListAsync(cancellationToken);
        foreach (var attempt in mine)
        {
            if (running.Contains(attempt.Id))
            {
                attempt.LeaseExpiresAt = clock.GetUtcNow().AddSeconds(options.Value.AgentChannel.LeaseSeconds);
            }
            else if (attempt.Status == AttemptStatuses.Assigned && !attempt.Mutating)
            {
                // The agent reconnected without it. A read-only action is safe to assign again;
                // a change may have run with its start lost, so it waits for its result or its lease.
                Requeue(attempt, "The agent reconnected without this attempt; it will be reassigned.");
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>How often a changing offer is logged, woken for and stored, per connection: the engine ticks this often anyway.</summary>
    public static readonly TimeSpan OfferEffectsEvery = TimeSpan.FromSeconds(5);

    /// <summary>
    /// What an agent offers after it probed again. Applied under the engine's gate, which dispatch holds from reading an agent's
    /// executors to handing it work, so work for an executor is never handed over after its removal was applied; an attempt
    /// already running is not touched. The session always takes the latest offer at once. Logging it, waking the engine and
    /// storing it on the agent's record happen at most once per <see cref="OfferEffectsEvery"/> per connection, so an agent
    /// that flaps costs no more; an offer not stored then is stored by <see cref="FlushOfferAsync"/>, which the next heartbeat
    /// after the interval queues, or by the next change after it.
    /// </summary>
    public Task OnExecutorsChangedAsync(AgentSession session, ExecutorsChanged changed, CancellationToken cancellationToken) =>
        WithDbAsync(async db =>
        {
            if (session.SetExecutors(AgentAvailability.Executors(changed.Executors), AgentAvailability.Unavailable(changed.Unavailable)))
                session.OfferUnsaved = true;
            await ApplyOfferEffectsAsync(db, session, cancellationToken);
        }, cancellationToken);

    /// <summary>Stores an offer that a change left unsaved, once its interval is over.</summary>
    public Task FlushOfferAsync(AgentSession session, CancellationToken cancellationToken) =>
        WithDbAsync(db => ApplyOfferEffectsAsync(db, session, cancellationToken), cancellationToken);

    /// <summary>
    /// Makes <paramref name="session"/> its agent's connection, as one step under the engine's gate: what its Hello settles
    /// about the attempts the agent holds and what it offers are stored together, then <paramref name="register"/>, with
    /// nothing in between that can be cancelled. Nothing is dispatched between a read being queued again and the earlier
    /// connection closing, so that read cannot be handed to the earlier connection a second time. An older connection of
    /// the agent may have stored its own offer since this one's Hello did; after this the record is this connection's, and
    /// the older one stores nothing more (see <see cref="ApplyOfferEffectsAsync"/>). If a write fails or is cancelled,
    /// neither is stored and the connection is not registered.
    /// </summary>
    public Task RegisterAsync(AgentSession session, Hello hello, Action register, CancellationToken cancellationToken) =>
        WithDbAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await ReconcileHelloAsync(db, session.AgentId, hello, cancellationToken);
            await WriteOfferAsync(db, session, cancellationToken);
            try
            {
                await transaction.CommitAsync(CancellationToken.None);
            }
            catch
            {
                // Whether it was committed is not known. If it was, reads are queued again while the earlier connection is
                // still the agent's: it is closed here, under the gate, so none is handed to it a second time.
                connections.Disconnect(session.AgentId);
                throw;
            }
            register();
            signal.Wake();
        }, cancellationToken);

    private static Task WriteOfferAsync(OctopushyDb db, AgentSession session, CancellationToken cancellationToken)
    {
        // What the session holds now: the latest offer, whichever report brought it.
        var executors = session.Executors.ToArray();
        var reasons = AgentAvailability.Serialize(session.Unavailable);
        return db.Agents.Where(a => a.Id == session.AgentId)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.Executors, executors).SetProperty(a => a.Unavailable, reasons), cancellationToken);
    }

    private async Task ApplyOfferEffectsAsync(OctopushyDb db, AgentSession session, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (!session.OfferUnsaved || now < session.OfferEffectsAfter)
            return;
        // A connection a newer one of the same agent replaced stores nothing: the record is the newer one's now.
        if (connections.Get(session.AgentId) != session)
            return;
        session.OfferEffectsAfter = now + OfferEffectsEvery;
        var executors = session.Executors;
        var unavailable = session.Unavailable;
        await WriteOfferAsync(db, session, cancellationToken);
        // Only now: a write that failed is tried again by the next heartbeat after the interval.
        session.OfferUnsaved = false;
        logger.LogInformation("Agent {Name} ({Agent}) now offers {Executors}; without {Unavailable}.", session.Name, session.AgentId,
            string.Join(",", executors.Where(AgentAvailability.KnownExecutors.Contains)), string.Join(",", unavailable.Keys));
        signal.Wake();
    }

    public async Task OnHeartbeatAsync(Guid agentId, IEnumerable<string> runningAttemptIds, CancellationToken cancellationToken)
    {
        // Only conditional, atomic renewals bypass the engine's single-writer lock.
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
        var ids = runningAttemptIds.Select(Guid.Parse).ToList();
        var lease = clock.GetUtcNow().AddSeconds(options.Value.AgentChannel.LeaseSeconds);
        await db.Attempts
            .Where(a => a.AgentId == agentId && ids.Contains(a.Id)
                        && (a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running))
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.LeaseExpiresAt, lease), cancellationToken);
        await db.Agents.Where(a => a.Id == agentId)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.LastSeenAt, clock.GetUtcNow()), cancellationToken);
    }

    public Task OnStartedAsync(Guid agentId, Guid attemptId, CancellationToken cancellationToken) =>
        WithDbAsync(async db =>
        {
            var attempt = await db.Attempts.FirstOrDefaultAsync(a => a.Id == attemptId && a.AgentId == agentId, cancellationToken);
            if (attempt?.Status == AttemptStatuses.Assigned)
            {
                attempt.Status = AttemptStatuses.Running;
                attempt.StartedAt = clock.GetUtcNow();
                attempt.LeaseExpiresAt = clock.GetUtcNow().AddSeconds(options.Value.AgentChannel.LeaseSeconds);
                await db.SaveChangesAsync(cancellationToken);
            }
            else if (attempt is { StartedAt: null })
            {
                // Lost with a connection and resent after the lease ended. It changes nothing
                // about the attempt, but that it ran still decides what a missing state means.
                attempt.StartedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken);
            }
        }, cancellationToken);

    /// <summary>Records an attempt's result. Returns true when the agent may forget it.</summary>
    public async Task<bool> OnResultAsync(Guid agentId, AttemptResult result, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(result.AttemptId, out var attemptId))
        {
            return true;
        }

        // What the agent sent, before anything below is changed in it.
        var received = ArtifactStore.Sha256(result.ToByteArray());

        // A corrupted artifact fails the attempt; it is never stored or used.
        if (result.Artifacts.FirstOrDefault(a => ArtifactStore.Sha256(a.Content.Span) != a.Sha256.ToLowerInvariant()) is { } corrupted)
        {
            logger.LogWarning("Artifact {Name} of attempt {Attempt} does not match its checksum.", corrupted.Name, attemptId);
            result = new AttemptResult
            {
                AttemptId = result.AttemptId,
                // Losing the state of an apply loses what it did, so that outcome cannot be a known failure.
                Outcome = corrupted.Name == TofuStateStore.ArtifactName ? Outcome.Uncertain
                    : result.Outcome == Outcome.Succeeded ? Outcome.Failed : result.Outcome,
                OutputsJson = result.OutputsJson,
                Error = $"The artifact {corrupted.Name} did not match its checksum.",
            };
        }

        return await WithDbAsync(async (db, services) =>
        {
            var attempt = await db.Attempts.Include(a => a.Step).ThenInclude(s => s!.Run)
                .FirstOrDefaultAsync(a => a.Id == attemptId, cancellationToken);
            if (attempt is null || attempt.AgentId != agentId)
            {
                logger.LogWarning("Agent {Agent} reported a result for attempt {Attempt}, which is not assigned to it.", agentId, attemptId);
                return true;
            }

            // The result this attempt was settled with, sent again: nothing to do.
            if (attempt.ResultSha256 == received)
            {
                _resultFailures.Remove(attemptId);
                return true;
            }

            // An uncertain attempt takes one later result: its lease ran out, or the agent could only say it was uncertain
            // and knows better now, as when its stored result could not be read for a moment. Not one the server settled
            // itself because the outputs it had could not be kept: that result stands.
            var late = attempt.Status == AttemptStatuses.Uncertain && !attempt.LateResult && !attempt.OutputsWithheld;
            if (AttemptStatuses.IsTerminal(attempt.Status) && !late)
            {
                // The agent's report on an attempt the server cancelled changes nothing but this: its log is complete, and
                // no more lines are taken for it.
                if (attempt.Status == AttemptStatuses.Cancelled && attempt.ResultSha256 is null)
                {
                    attempt.ResultSha256 = received;
                    await db.SaveChangesAsync(cancellationToken);
                }

                _resultFailures.Remove(attemptId);
                return true;
            }

            // What the attempt reports is masked with what it was sent, read back if this process does not know it, and held
            // until this result is handled.
            using var held = await masks.EnsureAsync(attempt.Id, cancellationToken);
            JsonNode? outputs = null;
            string? outputsWithheld = null;
            if (!string.IsNullOrWhiteSpace(result.OutputsJson))
            {
                if (logs.TryMaskOutputs(attempt.Id, result.OutputsJson, out var masked, out var withheld))
                    outputs = masked;
                else
                {
                    // Fails closed: no outputs are kept, and what they would have said is unknown, not absent.
                    outputsWithheld = withheld;
                    logger.LogWarning("Attempt {Attempt} returned outputs that are not kept: {Reason}", attemptId, withheld);
                }
            }

            // State goes to the database, never to the artifact files.
            var state = result.Artifacts.FirstOrDefault(a => a.Name == TofuStateStore.ArtifactName);
            foreach (var artifact in result.Artifacts.Where(a => a != state))
            {
                await artifacts.SaveAsync(attempt.Id, artifact.Name, artifact.Content.ToByteArray(), cancellationToken);
                if (!await db.Artifacts.AnyAsync(a => a.AttemptId == attempt.Id && a.Name == artifact.Name, cancellationToken))
                {
                    db.Artifacts.Add(new ArtifactRecord
                    {
                        Id = Guid.CreateVersion7(),
                        AttemptId = attempt.Id,
                        Name = artifact.Name,
                        Sha256 = artifact.Sha256.ToLowerInvariant(),
                        Size = artifact.Content.Length,
                        CreatedAt = clock.GetUtcNow(),
                    });
                }

                if (artifact.Name is AssignmentBuilder.PlanArtifact or AssignmentBuilder.PreviewArtifact && outputs is JsonObject planOutputs)
                {
                    planOutputs["planSha256"] = artifact.Sha256.ToLowerInvariant();
                }
            }

            // What is kept is the outputs with what was added to them since they were masked, such as a plan's digest:
            // checked whole, and withheld whole rather than changed.
            if (outputs is not null && logs.Withholds(attempt.Id, outputs) is { } assembled)
            {
                outputs = null;
                outputsWithheld = assembled;
                logger.LogWarning("Attempt {Attempt} returned outputs that are not kept once assembled: {Reason}", attemptId, assembled);
            }

            attempt.Status = result.Outcome switch
            {
                Outcome.Succeeded => AttemptStatuses.Succeeded,
                Outcome.Cancelled => AttemptStatuses.Cancelled,
                Outcome.Uncertain => AttemptStatuses.Uncertain,
                _ => AttemptStatuses.Failed,
            };
            // Withheld outputs settle the attempt without them: what reads its outputs, such as a plan's approval, the
            // inventory or the VMID collision check, never takes their absence for evidence. Work that may have changed
            // something is uncertain; anything else failed.
            if (outputsWithheld is not null && attempt.Status == AttemptStatuses.Succeeded)
                attempt.Status = attempt.Mutating ? AttemptStatuses.Uncertain : AttemptStatuses.Failed;
            attempt.LateResult = late;
            attempt.OutputsWithheld = outputsWithheld is not null;
            attempt.ResultSha256 = received;
            var error = string.IsNullOrEmpty(result.Error) ? null : logs.MaskDiagnostic(attempt.Id, result.Error);
            // Joined with the server's words, the reported error is masked whole again.
            attempt.Error = outputsWithheld is null ? error
                : error is null || error == outputsWithheld ? outputsWithheld
                : logs.MaskDiagnostic(attempt.Id, $"{outputsWithheld} {error}");
            attempt.Outputs = outputs?.ToJsonString();
            attempt.FinishedAt = clock.GetUtcNow();
            attempt.LeaseExpiresAt = null;

            if (attempt.Step?.Run is { } tofuRun && attempt.Action == ExecutorActions.Apply && attempt.Executor == Executors.OpenTofu
                && RunContext.Parse(tofuRun.Context).ForStep(attempt.Step).OpenTofu is not null
                && await services.GetRequiredService<TofuStateStore>().SettleAsync(attempt, tofuRun.ResourceId, state?.Content.ToByteArray(), cancellationToken, attempt.Step.Stage ?? "") is { } refused)
            {
                logger.LogError("The state of attempt {Attempt} was refused: {Reason}", attempt.Id, refused);
                attempt.Status = AttemptStatuses.Uncertain;
                attempt.Error = $"The state of this apply was not saved: {refused} Inspect the guest and plan again.";
                outputs = null;
                attempt.Outputs = null;
            }

            if (Images.TemplateImages.IsBuild(attempt))
            {
                await logs.CloseAsync(attempt.Id, cancellationToken);
                await FinishImageBuildAsync(db, services, attempt, outputs, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                signal.Wake();
                _resultFailures.Remove(attemptId);
                return true;
            }

            if (attempt.Step is null)
            {
                // The observation's data is assembled from its outputs, and checked with its masks before they are forgotten.
                await FinishObservationAsync(db, attempt, outputs, cancellationToken);
                await logs.CloseAsync(attempt.Id, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                signal.Wake();
                _resultFailures.Remove(attemptId);
                return true;
            }

            await logs.CloseAsync(attempt.Id, cancellationToken);
            var step = attempt.Step;
            var run = step.Run!;
            // A late result of an attempt that was already retried is recorded, but the retry decides the step.
            var current = await db.Attempts.Where(a => a.StepId == step.Id).OrderByDescending(a => a.Number).FirstAsync(cancellationToken);
            var stepWaitsForThis = current.Id == attempt.Id
                                   && step.Status is StepStatuses.Running or StepStatuses.Queued or StepStatuses.Uncertain
                                   && !RunStatuses.IsTerminal(run.Status);
            if (stepWaitsForThis)
            {
                await db.Entry(run).Collection(r => r.Steps).Query().Include(s => s.Attempts).LoadAsync(cancellationToken);
                step.FinishedAt = clock.GetUtcNow();
                switch (attempt.Status)
                {
                    case AttemptStatuses.Succeeded:
                        step.Status = StepStatuses.Succeeded;
                        step.Detail = late ? "The result arrived after the agent had stopped reporting." : null;
                        step.Outputs = outputs?.ToJsonString();
                        if (step.Status == StepStatuses.Succeeded)
                            await ApplyEffectsAsync(db, services.GetRequiredService<VariableStore>(), run, step, outputs, cancellationToken);
                        break;
                    case AttemptStatuses.Cancelled:
                        step.Status = StepStatuses.Cancelled;
                        step.Detail = attempt.Error ?? "Cancelled.";
                        break;
                    case AttemptStatuses.Uncertain:
                        step.Status = StepStatuses.Uncertain;
                        step.Detail = attempt.Error ?? "The agent cannot tell whether the change was made. Plan again to see the current state.";
                        break;
                    default:
                        step.Status = StepStatuses.Failed;
                        step.Detail = attempt.Error ?? "The attempt failed.";
                        step.Outputs = outputs?.ToJsonString();
                        break;
                }

                if (run.Status == RunStatuses.Uncertain && step.Status != StepStatuses.Uncertain)
                {
                    run.Status = RunStatuses.Running;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await AdvanceAsync(db, run.Id, cancellationToken);
            signal.Wake();
            _resultFailures.Remove(attemptId);
            return true;
        }, cancellationToken);
    }

    /// <summary>Stores a running apply's state. Best effort: the result decides whether it is lost.</summary>
    public Task OnStateSnapshotAsync(Guid agentId, Guid attemptId, Artifact state, CancellationToken cancellationToken) =>
        WithDbAsync<bool>(async (db, services) =>
        {
            var attempt = await db.Attempts.Include(a => a.Step).ThenInclude(s => s!.Run)
                .FirstOrDefaultAsync(a => a.Id == attemptId, cancellationToken);
            if (attempt?.Step?.Run is not { } run || attempt.AgentId != agentId || attempt.Action != ExecutorActions.Apply || attempt.Executor != Executors.OpenTofu
                || RunContext.Parse(run.Context).ForStep(attempt.Step).OpenTofu is null)
            {
                logger.LogWarning("Agent {Agent} sent state for attempt {Attempt}, which may not write any.", agentId, attemptId);
                return false;
            }

            if (ArtifactStore.Sha256(state.Content.Span) != state.Sha256.ToLowerInvariant())
            {
                logger.LogWarning("A state snapshot of attempt {Attempt} does not match its checksum.", attemptId);
                return false;
            }

            if (await services.GetRequiredService<TofuStateStore>().WriteAsync(attempt.Id, run.ResourceId, state.Content.ToByteArray(), cancellationToken, attempt.Step.Stage ?? "") is { } refused)
            {
                logger.LogWarning("A state snapshot of attempt {Attempt} was refused: {Reason}", attemptId, refused);
                return false;
            }

            await db.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

    /// <summary>
    /// Gives every attempt an agent holds a full lease from now. For when the server starts: it could take no heartbeat while it
    /// was down, so a lease that ran out meanwhile says nothing about its agent. One that does not connect again still loses it.
    /// </summary>
    public Task RenewLeasesAsync(CancellationToken cancellationToken) =>
        WithDbAsync(async db =>
        {
            var lease = clock.GetUtcNow().AddSeconds(options.Value.AgentChannel.LeaseSeconds);
            var renewed = await db.Attempts
                .Where(a => (a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running) && a.LeaseExpiresAt < lease)
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.LeaseExpiresAt, lease), cancellationToken);
            if (renewed > 0)
                logger.LogInformation("Renewed the leases of {Count} attempt(s) that were with an agent when the server stopped.", renewed);
        }, cancellationToken);

    /// <summary>The error of an attempt whose lease ran out: the one kind of uncertain attempt that still takes a late result.</summary>
    public const string LeaseExpired = "The agent stopped reporting before the attempt finished.";

    private async Task ExpireLeasesAsync(OctopushyDb db, CancellationToken cancellationToken)
    {
        await TakeBackNeverWrittenAgainAsync(cancellationToken);
        await PruneHandOffsAsync(db, cancellationToken);
        var now = clock.GetUtcNow();
        var expired = await db.Attempts.Include(a => a.Step).ThenInclude(s => s!.Attempts)
            .Where(a => (a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running) && a.LeaseExpiresAt < now)
            .ToListAsync(cancellationToken);
        foreach (var attempt in expired)
        {
            // Its hand-off record stays while its write has not begun: if the session never wrote it, what it recorded as
            // released is still taken back when the session closes. The attempt is settled here, so it is not put back.
            attempt.Status = AttemptStatuses.Uncertain;
            attempt.Error = LeaseExpired;
            attempt.FinishedAt = now;
            attempt.LeaseExpiresAt = null;
            logger.LogWarning("The lease of attempt {Attempt} expired.", attempt.Id);
            var step = attempt.Step;
            if (Images.TemplateImages.IsBuild(attempt))
            {
                await ExpireImageBuildAsync(db, attempt, cancellationToken);
            }
            else if (step is null)
            {
                // An observation is not retried here; the schedule observes again.
                await FinishObservationAsync(db, attempt, null, cancellationToken);
            }
            else if (attempt.Mutating)
            {
                step.Status = StepStatuses.Uncertain;
                step.Detail = $"The agent stopped reporting during {attempt.Action}; whether the change was made is unknown. Plan again to see the current state, or wait for the agent to report.";
            }
            else if (step.Attempts.Count <= options.Value.Scheduling.ReadOnlyRetries)
            {
                // Read-only actions change nothing, so trying again elsewhere is safe.
                var retry = new Attempt
                {
                    Id = Guid.CreateVersion7(),
                    StepId = step.Id,
                    RunId = attempt.RunId,
                    Number = step.Attempts.Count + 1,
                    Executor = attempt.Executor,
                    Action = attempt.Action,
                    Mutating = false,
                    RequiredLabels = attempt.RequiredLabels,
                    HostKey = attempt.HostKey,
                    HostLimit = attempt.HostLimit,
                    CreatedAt = now,
                };
                db.Attempts.Add(retry);
                step.Status = StepStatuses.Queued;
                step.Detail = "Retrying: the previous agent stopped reporting.";
            }
            else
            {
                step.Status = StepStatuses.Failed;
                step.Detail = $"The agent stopped reporting, {step.Attempts.Count} times.";
            }
        }

        if (expired.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static void Requeue(Attempt attempt, string reason)
    {
        attempt.Status = AttemptStatuses.Queued;
        attempt.AgentId = null;
        attempt.AssignedAt = null;
        attempt.LeaseExpiresAt = null;
        attempt.WaitingReason = reason;
    }
}
