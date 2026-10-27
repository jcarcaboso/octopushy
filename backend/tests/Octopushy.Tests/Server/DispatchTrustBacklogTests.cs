using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Api;
using Octopushy.Server.Data;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class DispatchTrustBacklogTests(PostgresFixture postgres)
{
    [Fact]
    public async Task More_than_a_batch_of_old_source_observations_cannot_starve_current_source_assignments()
    {
        using var repository = TestRepository.Minimal();
        var installation = await File.ReadAllTextAsync(Path.Combine(repository.Root, "labs/home/proxmox/korriban/installation.yaml"));
        for (var i = 0; i < 200; i++)
        {
            repository.Write($"labs/home/proxmox/installation-{i:D3}/installation.yaml", installation);
        }

        await using var server = await ServerHarness.StartAsync(postgres, repository);
        using var client = await TrustedRevisionTests.AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        var previous = (await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!;
        await TrustedRevisionTests.UpdateSourceAsync(client, repository.Root + "/.");
        await server.SyncAndTrustAsync(client);
        var current = (await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!;
        Assert.NotEqual(previous.Id, current.Id);
        Assert.NotNull(current.TrustedAt);
        await server.TickAsync(); // Real observation attempts queue while there is no connected worker.

        // A current observation replaces a waiting one, so leave the whole backlog on the old source.
        var oldIds = await server.QueryAsync(db => db.Attempts.Where(a => a.StepId == null).Select(a => a.Id).ToListAsync());
        Assert.Equal(201, oldIds.Count);
        await server.WithDbAsync(db => db.Attempts.Where(a => oldIds.Contains(a.Id))
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.RevisionId, previous.Id)));
        var agent = await server.ConnectAgentAsync("home");
        await server.TickAsync();

        var retained = await server.QueryAsync(db => db.Attempts.Where(a => oldIds.Contains(a.Id)).ToListAsync());
        Assert.Equal(201, retained.Count);
        Assert.All(retained, a =>
        {
            Assert.Equal(AttemptStatuses.Queued, a.Status);
            Assert.Null(a.AgentId);
            Assert.Null(a.AssignedAt);
        });
        Assert.Contains(retained, a => a.WaitingReason?.Contains("Waiting for trust") == true);
        Assert.False(await server.QueryAsync(db => db.SecretResolutions.AnyAsync(r => oldIds.Contains(r.AttemptId))));

        var assignments = agent.TakeAssignments();
        Assert.Contains(assignments, a => a.Action == "preview");
        var assignedIds = assignments.Select(a => Guid.Parse(a.AttemptId)).ToList();
        var assignedRevisions = await server.QueryAsync(db => db.Attempts
            .Where(a => assignedIds.Contains(a.Id))
            .Select(a => a.StepId == null ? a.RevisionId : a.Step!.Run!.RevisionId)
            .ToListAsync());
        Assert.All(assignedRevisions, id => Assert.Equal(current.Id, id));
    }
}
