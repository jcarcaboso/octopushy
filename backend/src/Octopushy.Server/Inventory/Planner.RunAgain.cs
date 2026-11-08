using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Inventory;

/// <summary>Running a failed run's work again, as a new run.</summary>
public sealed partial class Planner
{
    /// <summary>
    /// Starts a new run of the work a failed run did, at the latest revision, as a person would from the guest's or
    /// service's page. Nothing of the failed run is reused: the new run plans again and takes its own approval, so it
    /// is refused for the same reasons starting it there would be.
    /// </summary>
    public async Task<Guid> RunAgainAsync(Guid runId, CancellationToken cancellationToken)
    {
        var (resourceId, operation, action) = await engine.ExecuteAsync(async (db, _) =>
        {
            var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken)
                ?? throw new RunException("There is no such run.");
            if (run.Status != RunStatuses.Failed)
                throw new RunException("Only a failed run can be run again.");
            if (await NewerRunAsync(db, run, cancellationToken) is { } newer)
                throw new RunException($"Run #{newer} is newer and does the same work.");
            return (run.ResourceId, run.Operation, RunContext.Parse(run.Context).Ansible?.Action);
        }, cancellationToken);

        return operation switch
        {
            Operations.Provision or Operations.Retire => await PlanGuestAsync(resourceId, cancellationToken),
            Operations.Configure => await ConfigureGuestAsync(resourceId, action, cancellationToken),
            Operations.Pipeline => await DeployServiceAsync(resourceId, cancellationToken),
            Operations.RotateAccess => await RotateAccessAsync(resourceId, cancellationToken),
            _ => throw new RunException($"A {operation} run cannot be run again."),
        };
    }

    /// <summary>Whether a failed run is the latest of its work, so running it again does not repeat what a newer run did.</summary>
    public static async Task<bool> CanRunAgainAsync(OctopushyDb db, Run run, CancellationToken cancellationToken) =>
        run.Status == RunStatuses.Failed
        && run.Operation is Operations.Provision or Operations.Retire or Operations.Configure or Operations.Pipeline or Operations.RotateAccess
        && await NewerRunAsync(db, run, cancellationToken) is null;

    /// <summary>The number of a later run of the same resource that does the same work: the same action, for a configure run.</summary>
    private static async Task<long?> NewerRunAsync(OctopushyDb db, Run run, CancellationToken cancellationToken)
    {
        string[] same = run.Operation is Operations.Provision or Operations.Retire ? [Operations.Provision, Operations.Retire] : [run.Operation];
        var newer = await db.Runs.AsNoTracking()
            .Where(r => r.ResourceId == run.ResourceId && r.CreatedAt > run.CreatedAt && same.Contains(r.Operation))
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.Number, r.Context })
            .ToListAsync(cancellationToken);
        if (run.Operation != Operations.Configure)
            return newer.FirstOrDefault()?.Number;
        var action = RunContext.Parse(run.Context).Ansible?.Action;
        return newer.FirstOrDefault(r => RunContext.Parse(r.Context).Ansible?.Action == action)?.Number;
    }
}
