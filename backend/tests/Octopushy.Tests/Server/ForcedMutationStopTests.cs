using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class ForcedMutationStopTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_force_stop_that_cannot_be_saved_does_not_interrupt_the_tool()
    {
        var failure = new FailStopSave();
        await using var server = await ServerHarness.StartAsync(postgres,
            services: services => services.ConfigureDbContext<OctopushyDb>(options => options.AddInterceptors(failure)));
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded,
            FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.NextAsync("apply");
        var run = await server.LatestRunAsync("home/korriban/krb-app-01", Operations.Provision);
        var session = new AgentSession(agent.Id, "force-stop-test", agent.Session.Executors,
            [AgentSession.ForcedMutationStopCapability], "podman");
        server.Services.GetRequiredService<AgentConnections>().Add(session);
        failure.Enabled = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            server.Engine.ForceStopAsync(run.Id, "operator", CancellationToken.None));

        Assert.False(session.Outbound.TryRead(out _));
        var saved = await server.LatestRunAsync("home/korriban/krb-app-01", Operations.Provision);
        Assert.Equal(RunStatuses.Running, saved.Status);
        Assert.Equal(AttemptStatuses.Assigned, saved.Steps.Single(s => s.Name == StepNames.Apply).Attempts.Single().Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Force_stop_sets_force_only_for_a_capable_session(bool capable)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded,
            FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        var apply = await agent.NextAsync("apply");
        var run = await server.LatestRunAsync("home/korriban/krb-app-01", Operations.Provision);
        var capabilities = agent.Session.Capabilities.ToList();
        if (capable) capabilities.Add(AgentSession.ForcedMutationStopCapability);
        var session = new AgentSession(agent.Id, "force-stop-test", agent.Session.Executors, capabilities, "podman");
        server.Services.GetRequiredService<AgentConnections>().Add(session);

        await server.Engine.ForceStopAsync(run.Id, "operator", CancellationToken.None);

        Assert.True(session.Outbound.TryRead(out var message));
        Assert.Equal(ServerMessage.KindOneofCase.Cancel, message.KindCase);
        Assert.Equal(apply.AttemptId, message.Cancel.AttemptId);
        Assert.Equal(capable, message.Cancel.Force);
        run = await server.LatestRunAsync("home/korriban/krb-app-01", Operations.Provision);
        Assert.Equal(RunStatuses.Uncertain, run.Status);
        Assert.Equal(AttemptStatuses.Uncertain, run.Steps.Single(s => s.Name == StepNames.Apply).Attempts.Single().Status);
    }
    [Fact]
    public async Task Normal_cancel_never_sets_force_even_for_a_capable_session()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var plan = await agent.NextAsync("preview");
        var run = await server.LatestRunAsync("home/korriban/krb-app-01", Operations.Provision);
        var session = new AgentSession(agent.Id, "force-stop-test", agent.Session.Executors,
            [AgentSession.ForcedMutationStopCapability], "podman");
        server.Services.GetRequiredService<AgentConnections>().Add(session);

        await server.Engine.CancelAsync(run.Id, "operator", CancellationToken.None);

        Assert.True(session.Outbound.TryRead(out var message));
        Assert.Equal(ServerMessage.KindOneofCase.Cancel, message.KindCase);
        Assert.Equal(plan.AttemptId, message.Cancel.AttemptId);
        Assert.False(message.Cancel.Force);
    }

    private sealed class FailStopSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled)
                throw new InvalidOperationException("Test storage failure before a force stop is durable.");
            return ValueTask.FromResult(result);
        }
    }
}
