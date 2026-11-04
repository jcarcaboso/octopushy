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
    /// A retire run for <paramref name="resource"/>, deleted in <paramref name="removal"/>, or null when
    /// one is already under way, or, unless <paramref name="force"/>, was already proposed for it.
    /// </summary>
    /// <summary>
    /// Whether a run exists for this occurrence of the resource's desired work, of any status: work proposed and then run,
    /// failed or cancelled on purpose is not proposed again by itself, but work recorded without its run is. An occurrence is
    /// what recorded the work, not only its inputs, so coming back to inputs that ran before is new work:
    /// <list type="bullet">
    /// <item>a removal is this time the folder is gone: a retire run with these inputs made since the resource went missing;</item>
    /// <item>anything else is the revision that recorded the desired inputs: a run with these inputs for that revision, which a
    /// later revision that changes nothing for the resource keeps.</item>
    /// </list>
    /// </summary>
    public static Task<bool> ProposedAsync(OctopushyDb db, Resource resource, string inputsHash, CancellationToken ct)
    {
        if (resource.MissingSince is { } since)
            return db.Runs.AnyAsync(r => r.ResourceId == resource.Id && r.InputsHash == inputsHash && r.Operation == Operations.Retire
                && r.CreatedAt >= since, ct);
        var recordedBy = resource.DesiredRevisionId;
        return db.Runs.AnyAsync(r => r.ResourceId == resource.Id && r.InputsHash == inputsHash && r.RevisionId == recordedBy
            && (r.Operation == Operations.Provision || r.Operation == Operations.Retire), ct);
    }

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
            $"definition removed in {removal.Commit[..10]}", await GuestAccessCredentials.IssuedAsync(db, resource.Id, ct));
        if (!force && resource.DesiredInputsHash == run.InputsHash && resource.DesiredRevisionId == lastDefined.Id
            && await ProposedAsync(db, resource, run.InputsHash!, ct))
            return null;
        resource.DesiredInputsHash = run.InputsHash;
        resource.DesiredRevisionId = lastDefined.Id;
        return run;
    }
}
