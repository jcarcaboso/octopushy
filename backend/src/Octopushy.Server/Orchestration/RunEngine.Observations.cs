using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// Observations: an agent reads an installation's hosts and VMs through its Proxmox API, in the
/// background. They change nothing, so they are not runs; only their failures are shown.
/// </summary>
public sealed partial class RunEngine
{
    /// <summary>Observations kept per installation.</summary>
    private const int KeptObservations = 50;

    /// <summary>Finished observation attempts kept per installation; their logs go with them.</summary>
    private const int KeptObservationAttempts = 10;

    /// <summary>
    /// Sends the installation to an agent to be observed with the context built from
    /// <paramref name="revision"/>, unless an observation of it is already with an agent; one still
    /// waiting is replaced by a newer revision. Unavailable credentials are recorded as the observation's error instead.
    /// </summary>
    public async Task ObserveAsync(OctopushyDb db, Resource installation, DefinitionRevision revision, RunContext context, CancellationToken cancellationToken)
    {
        var underWay = await db.Attempts.FirstOrDefaultAsync(
            a => a.ResourceId == installation.Id
                 && (a.Status == AttemptStatuses.Queued || a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running),
            cancellationToken);
        // One with an agent finishes first. One still waiting stays when it is from this revision.
        if (underWay is not null && (underWay.Status != AttemptStatuses.Queued || underWay.RevisionId == revision.Id))
        {
            return;
        }

        // A queued observation has released nothing, so it is simply replaced.
        if (underWay is not null)
        {
            db.Attempts.Remove(underWay);
        }

        var now = clock.GetUtcNow();
        var problems = await CredentialBindings.CheckAsync(db, null, context, cancellationToken, AssignmentBuilder.SecretsFor(StepNames.Observe, context));
        if (problems.Count > 0)
        {
            // Nothing was sent, so the installation stays due and is tried again at the next check.
            var error = $"Blocked credentials: {string.Join(", ", problems)}.";
            if (installation.ObserveError != error)
            {
                installation.ObserveError = error;
                installation.ObserveErrorAt = now;
            }
            installation.ObserveRequestedAt = null;
        }
        else
        {
            installation.ObserveRequestedAt = now;
            var action = AssignmentBuilder.ActionFor(StepNames.Observe);
            db.Attempts.Add(new Attempt
            {
                Id = Guid.CreateVersion7(),
                ResourceId = installation.Id,
                RevisionId = revision.Id,
                Context = context.Serialize(),
                Number = 1,
                Executor = action.Executor,
                Action = action.Action,
                Mutating = action.Mutating,
                RequiredLabels = context.AgentLabels.ToArray(),
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        signal.Wake();
    }

    /// <summary>Records a finished observation attempt on its installation; the caller saves.</summary>
    private async Task FinishObservationAsync(OctopushyDb db, Attempt attempt, JsonNode? outputs, CancellationToken cancellationToken)
    {
        var installation = await db.Resources.FirstOrDefaultAsync(r => r.Id == attempt.ResourceId, cancellationToken);
        // A late result of an observation that a newer one replaced says nothing current.
        if (installation is null
            || await db.Attempts.AnyAsync(a => a.ResourceId == installation.Id && a.CreatedAt > attempt.CreatedAt, cancellationToken))
        {
            return;
        }

        var now = clock.GetUtcNow();
        switch (attempt.Status)
        {
            case AttemptStatuses.Succeeded:
                installation.ObserveError = null;
                installation.ObserveErrorAt = null;
                db.Observations.Add(new Observation
                {
                    Id = Guid.CreateVersion7(),
                    ResourceId = installation.Id,
                    AttemptId = attempt.Id,
                    ObservedAt = now,
                    Data = outputs?.ToJsonString() ?? "{}",
                });
                var oldObservations = await db.Observations
                    .Where(o => o.ResourceId == installation.Id)
                    .OrderByDescending(o => o.ObservedAt)
                    .Skip(KeptObservations - 1)
                    .Select(o => o.Id)
                    .ToListAsync(cancellationToken);
                await db.Observations.Where(o => oldObservations.Contains(o.Id)).ExecuteDeleteAsync(cancellationToken);
                break;
            case AttemptStatuses.Cancelled:
                break;
            default:
                installation.ObserveError = attempt.Error ?? "The observation failed.";
                installation.ObserveErrorAt = now;
                break;
        }

        var oldAttempts = await db.Attempts
            .Where(a => a.ResourceId == installation.Id && a.Id != attempt.Id
                        && a.Status != AttemptStatuses.Queued && a.Status != AttemptStatuses.Assigned && a.Status != AttemptStatuses.Running)
            .OrderByDescending(a => a.CreatedAt)
            .Skip(KeptObservationAttempts - 1)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);
        if (oldAttempts.Count > 0)
        {
            await db.Attempts.Where(a => oldAttempts.Contains(a.Id)).ExecuteDeleteAsync(cancellationToken);
            foreach (var id in oldAttempts)
            {
                logs.Delete(id);
            }
        }
    }
}
