using Google.Protobuf;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Store;

namespace Octopushy.Server.Orchestration;

public sealed partial class RunEngine
{
    /// <summary>Assigns queued attempts to connected agents that are allowed and have room.</summary>
    public async Task DispatchAsync(CancellationToken cancellationToken)
    {
        await WithDbAsync(async (db, services) =>
        {
            var eligible = RepositoryTrust.EligibleRevisions(db);
            var pending = db.Attempts.Include(a => a.Step).ThenInclude(s => s!.Run)
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
            // A change that waited for an agent may have outlived its schedule since it was queued.
            // Stopping it here keeps its secrets from being released for nothing.
            foreach (var attempt in queued.Where(a => a is { Mutating: true, Step.Run.Reason: RunReasons.Schedule }).ToList())
            {
                if (await StopIfLateAsync(db, await LoadRunAsync(db, attempt.Step!.RunId, cancellationToken), clock.GetUtcNow(), cancellationToken))
                    queued.Remove(attempt);
            }

            var result = new List<(AgentSession Session, ServerMessage Message, Guid AttemptId)>();
            if (queued.Count == 0)
            {
                return 0;
            }

            var active = await db.Attempts.AsNoTracking()
                .Where(a => a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running)
                .Select(a => new { a.AgentId, a.HostKey })
                .ToListAsync(cancellationToken);
            var perAgent = active.Where(a => a.AgentId is not null).GroupBy(a => a.AgentId!.Value).ToDictionary(g => g.Key, g => g.Count());
            var perHost = active.Where(a => a.HostKey is not null).GroupBy(a => a.HostKey!).ToDictionary(g => g.Key, g => g.Count());
            var connectedIds = connections.All.Select(s => s.AgentId).ToList();
            var agents = await db.Agents.AsNoTracking()
                .Where(a => !a.Disabled && !a.Draining && connectedIds.Contains(a.Id))
                .ToListAsync(cancellationToken);
            var builder = services.GetRequiredService<AssignmentBuilder>();
            // Guests something was handed to in this pass: the database does not show them yet.
            var changing = new HashSet<Guid>();
            var observedOnly = await SafetyBoundary.ObservedOnlyInstallationsAsync(db, cancellationToken);

            foreach (var attempt in queued)
            {
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
                    continue;
                }

                if (await (run is null
                        ? RepositoryTrust.BlockedReasonAsync(db, attempt.RevisionId, context.Commit, context, cancellationToken)
                        : RepositoryTrust.BlockedReasonAsync(db, run, cancellationToken)) is { } trustReason)
                {
                    attempt.WaitingReason = trustReason;
                    if (run is not null)
                    {
                        run.Status = RunStatuses.Blocked;
                        run.Detail = trustReason;
                    }
                    continue;
                }
                // Asked again here, not only when the attempt was queued: others may have started since, and a
                // service's guest may have stopped being ready for it.
                var family = run is not null && attempt.Mutating ? await GuestWork.FamilyAsync(db, run.ResourceId, cancellationToken) : [];
                if (family.Count > 1 && family.Any(changing.Contains))
                {
                    attempt.WaitingReason = "Waiting for another run that is changing the same guest.";
                    continue;
                }

                if (run is not null && attempt.Mutating && await ChangeHeldAsync(db, run, context, dispatching: true, cancellationToken) is { } held)
                {
                    if (context.Stages is not null)
                        TakeBack(attempt, step!, run, held);
                    else
                        attempt.WaitingReason = held;
                    continue;
                }

                var resourceId = run?.ResourceId ?? attempt.ResourceId;
                var needsBuiltInState = attempt.Executor == "opentofu";
                var candidates = agents
                    .Where(a => connections.Get(a.Id) is { } s && s.Executors.Contains(attempt.Executor)
                                && attempt.RequiredLabels.All(a.Labels.Contains)
                                && (!needsBuiltInState || s.Capabilities.Contains(TofuStateStore.AgentCapability)))
                    .ToList();
                string? waiting = null;
                // Only agents with fresh evidence of every connection the step makes; the rest are asked, never waited for.
                // Labels, executors and capabilities above, and capacity, preference and every check of the assignment
                // below, still apply to those that remain.
                var reachable = candidates.Count == 0 ? default : await ReachableAsync(db, step, context, candidates, cancellationToken);
                if (candidates.Count == 0)
                {
                    var matchingLabels = agents.Any(a => connections.Get(a.Id) is not null && attempt.RequiredLabels.All(a.Labels.Contains));
                    waiting = matchingLabels
                        ? needsBuiltInState
                            ? $"Waiting for a connected agent advertising executor {attempt.Executor} with the required labels and built-in OpenTofu state; update the agent if it is older."
                            : $"Waiting for a connected agent advertising executor {attempt.Executor} with the required labels."
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
                    continue;
                }

                var agent = free.FirstOrDefault(a => a.Id == attempt.PreferredAgentId)
                            ?? free.MinBy(a => perAgent.GetValueOrDefault(a.Id))!;
                // Only through the connection whose evidence was checked: one that replaced it since has none. A replaced
                // connection is closed, so the hand-off below requeues the attempt.
                var session = reachable.Sessions is { } checkedOn ? checkedOn[agent.Id] : connections.Get(agent.Id);
                if (session is null)
                {
                    attempt.WaitingReason = "The agent disconnected before assignment.";
                    continue;
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
                    continue;
                }
                catch (Exception e) when (e is ExecutionUntrustedException or StateBusyException)
                {
                    attempt.WaitingReason = e.Message;
                    continue;
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
                        await FinishObservationAsync(db, attempt, null, cancellationToken);
                    }
                    else
                    {
                        step.Status = StepStatuses.Failed;
                        step.Detail = e.Message;
                    }

                    signal.Wake();
                    continue;
                }

                // Building an assignment takes time, so the limit is decided at the time this one records.
                var now = clock.GetUtcNow();
                if (attempt.Mutating && run is not null && await StopIfLateAsync(db, run, now, cancellationToken))
                    continue;

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

                changing.UnionWith(family);
                perAgent[agent.Id] = perAgent.GetValueOrDefault(agent.Id) + 1;
                if (attempt.HostKey is { } key)
                {
                    perHost[key] = perHost.GetValueOrDefault(key) + 1;
                }

                logs.RegisterSecrets(attempt.Id, assignment.Secrets.Values);
                result.Add((session, new ServerMessage { Assignment = assignment }, attempt.Id));
            }

            await db.SaveChangesAsync(cancellationToken);
            // Keep revocation and the final outbound hand-off under the same controller
            // gate. A full channel requeues immediately; it must not stall unrelated work.
            foreach (var (session, message, attemptId) in result)
            {
                if (!session.TrySend(message))
                {
                    var attempt = queued.Single(a => a.Id == attemptId);
                    Requeue(attempt, "The agent channel is full or disconnected; waiting to reassign.");
                    if (attempt.Step is { } requeued)
                    {
                        requeued.Status = StepStatuses.Queued;
                    }
                }
            }
            await db.SaveChangesAsync(cancellationToken);
            return result.Count;
        }, cancellationToken);
    }

    /// <summary>
    /// The candidates that may get the attempt as far as its connections go, and what the rest wait for. A step whose
    /// connections are not modeled, or that makes none, keeps them all.
    /// </summary>
    private async Task<(List<AgentRecord> Agents, IReadOnlyDictionary<Guid, AgentSession>? Sessions, string Waiting)> ReachableAsync(
        OctopushyDb db, Step? step, RunContext context, List<AgentRecord> candidates, CancellationToken cancellationToken)
    {
        var needs = await NetworkRequirements.ForAsync(db, step?.Name ?? StepNames.Observe, context.ForStep(step), cancellationToken);
        if (needs.Targets.Count == 0)
            return (candidates, null, "");
        var able = new List<AgentRecord>();
        var sessions = new Dictionary<Guid, AgentSession>();
        var reasons = new List<string>();
        foreach (var candidate in candidates)
        {
            if (connections.Get(candidate.Id) is not { } session)
                continue;
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

    public Task OnHelloAsync(Guid agentId, Hello hello, CancellationToken cancellationToken) =>
        WithDbAsync(async db =>
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
            signal.Wake();
        }, cancellationToken);

    public Task OnHeartbeatAsync(Guid agentId, IEnumerable<string> runningAttemptIds, CancellationToken cancellationToken) =>
        WithDbAsync(async db =>
        {
            var ids = runningAttemptIds.Select(Guid.Parse).ToList();
            var lease = clock.GetUtcNow().AddSeconds(options.Value.AgentChannel.LeaseSeconds);
            await db.Attempts
                .Where(a => a.AgentId == agentId && ids.Contains(a.Id)
                            && (a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running))
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.LeaseExpiresAt, lease), cancellationToken);
            await db.Agents.Where(a => a.Id == agentId)
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.LastSeenAt, clock.GetUtcNow()), cancellationToken);
        }, cancellationToken);

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
                return true;
            }

            // An uncertain attempt takes one later result: its lease ran out, or the agent could only say it was uncertain
            // and knows better now, as when its stored result could not be read for a moment. Not one the server settled
            // itself because the outputs it had could not be kept: that result stands.
            var late = attempt.Status == AttemptStatuses.Uncertain && !attempt.LateResult && !attempt.OutputsWithheld;
            if (AttemptStatuses.IsTerminal(attempt.Status) && !late)
            {
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

            if (attempt.Step?.Run is { } tofuRun && attempt.Action == "apply" && attempt.Executor == "opentofu"
                && RunContext.Parse(tofuRun.Context).ForStep(attempt.Step).OpenTofu is not null
                && await services.GetRequiredService<TofuStateStore>().SettleAsync(attempt, tofuRun.ResourceId, state?.Content.ToByteArray(), cancellationToken, attempt.Step.Stage ?? "") is { } refused)
            {
                logger.LogError("The state of attempt {Attempt} was refused: {Reason}", attempt.Id, refused);
                attempt.Status = AttemptStatuses.Uncertain;
                attempt.Error = $"The state of this apply was not saved: {refused} Inspect the guest and plan again.";
                outputs = null;
                attempt.Outputs = null;
            }

            if (attempt.Step is null)
            {
                // The observation's data is assembled from its outputs, and checked with its masks before they are forgotten.
                await FinishObservationAsync(db, attempt, outputs, cancellationToken);
                await logs.CloseAsync(attempt.Id, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                signal.Wake();
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
            return true;
        }, cancellationToken);
    }

    /// <summary>Stores a running apply's state. Best effort: the result decides whether it is lost.</summary>
    public Task OnStateSnapshotAsync(Guid agentId, Guid attemptId, Artifact state, CancellationToken cancellationToken) =>
        WithDbAsync<bool>(async (db, services) =>
        {
            var attempt = await db.Attempts.Include(a => a.Step).ThenInclude(s => s!.Run)
                .FirstOrDefaultAsync(a => a.Id == attemptId, cancellationToken);
            if (attempt?.Step?.Run is not { } run || attempt.AgentId != agentId || attempt.Action != "apply" || attempt.Executor != "opentofu"
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

    /// <summary>The error of an attempt whose lease ran out: the one kind of uncertain attempt that still takes a late result.</summary>
    public const string LeaseExpired = "The agent stopped reporting before the attempt finished.";

    private async Task ExpireLeasesAsync(OctopushyDb db, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var expired = await db.Attempts.Include(a => a.Step).ThenInclude(s => s!.Attempts)
            .Where(a => (a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running) && a.LeaseExpiresAt < now)
            .ToListAsync(cancellationToken);
        foreach (var attempt in expired)
        {
            attempt.Status = AttemptStatuses.Uncertain;
            attempt.Error = LeaseExpired;
            attempt.FinishedAt = now;
            attempt.LeaseExpiresAt = null;
            logger.LogWarning("The lease of attempt {Attempt} expired.", attempt.Id);
            var step = attempt.Step;
            if (step is null)
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
