using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Options;

namespace Octopushy.Tests.Server;

/// <summary>An attempt's log keeps at most its limit of bytes, says once that it dropped the rest, and stays full after a restart.</summary>
public sealed class AttemptLogCapTests : IDisposable
{
    private const long Cap = 2000;
    private readonly TestRepository _data = new();

    public void Dispose() => _data.Dispose();

    private AttemptLogStore Store(Guid attempt)
    {
        var store = new AttemptLogStore(Options.Create(new OctopushyOptions
        {
            DataDirectory = _data.Root,
            AgentChannel = new AgentChannelOptions { MaxLogBytesPerAttempt = Cap },
        }));
        store.RegisterSecrets(attempt, []);
        return store;
    }

    private static List<(long, string, string)> Lines(int count) =>
        Enumerable.Range(0, count).Select(i => (0L, "stdout", $"line {i} " + new string('x', 80))).ToList();

    private string PathOf(Guid attempt) => Path.Combine(_data.Root, "logs", $"{attempt}.jsonl");

    [Fact]
    public async Task Past_the_limit_the_rest_is_dropped_and_said_once()
    {
        var attempt = Guid.CreateVersion7();
        var store = Store(attempt);

        var stored = await store.AppendAsync(attempt, 0, Lines(100), CancellationToken.None);
        var lines = await store.ReadAsync(attempt, -1, 1000, CancellationToken.None);
        Assert.Equal(stored, lines.Count);
        Assert.InRange(lines.Count, 2, 99);
        Assert.All(lines.SkipLast(1), l => Assert.Equal("stdout", l.Stream));
        var marker = lines[^1];
        Assert.Equal(AttemptLogStore.ServerStream, marker.Stream);
        Assert.Contains("Octopushy:AgentChannel:MaxLogBytesPerAttempt", marker.Text);
        Assert.Contains($"{Cap} bytes", marker.Text);
        // Only the marker may pass the limit.
        Assert.True(new FileInfo(PathOf(attempt)).Length - (marker.Text.Length + 100) <= Cap);

        // Later lines, and those after a restart that reads the log from its file, are dropped without another marker.
        var size = new FileInfo(PathOf(attempt)).Length;
        Assert.Equal(stored, await store.AppendAsync(attempt, 100, Lines(10), CancellationToken.None));
        Assert.Equal(stored, await Store(attempt).AppendAsync(attempt, 110, Lines(10), CancellationToken.None));
        Assert.Equal(size, new FileInfo(PathOf(attempt)).Length);
    }

    [Fact]
    public async Task A_log_within_the_limit_is_kept_whole_across_a_restart()
    {
        var attempt = Guid.CreateVersion7();
        await Store(attempt).AppendAsync(attempt, 0, Lines(5), CancellationToken.None);
        await Store(attempt).AppendAsync(attempt, 5, Lines(5), CancellationToken.None);
        var lines = await Store(attempt).ReadAsync(attempt, -1, 1000, CancellationToken.None);
        Assert.Equal(10, lines.Count);
        Assert.DoesNotContain(lines, l => l.Stream == AttemptLogStore.ServerStream);
    }

    [Fact]
    public async Task A_deleted_log_starts_again_with_its_full_limit()
    {
        var attempt = Guid.CreateVersion7();
        var store = Store(attempt);
        await store.AppendAsync(attempt, 0, Lines(100), CancellationToken.None);
        await store.DeleteAsync(attempt, CancellationToken.None);
        store.RegisterSecrets(attempt, []);
        Assert.Equal(5, await store.AppendAsync(attempt, 0, Lines(5), CancellationToken.None));
    }
}

/// <summary>Which attempts take an agent's log lines.</summary>
public class AttemptLogAcceptanceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static Task LogAsync(ServerHarness server, Guid agent, Guid attempt, long sequence, string text) =>
        server.Engine.OnLogAsync(agent, attempt, sequence, [(0, "stdout", text)], CancellationToken.None);

    private static async Task<List<string>> TextAsync(ServerHarness server, Guid attempt) =>
        (await server.Services.GetRequiredService<AttemptLogStore>().ReadAsync(attempt, -1, 100, CancellationToken.None)).Select(l => l.Text).ToList();

    [Fact]
    public async Task A_running_attempt_takes_lines_and_a_finished_one_takes_no_more()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAndTrustAsync();
        var agent = await server.ConnectAgentAsync("home");
        var observation = await agent.NextAsync("observe");
        var id = Guid.Parse(observation.AttemptId);

        await LogAsync(server, agent.Id, id, 0, "while running");
        // Another agent's lines are never kept.
        await LogAsync(server, Guid.NewGuid(), id, 1, "from someone else");
        Assert.True(await agent.ReportAsync(observation, Outcome.Succeeded, new JsonObject()));
        await LogAsync(server, agent.Id, id, 1, "after the result");

        Assert.Equal(["while running"], await TextAsync(server, id));
    }

    [Fact]
    public async Task An_attempt_whose_lease_ran_out_takes_lines_until_its_late_result()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAndTrustAsync();
        var agent = await server.ConnectAgentAsync("home");
        var observation = await agent.NextAsync("observe");
        var id = Guid.Parse(observation.AttemptId);
        await server.WithDbAsync(db => db.Attempts.Where(a => a.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        await server.TickAsync();
        Assert.Equal(AttemptStatuses.Uncertain, await server.QueryAsync(db => db.Attempts.Where(a => a.Id == id).Select(a => a.Status).SingleAsync()));

        // The agent comes back and sends what it logged, then its result: the log arrives before the result, and is kept.
        await LogAsync(server, agent.Id, id, 0, "logged while away");
        Assert.True(await agent.ReportAsync(observation, Outcome.Succeeded, new JsonObject()));
        await LogAsync(server, agent.Id, id, 1, "after the late result");

        Assert.Equal(["logged while away"], await TextAsync(server, id));
    }

    [Fact]
    public async Task A_cancelled_attempt_takes_lines_until_its_agent_reports_on_it()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAndTrustAsync();
        var agent = await server.ConnectAgentAsync("home");
        var observation = await agent.NextAsync("observe");
        var id = Guid.Parse(observation.AttemptId);

        // Cancelled while the agent was away, far longer ago than a lease.
        await server.WithDbAsync(db => db.Attempts.Where(a => a.Id == id).ExecuteUpdateAsync(u => u
            .SetProperty(a => a.Status, AttemptStatuses.Cancelled).SetProperty(a => a.FinishedAt, DateTimeOffset.UtcNow.AddHours(-1))));

        // The agent comes back and sends what it kept, then its result. The server acknowledges that result, and the agent
        // forgets the log, so this is the only time those lines can be stored.
        await LogAsync(server, agent.Id, id, 0, "logged while stopping");
        Assert.True(await agent.ReportAsync(observation, Outcome.Cancelled, new JsonObject()));
        await LogAsync(server, agent.Id, id, 1, "after the report");

        Assert.Equal(["logged while stopping"], await TextAsync(server, id));
        Assert.Equal(AttemptStatuses.Cancelled, await server.QueryAsync(db => db.Attempts.Where(a => a.Id == id).Select(a => a.Status).SingleAsync()));
    }
}
