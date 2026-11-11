using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
using Octopushy.Server.Store;
using Octopushy.Server.Git;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Inventory;

/// <summary>
/// Removal of a guest whose folder was deleted. The retire run is built from the revision the
/// guest was last applied from (or else last defined in), so it destroys what that definition
/// created, and it follows that definition's approval and destructive policy.
/// </summary>
public static class GuestRemoval
{
    /// <summary>
    /// Whether a run exists for this occurrence of the resource's desired work, of any status: work proposed and then run,
    /// failed or cancelled on purpose is not proposed again by itself, but work recorded without its run is. Each new piece of
    /// desired work gets its own occurrence id on the resource when it is recorded, and the run proposed for it carries that
    /// id: coming back to inputs that ran before, rolling back to a commit whose revision is reused, or deleting a guest's
    /// folder a second time, is new work that no earlier run stands in for, whatever the clock said. A later revision that
    /// changes nothing for the resource keeps the occurrence, and with it the run.
    /// </summary>
    public static Task<bool> ProposedAsync(OctopushyDb db, Resource resource, string inputsHash, CancellationToken ct)
    {
        if (resource.DesiredOccurrence is not { } occurrence)
            return Task.FromResult(false);
        return db.Runs.AnyAsync(r => r.ResourceId == resource.Id && r.InputsHash == inputsHash && r.Occurrence == occurrence, ct);
    }

    /// <summary>
    /// Records <paramref name="inputsHash"/> as the resource's desired work, recorded by <paramref name="revision"/>, and
    /// stamps <paramref name="run"/> with its occurrence: the same occurrence when it is the same work proposed again, as when
    /// recovering a revision applied in part, a new one otherwise.
    /// </summary>
    public static void Record(Resource resource, string inputsHash, Guid revision, Run run, bool sameWork)
    {
        if (!sameWork || resource.DesiredOccurrence is null)
            resource.DesiredOccurrence = Guid.NewGuid();
        resource.DesiredInputsHash = inputsHash;
        resource.DesiredRevisionId = revision;
        run.Occurrence = resource.DesiredOccurrence;
    }

    /// <summary>
    /// A retire run for <paramref name="resource"/>, deleted in <paramref name="removal"/>, or null when
    /// one is already under way, or, unless <paramref name="force"/>, was already proposed for it.
    /// </summary>
    public static async Task<Run?> ProposeAsync(OctopushyDb db, RevisionCache revisions, RunFactory factory, Resource resource,
        DefinitionRevision removal, bool force, CancellationToken ct)
    {
        if (resource.Kind != ResourceKinds.Guest || resource.RetiredAt is not null || resource.MissingSince is null)
            return null;
        if (await db.Runs.AnyAsync(r => r.ResourceId == resource.Id && r.Operation == Operations.Retire
            && r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled, ct))
            return null;

        var eligible = RepositoryTrust.EligibleRevisions(db);
        var lastDefined = await eligible.AsNoTracking().FirstOrDefaultAsync(r => r.Id == resource.AppliedRevisionId, ct)
            ?? await db.Revisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == resource.LastRevisionId, ct)
            ?? throw new RunException("No revision that defined this guest is known; restore its folder instead.");
        var repository = await revisions.GetAsync(lastDefined, ct);
        var guest = repository.Guest(resource.Address);
        if (guest is null || !repository.IsValid(resource.Address))
            throw new RunException($"The guest's definition in {lastDefined.Commit[..10]} is not valid, so its removal cannot be planned; restore its folder instead.");

        var run = factory.RemoveDeleted(repository, guest, resource, lastDefined, removal.Commit,
            $"definition removed in {removal.Commit[..10]}", await GuestAccessCredentials.IssuedAsync(db, resource.Id, ct),
            await Images.TemplateImages.ForAsync(db, repository, guest, ct));
        // The same removal is this time the folder is gone; restoring it, valid or not, and deleting it again is another, since
        // a return clears the occurrence. Planned by hand, the same removal keeps its occurrence.
        var sameWork = resource.DesiredInputsHash == run.InputsHash && resource.DesiredRevisionId == lastDefined.Id;
        if (!force && sameWork && await ProposedAsync(db, resource, run.InputsHash!, ct))
            return null;
        Record(resource, run.InputsHash!, lastDefined.Id, run, sameWork);
        return run;
    }
}
