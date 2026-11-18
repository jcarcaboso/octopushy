using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Server.Agents;
using Octopushy.Server.Api;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>The line count of an attempt's log, as the run page shows it and as a restart finds it.</summary>
public class AttemptLogCountTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static async Task<(Guid Run, Guid Attempt, Guid Agent)> SeedAsync(ServerHarness server)
    {
        var (runId, stepId, attemptId, agentId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await server.WithDbAsync(async db =>
        {
            db.Agents.Add(new AgentRecord { Id = agentId, Name = "counter", CertificateSerial = "counter" });
            var run = new Run
            {
                Id = runId,
                ResourceId = Guid.NewGuid(),
                Operation = Operations.Provision,
                Reason = "manual",
                Status = RunStatuses.Running,
                Commit = "0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f",
                Context = """{"address":"lab/guest","proxmox":{"endpoint":"https://pve.invalid:8006","verifyTls":true}}""",
                CreatedAt = DateTimeOffset.UtcNow,
                StartedAt = DateTimeOffset.UtcNow,
            };
            var step = new Step { Id = stepId, RunId = runId, Name = StepNames.Plan, Kind = StepKinds.Agent, Status = StepStatuses.Running };
            step.Attempts.Add(new Attempt
            {
                Id = attemptId,
                StepId = stepId,
                RunId = runId,
                Number = 1,
                Executor = "opentofu",
                Action = "plan",
                AgentId = agentId,
                Status = AttemptStatuses.Running,
                CreatedAt = DateTimeOffset.UtcNow,
                StartedAt = DateTimeOffset.UtcNow,
            });
            run.Steps.Add(step);
            db.Runs.Add(run);
            // What the attempt was sent, read back to mask its lines when this process does not know it.
            db.SecretResolutions.Add(new SecretResolution { Key = "proxmox/korriban/read-only-token", Version = 1, AttemptId = attemptId, AgentId = agentId, ResolvedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });
        return (runId, attemptId, agentId);
    }

    private static Task LogAsync(ServerHarness server, Guid agent, Guid attempt, long first, int count) =>
        server.Engine.OnLogAsync(agent, attempt, first,
            Enumerable.Range(0, count).Select(i => (0L, "stdout", $"line {first + i} octopushy@pve!agent=secret-token")).ToList(), CancellationToken.None);

    private static async Task<long> ShownAsync(ServerHarness server, Guid run)
    {
        // Signed in again each time: a restart starts a new server, with the account the harness set up.
        using var client = server.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new { username = "fixture-trust", password = "synthetic trust password" })).EnsureSuccessStatusCode();
        var detail = (await client.GetFromJsonAsync<RunDetailDto>($"/api/runs/{run}"))!;
        return detail.Steps.Single().Attempts.Single().LogLines;
    }

    [Fact]
    public async Task A_restart_in_the_middle_of_an_attempt_counts_its_log_once_and_the_run_page_shows_every_line()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SignedInClientAsync();
        var (run, attempt, agent) = await SeedAsync(server);

        await LogAsync(server, agent, attempt, 0, 2500);
        Assert.Equal(2500, await ShownAsync(server, run));

        await server.RestartFixtureAsync();
        var logs = server.Services.GetRequiredService<AttemptLogStore>();
        for (var batch = 0; batch < 5; batch++)
            await LogAsync(server, agent, attempt, 2500 + batch * 10, 10);

        // The file is read once to learn where the log ends, however many batches follow.
        Assert.Equal(1, Interlocked.Read(ref logs.Scans));
        Assert.Equal(2550, await ShownAsync(server, run));
        var lines = await logs.ReadAsync(attempt, 2540, 100, CancellationToken.None);
        Assert.Equal(Enumerable.Range(2541, 9).Select(i => (long)i), lines.Select(l => l.Sequence));
        Assert.All(lines, l => Assert.DoesNotContain("secret-token", l.Text));

        // Finished, the count stands, and a restart reads it from the file again.
        await server.WithDbAsync(async db =>
        {
            await db.Attempts.Where(a => a.Id == attempt).ExecuteUpdateAsync(a => a
                .SetProperty(x => x.Status, AttemptStatuses.Succeeded).SetProperty(x => x.ResultSha256, "done"));
        });
        await LogAsync(server, agent, attempt, 2550, 10);
        Assert.Equal(2550, await ShownAsync(server, run));
        await server.RestartFixtureAsync();
        Assert.Equal(2550, await ShownAsync(server, run));
    }
}
