using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class RunRaceTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";

    [Fact]
    public async Task Hello_reconciliation_finishes_before_the_session_is_welcomed()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var plan = await agent.NextAsync("preview");
        server.Services.GetRequiredService<AgentConnections>().Disconnect(agent.Id);
        var stream = new AgentStream
        {
            OnWrite = async message =>
            {
                if (message.Welcome is not null)
                    Assert.Equal(AttemptStatuses.Queued, await server.QueryAsync(db => db.Attempts
                        .Where(a => a.Id == Guid.Parse(plan.AttemptId)).Select(a => a.Status).SingleAsync()));
            },
        };
        stream.Send(new AgentMessage { Hello = new Hello { AgentVersion = "test", Executors = { "opentofu", "proxmox", "ansible" } } });
        var service = ActivatorUtilities.CreateInstance<AgentChannelService>(server.Services);
        var connected = service.Connect(stream, stream, await AgentStream.CallAsAsync(server, agent));
        try
        {
            await stream.ReadingMessages.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var run = await server.LatestRunAsync(Guest);
            Assert.Equal(AttemptStatuses.Queued, run.Steps.Single(s => s.Name == StepNames.Plan).Attempts.Single().Status);
        }
        finally
        {
            stream.Complete();
            await connected.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disable_refuses_running_and_uncertain_changes_and_keeps_late_results_accepted(bool expired)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        var apply = await agent.NextAsync("apply");
        if (expired)
        {
            await server.WithDbAsync(db => db.Attempts.Where(a => a.Id == Guid.Parse(apply.AttemptId))
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
            await server.TickAsync();
        }
        var run = await server.LatestRunAsync(Guest);
        var http = await server.SignedInClientAsync();
        var response = await http.PatchAsJsonAsync($"/api/agents/{agent.Id}", new { disabled = true });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains($"run #{run.Number}", await response.Content.ReadAsStringAsync());
        Assert.False(await server.QueryAsync(db => db.Agents.Where(a => a.Id == agent.Id).Select(a => a.Disabled).SingleAsync()));
        Assert.True(server.Services.GetRequiredService<AgentConnections>().IsConnected(agent.Id));
        Assert.True(await agent.ReportAsync(apply, Outcome.Succeeded));
        Assert.Equal(AttemptStatuses.Succeeded, await server.QueryAsync(db => db.Attempts.Where(a => a.Id == Guid.Parse(apply.AttemptId)).Select(a => a.Status).SingleAsync()));
    }

    [Fact]
    public async Task A_rolled_back_cancellation_sends_nothing_but_a_committed_cancellation_does()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var plan = await agent.NextAsync("preview");
        var run = await server.LatestRunAsync(Guest);
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.Engine.ExecuteAsync<bool>(async (db, _) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await server.Engine.CancelInternalAsync(db, run.Id, "superseded", CancellationToken.None);
            Assert.Empty(agent.TakeCancels());
            await transaction.RollbackAsync();
            throw new InvalidOperationException("revision rolled back");
        }, CancellationToken.None));
        Assert.Empty(agent.TakeCancels());
        Assert.Equal(AttemptStatuses.Assigned, await server.QueryAsync(db => db.Attempts.Where(a => a.Id == Guid.Parse(plan.AttemptId)).Select(a => a.Status).SingleAsync()));
        await server.Engine.CancelAsync(run.Id, "operator", CancellationToken.None);
        Assert.Equal(plan.AttemptId, Assert.Single(agent.TakeCancels()));
    }
}
