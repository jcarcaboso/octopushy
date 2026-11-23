using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Server.Api;
using Octopushy.Server.Git;

namespace Octopushy.Tests.Server;

/// <summary>What the repository response says about a sync under way: the Repository page locks Sync now and draws it.</summary>
public class RepositorySyncProgressTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static async Task<RepositoryDto> RepositoryAsync(HttpClient client) => (await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!;

    private static async Task<RepositorySyncDto> UntilAsync(HttpClient client, string phase) =>
        await WithinAsync(client, phase, TimeSpan.FromSeconds(30)) ?? throw new Xunit.Sdk.XunitException($"The sync never reported the {phase} phase.");

    private static async Task<RepositorySyncDto?> WithinAsync(HttpClient client, string phase, TimeSpan limit)
    {
        var deadline = DateTimeOffset.UtcNow + limit;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((await RepositoryAsync(client)).Sync is { } sync && sync.Phase == phase) return sync;
            await Task.Delay(20);
        }
        return null;
    }

    /// <summary>Holds the engine's gate until the returned hold is released. Waiters on the gate are served in order.</summary>
    private static async Task<Hold> HoldEngineAsync(ServerHarness server)
    {
        var hold = new Hold();
        hold.Task = server.Engine.ExecuteAsync(async (_, _) =>
        {
            hold.Taken.SetResult();
            await hold.Released.Task;
            return 0;
        }, CancellationToken.None);
        await hold.Taken.Task;
        return hold;
    }

    private sealed class Hold
    {
        public TaskCompletionSource Taken { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Task { get; set; } = Task.CompletedTask;

        public void Release() => Released.TrySetResult();
    }

    /// <summary>Waits until the sync holds the Git gate, on its way to the engine's gate.</summary>
    private static async Task UntilGitGateTakenAsync(ServerHarness server)
    {
        var gate = server.Services.GetRequiredService<GitNetworkGate>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            using var probe = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            try
            {
                (await gate.EnterAsync(probe.Token)).Dispose();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            Assert.True(DateTimeOffset.UtcNow < deadline, "The sync never took the Git gate.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task A_sync_reports_its_phase_then_the_fetched_head_and_nothing_once_it_ends()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var client = await server.SignedInClientAsync();
        var sync = server.Services.GetRequiredService<RepositorySync>();

        // The harness runs no background loop, so a requested sync never begins: it must not lock the page.
        (await client.PostAsync("/api/repository/sync", null)).EnsureSuccessStatusCode();
        Assert.Null((await RepositoryAsync(client)).Sync);

        // Held at the Git gate: fetching, no commit known yet.
        var gate = await server.Services.GetRequiredService<GitNetworkGate>().EnterAsync(CancellationToken.None);
        var running = sync.SyncOnceAsync(CancellationToken.None);
        var fetching = await UntilAsync(client, RepositorySyncPhases.Fetch);
        Assert.Null(fetching.Commit);

        // Held at the engine's gate right after the fetch: the head is known before it is validated or applied. Under the Git
        // gate the sync takes the engine once, to pick changes; the test lets that one through and holds the next.
        var held = await HoldEngineAsync(server);
        gate.Dispose();
        await UntilGitGateTakenAsync(server);
        RepositorySyncDto? fetched;
        // If the test queued before the sync's pick, the sync waits behind it: pass the gate on once and hold it again.
        for (var attempt = 0; (fetched = await WithinAsync(client, RepositorySyncPhases.Workflows, TimeSpan.FromSeconds(2))) is null; attempt++)
        {
            Assert.True(attempt < 5, "The sync never reported the workflows phase.");
            var next = HoldEngineAsync(server);
            held.Release();
            held = await next;
        }
        Assert.Equal(fetching.StartedAt, fetched.StartedAt);
        Assert.NotNull(fetched.Commit);
        Assert.False(string.IsNullOrEmpty(fetched.Subject));
        Assert.False(string.IsNullOrEmpty(fetched.Author));

        held.Release();
        await running;
        var done = await RepositoryAsync(client);
        Assert.Null(done.Sync);
        Assert.Equal(fetched.Commit, done.Revision!.Commit);
        Assert.Null(sync.Progress);
    }
}
