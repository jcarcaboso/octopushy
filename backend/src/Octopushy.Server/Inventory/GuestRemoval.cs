using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
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
    public static async Task<Run?> ProposeAsync(OctopushyDb db, RevisionCache revisions, RunFactory factory, Resource resource,
        DefinitionRevision removal, bool force, CancellationToken ct)
    {
        if (resource.Kind != ResourceKinds.Guest || resource.RetiredAt is not null || resource.MissingSince is null)
            return null;
        if (await db.Runs.AnyAsync(r => r.ResourceId == resource.Id && r.Operation == Operations.Retire
            && r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled, ct))
            return null;

        // A worker whose enrollment was removed by hand is on the verify-only retirement path;
        // it is destroyed only when its removal is requested explicitly.
        if (!force && resource.ManagedWorker is { AgentId: { } agentId } && !await db.Agents.AnyAsync(a => a.Id == agentId, ct))
            return null;

        var eligible = RepositoryTrust.EligibleRevisions(db);
        var lastDefined = await eligible.AsNoTracking().FirstOrDefaultAsync(r => r.Id == resource.AppliedRevisionId, ct)
            ?? await db.Revisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == resource.LastRevisionId, ct)
            ?? throw new RunException("No revision that defined this guest is known; restore its folder instead.");
        var repository = await revisions.GetAsync(lastDefined.Commit, ct);
        var guest = repository.Guest(resource.Address);
        if (guest is null || !repository.IsValid(resource.Address))
            throw new RunException($"The guest's definition in {lastDefined.Commit[..10]} is not valid, so its removal cannot be planned; restore its folder instead.");

        var run = factory.RemoveDeleted(repository, guest, resource, lastDefined, removal.Commit,
            $"definition removed in {removal.Commit[..10]}");
        if (!force && resource.DesiredInputsHash == run.InputsHash && resource.DesiredRevisionId == lastDefined.Id)
            return null;
        resource.DesiredInputsHash = run.InputsHash;
        resource.DesiredRevisionId = lastDefined.Id;
        return run;
    }
}
