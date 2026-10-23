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
        await server.TickAsync(); // Real observation attempts queue while there is no connected worker.
        var oldAttempts = await server.QueryAsync(db => db.Attempts
            .Where(a => a.Action == "observe" && a.Step!.Run!.RevisionId == previous.Id)
            .Select(a => new { a.Id, a.RunId })
            .ToListAsync());
        Assert.Equal(201, oldAttempts.Count);
        var oldIds = oldAttempts.Select(a => a.Id).ToList();

        await TrustedRevisionTests.UpdateSourceAsync(client, repository.Root + "/.");
        await server.SyncAndTrustAsync(client);
        var current = (await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!;
        Assert.NotEqual(previous.Id, current.Id);
        Assert.NotNull(current.TrustedAt);
        var agent = await server.ConnectAgentAsync("home");
        await server.TickAsync();

        var oldDetail = (await client.GetFromJsonAsync<RunDetailDto>($"/api/runs/{oldAttempts[0].RunId}"))!;
        Assert.Equal(RunStatuses.Blocked, oldDetail.Run.Status);
        Assert.Contains("Waiting for trust", oldDetail.Run.Detail);
        var retained = await server.QueryAsync(db => db.Attempts.Where(a => oldIds.Contains(a.Id)).ToListAsync());
        Assert.Equal(201, retained.Count);
        Assert.All(retained, a =>
        {
            Assert.Equal(AttemptStatuses.Queued, a.Status);
            Assert.Null(a.AgentId);
            Assert.Null(a.AssignedAt);
        });
        Assert.False(await server.QueryAsync(db => db.SecretResolutions.AnyAsync(r => oldIds.Contains(r.AttemptId))));

        var assignments = agent.TakeAssignments();
        Assert.NotEmpty(assignments);
        Assert.Contains(assignments, a => a.Action == "observe");
        var assignedIds = assignments.Select(a => Guid.Parse(a.AttemptId)).ToList();
        var assignedRevisions = await server.QueryAsync(db => db.Attempts
            .Where(a => assignedIds.Contains(a.Id))
            .Select(a => a.Step!.Run!.RevisionId)
            .ToListAsync());
        Assert.All(assignedRevisions, id => Assert.Equal(current.Id, id));
    }
}
