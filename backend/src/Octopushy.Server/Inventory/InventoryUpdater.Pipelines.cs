using Microsoft.EntityFrameworkCore;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Inventory;

public sealed partial class InventoryUpdater
{
    /// <summary>
    /// Records what each service's pipeline wants in this revision. The orchestrator proposes the run, because
    /// that also has to wait for the guest. A run that has not started changing anything gives way to new inputs.
    /// </summary>
    private async Task UpdatePipelinesAsync(OctopushyDb db, List<Run> activeRuns, DefinitionRevision revision, LabRepository repository,
        List<Resource> services, List<Guid> currentSourceIds, CancellationToken cancellationToken)
    {
        var states = await db.GuestConfigurations.ToDictionaryAsync(c => c.ResourceId, cancellationToken);
        foreach (var resource in services)
        {
            if (!resource.Valid)
            {
                await SupersedeAsync(db, activeRuns, revision, resource, $"The definition is invalid in {Short(revision.Commit)}.", cancellationToken);
                continue;
            }

            if (!states.TryGetValue(resource.Id, out var state))
            {
                state = new GuestConfiguration { ResourceId = resource.Id };
                db.GuestConfigurations.Add(state);
            }

            var hash = PipelineInputs.Build(repository, repository.Service(resource.Address)!).Hash;
            // The same inputs from another source are not the same run: one built from the old source never goes.
            if (state.DesiredHash == hash && currentSourceIds.Contains(state.DesiredRevisionId ?? Guid.Empty))
                continue;
            state.DesiredHash = hash;
            state.DesiredRevisionId = revision.Id;
            await SupersedeAsync(db, activeRuns, revision, resource, $"Superseded by {Short(revision.Commit)}.", cancellationToken);
        }
    }

    /// <summary>A renamed guest keeps its services: their identity, and so their state, follow it.</summary>
    private static void MoveServices(Dictionary<string, Resource> byAddress, string previous, string guest)
    {
        foreach (var service in byAddress.Values.Where(r => r.Kind == ResourceKinds.Service && r.ParentAddress == previous).ToList())
        {
            var moved = $"{guest}/{service.Address.Split('/')[^1]}";
            byAddress.Remove(service.Address);
            service.Address = moved;
            service.ParentAddress = guest;
            byAddress[moved] = service;
        }
    }
}
