using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class ManagedWorkerAdmissionTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ready_managed_workers_require_explicit_workload_labels_in_dispatch_and_the_final_builder(bool optedIn)
    {
        using var repo = Definitions.ManagedWorkerDefinitionTests.Repository();
        if (!optedIn)
            repo.Write("labs/home/lab.yaml", "schemaVersion: 1\nname: Home\nexecution: { agentLabels: [] }");
        await using var server = await ServerHarness.StartAsync(postgres, repo);
        await server.SyncAndTrustAsync();
        await server.TickAsync();
        var worker = await server.ConnectAgentAsync("home");
        // Seed an already-enrolled, ready worker. The full bootstrap/readiness
        // handshake is exercised separately; this seam concerns admission.
        await server.WithDbAsync(async db =>
        {
            var binding = await db.ManagedWorkers.SingleAsync();
            binding.AgentId = worker.Id;
            binding.ReadyAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        });
        await server.Engine.ExecuteAsync(async (db, services) =>
        {
            var attempt = await db.Attempts.SingleAsync(a => a.StepId == null);
            var context = RunContext.Parse(attempt.Context!);
            var builder = services.GetRequiredService<AssignmentBuilder>();
            if (optedIn)
                Assert.Equal("observe", (await builder.BuildAsync(attempt, null, null, context,
                    worker.Id, CancellationToken.None)).Action);
            else
            {
                await Assert.ThrowsAsync<AssignmentException>(() =>
                    builder.BuildAsync(attempt, null, null, context, worker.Id, CancellationToken.None));
                Assert.False(await db.SecretResolutions.AnyAsync(r => r.AttemptId == attempt.Id));
            }
            return true;
        }, CancellationToken.None);
        await server.Engine.DispatchAsync(CancellationToken.None);
        var assignments = worker.TakeAssignments();
        if (optedIn)
            Assert.Equal("observe", Assert.Single(assignments).Action);
        else
        {
            Assert.Empty(assignments);
            var control = await server.ConnectAgentAsync("home");
            Assert.Equal("observe", (await control.NextAsync("observe")).Action);
        }
    }
}
