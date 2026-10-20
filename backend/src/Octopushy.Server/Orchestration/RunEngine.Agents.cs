using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;

namespace Octopushy.Server.Orchestration;

public sealed partial class RunEngine
{
    /// <summary>Assigns queued attempts to connected agents that are allowed and have room.</summary>
    public async Task DispatchAsync(CancellationToken cancellationToken)
    {
        var sends = await WithDbAsync(async (db, services) =>
        {
            var queued = await db.Attempts.Include(a => a.Step).ThenInclude(s => s!.Run)
                .Where(a => a.Status == AttemptStatuses.Queued)
                .OrderBy(a => a.CreatedAt)
                .Take(200)
                .ToListAsync(cancellationToken);
            var result = new List<(AgentSession Session, ServerMessage Message, Guid AttemptId)>();
            if (queued.Count == 0)
            {
                return result;
            }

            var active = await db.Attempts.AsNoTracking()
                .Where(a => a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running)
                .Select(a => new { a.AgentId, a.HostKey })
                .ToListAsync(cancellationToken);
            var perAgent = active.Where(a => a.AgentId is not null).GroupBy(a => a.AgentId!.Value).ToDictionary(g => g.Key, g => g.Count());
            var perHost = active.Where(a => a.HostKey is not null).GroupBy(a => a.HostKey!).ToDictionary(g => g.Key, g => g.Count());
            var connectedIds = connections.All.Select(s => s.AgentId).ToList();
            var agents = await db.Agents.AsNoTracking()
                .Where(a => !a.Disabled && connectedIds.Contains(a.Id))
                .ToListAsync(cancellationToken);
            var builder = services.GetRequiredService<AssignmentBuilder>();

            foreach (var attempt in queued)
            {
                var candidates = agents
                    .Where(a => connections.Get(a.Id) is { } s && s.Executors.Contains(attempt.Executor)
                                && attempt.RequiredLabels.All(a.Labels.Contains))
                    .ToList();
                string? waiting = null;
                if (candidates.Count == 0)
                {
                    waiting = attempt.RequiredLabels.Length == 0
                        ? "Waiting for a connected agent."
                        : $"Waiting for a connected agent with the labels {string.Join(", ", attempt.RequiredLabels)}.";
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
                var step = attempt.Step!;
                var run = step.Run!;
                Assignment assignment;
                try
                {
                    assignment = await builder.BuildAsync(attempt, step, run, RunContext.Parse(run.Context), agent.Id, cancellationToken);
                }
                catch (Exception e) when (e is AssignmentException or IOException or Git.GitException)
                {
                    logger.LogWarning("Attempt {Attempt} cannot be assigned: {Error}", attempt.Id, e.Message);
                    attempt.Status = AttemptStatuses.Failed;
                    attempt.Error = e.Message;
                    attempt.FinishedAt = clock.GetUtcNow();
                    step.Status = StepStatuses.Failed;
                    step.Detail = e.Message;
                    signal.Wake();
                    continue;
                }

                var now = clock.GetUtcNow();
                attempt.Status = AttemptStatuses.Assigned;
                attempt.AgentId = agent.Id;
                attempt.AssignedAt = now;
                attempt.LeaseExpiresAt = now.AddSeconds(options.Value.AgentChannel.LeaseSeconds);
                attempt.WaitingReason = null;
                step.Status = StepStatuses.Running;
                step.StartedAt ??= now;
                perAgent[agent.Id] = perAgent.GetValueOrDefault(agent.Id) + 1;
                if (attempt.HostKey is { } key)
                {
                    perHost[key] = perHost.GetValueOrDefault(key) + 1;
                }

                logs.RegisterSecrets(attempt.Id, assignment.Secrets.Values);
                result.Add((connections.Get(agent.Id)!, new ServerMessage { Assignment = assignment }, attempt.Id));
            }

            await db.SaveChangesAsync(cancellationToken);
            return result;
        }, cancellationToken);

        foreach (var (session, message, attemptId) in sends)
        {
            if (!await session.SendAsync(message, cancellationToken))
            {
                await RequeueAsync(attemptId, session.AgentId, "The assignment could not be delivered; it will be reassigned.", cancellationToken);
            }
        }
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
        }, cancellationToken);

    /// <summary>Records an attempt's result. Returns true when the agent may forget it.</summary>
    public async Task<bool> OnResultAsync(Guid agentId, AttemptResult result, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(result.AttemptId, out var attemptId))
        {
            return true;
        }

        // A corrupted artifact fails the attempt; it is never stored or used.
        if (result.Artifacts.FirstOrDefault(a => ArtifactStore.Sha256(a.Content.Span) != a.Sha256.ToLowerInvariant()) is { } corrupted)
        {
            logger.LogWarning("Artifact {Name} of attempt {Attempt} does not match its checksum.", corrupted.Name, attemptId);
            result = new AttemptResult
            {
                AttemptId = result.AttemptId,
                Outcome = result.Outcome == Outcome.Succeeded ? Outcome.Failed : result.Outcome,
                OutputsJson = result.OutputsJson,
                Error = $"The artifact {corrupted.Name} did not match its checksum.",
            };
        }

        return await WithDbAsync(async (db, _) =>
        {
            var attempt = await db.Attempts.Include(a => a.Step).ThenInclude(s => s!.Run)
                .FirstOrDefaultAsync(a => a.Id == attemptId, cancellationToken);
            if (attempt is null || attempt.AgentId != agentId)
            {
                logger.LogWarning("Agent {Agent} reported a result for attempt {Attempt}, which is not assigned to it.", agentId, attemptId);
                return true;
            }

            var late = attempt.Status == AttemptStatuses.Uncertain && !attempt.LateResult;
            if (AttemptStatuses.IsTerminal(attempt.Status) && !late)
            {
                return true;
            }

            JsonNode? outputs = null;
            if (!string.IsNullOrWhiteSpace(result.OutputsJson))
            {
                try
                {
                    outputs = JsonNode.Parse(logs.Mask(attempt.Id, result.OutputsJson));
                }
                catch (JsonException)
                {
                    logger.LogWarning("Attempt {Attempt} returned outputs that are not JSON.", attemptId);
                }
            }

            foreach (var artifact in result.Artifacts)
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

                if (artifact.Name == AssignmentBuilder.PlanArtifact && outputs is JsonObject planOutputs)
                {
                    planOutputs["planSha256"] = artifact.Sha256.ToLowerInvariant();
                }
            }

            attempt.Status = result.Outcome switch
            {
                Outcome.Succeeded => AttemptStatuses.Succeeded,
                Outcome.Cancelled => AttemptStatuses.Cancelled,
                Outcome.Uncertain => AttemptStatuses.Uncertain,
                _ => AttemptStatuses.Failed,
            };
            attempt.LateResult = late;
            attempt.Error = string.IsNullOrEmpty(result.Error) ? null : logs.Mask(attempt.Id, result.Error);
            attempt.Outputs = outputs?.ToJsonString();
            attempt.FinishedAt = clock.GetUtcNow();
            attempt.LeaseExpiresAt = null;
            logs.Close(attempt.Id);

            var step = attempt.Step!;
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
                        await ApplyEffectsAsync(db, run, step, outputs, cancellationToken);
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

    private async Task ExpireLeasesAsync(OctopushyDb db, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var expired = await db.Attempts.Include(a => a.Step).ThenInclude(s => s!.Attempts)
            .Where(a => (a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running) && a.LeaseExpiresAt < now)
            .ToListAsync(cancellationToken);
        foreach (var attempt in expired)
        {
            attempt.Status = AttemptStatuses.Uncertain;
            attempt.Error = "The agent stopped reporting before the attempt finished.";
            attempt.FinishedAt = now;
            attempt.LeaseExpiresAt = null;
            var step = attempt.Step!;
            if (attempt.Mutating)
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

            logger.LogWarning("The lease of attempt {Attempt} expired.", attempt.Id);
        }

        if (expired.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private Task RequeueAsync(Guid attemptId, Guid agentId, string reason, CancellationToken cancellationToken) =>
        WithDbAsync(async db =>
        {
            var attempt = await db.Attempts.FirstOrDefaultAsync(a => a.Id == attemptId && a.AgentId == agentId, cancellationToken);
            if (attempt?.Status == AttemptStatuses.Assigned)
            {
                Requeue(attempt, reason);
                await db.SaveChangesAsync(cancellationToken);
            }
        }, cancellationToken);

    private static void Requeue(Attempt attempt, string reason)
    {
        attempt.Status = AttemptStatuses.Queued;
        attempt.AgentId = null;
        attempt.AssignedAt = null;
        attempt.LeaseExpiresAt = null;
        attempt.WaitingReason = reason;
    }
}
