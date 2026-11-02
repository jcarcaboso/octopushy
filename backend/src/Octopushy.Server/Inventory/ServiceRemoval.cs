using Microsoft.EntityFrameworkCore;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Inventory;

/// <summary>
/// What happens to a service whose folder was deleted. Nothing happens to its data unless the recipe
/// declared an action for it, and that action goes past a person. Its run is built from the revision
/// last applied, so it removes what that definition deployed.
/// </summary>
public static class ServiceRemoval
{
    /// <summary>The revision a missing service was last defined in, its definition there, and the action that removes it, if there is one.</summary>
    public static async Task<(DefinitionRevision Revision, LabRepository Repository, ServiceDefinition Service, RecipeAction? Action)?> DefinedAsync(
        OctopushyDb db, RevisionCache revisions, Resource resource, GuestConfiguration? configuration, CancellationToken ct)
    {
        var eligible = RepositoryTrust.EligibleRevisions(db);
        var applied = configuration?.AppliedRevisionId;
        var lastDefined = await eligible.AsNoTracking().FirstOrDefaultAsync(r => r.Id == applied, ct)
            ?? await db.Revisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == resource.LastRevisionId, ct);
        if (lastDefined is null)
            return null;
        var repository = await revisions.GetAsync(lastDefined.Commit, ct);
        if (repository.Service(resource.Address) is not { } service || !repository.IsValid(resource.Address))
            return null;
        return (lastDefined, repository, service, repository.Recipes[service.Recipe].Configure?.Removal);
    }

    /// <summary>
    /// The removal run for a service, or null when there is nothing to run. A service no run ever reached the guest for
    /// has nothing there, so it is retired here. One whose recipe declares no removal stays missing, and keeps running. A
    /// guest that is going takes it along, and its retire run says so. Unless <paramref name="force"/>, a removal is
    /// proposed once, so that a rejected one does not come back with every commit.
    /// </summary>
    public static async Task<Run?> ProposeAsync(OctopushyDb db, RevisionCache revisions, RunFactory factory, LabRepository current, Resource resource,
        DefinitionRevision removal, DateTimeOffset now, bool force, CancellationToken ct)
    {
        var configuration = await db.GuestConfigurations.FirstOrDefaultAsync(c => c.ResourceId == resource.Id, ct);
        if (configuration is not null)
            configuration.DesiredHash = null;
        if (current.Guest(resource.ParentAddress ?? "") is not { Retired: false })
            return null;
        // A run still going may yet change the guest, so what it leaves is decided once it ends.
        if (await db.Runs.AnyAsync(r => r.ResourceId == resource.Id && r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled, ct))
            return null;
        // A run that failed partway has left things on the guest too, so only what never got that far has nothing to remove.
        if (!await db.Attempts.AnyAsync(a => a.Mutating && a.Status != AttemptStatuses.Queued && a.Status != AttemptStatuses.Cancelled
                && db.Runs.Any(r => r.Id == a.RunId && r.ResourceId == resource.Id), ct))
        {
            resource.RetiredAt = now;
            return null;
        }

        var defined = await DefinedAsync(db, revisions, resource, configuration, ct)
            ?? throw new RunException("The service's last definition is not valid, so its removal cannot be planned; restore its folder instead.");
        if (defined.Action is not { } action)
            return null;
        var run = factory.Configure(defined.Repository, defined.Repository.Guest(defined.Service.Guest)!, resource, defined.Revision, action.Id,
            $"definition removed in {removal.Commit[..10]}", configuration, null, defined.Service, removal.Commit);
        if (!force && configuration?.ProposedHash == run.InputsHash)
            return null;
        configuration?.ProposedHash = run.InputsHash;
        return run;
    }
}
