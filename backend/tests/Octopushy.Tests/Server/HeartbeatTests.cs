using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;

namespace Octopushy.Tests.Server;

public class HeartbeatTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Renewal_does_not_wait_for_the_engine_lock_and_only_renews_owned_active_attempts()
    {
        var clock = new TestClock { Now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()) };
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false, clock: clock);
        var agent = Guid.NewGuid();
        var other = Guid.NewGuid();
        var oldLease = clock.GetUtcNow().AddSeconds(-1);
        Attempt Attempt(string status, Guid owner) => new()
        {
            Id = Guid.NewGuid(),
            Executor = "proxmox",
            Action = "observe",
            AgentId = owner,
            Status = status,
            LeaseExpiresAt = oldLease,
            CreatedAt = clock.GetUtcNow(),
        };
        var attempts = new[]
        {
            Attempt(AttemptStatuses.Assigned, agent),
            Attempt(AttemptStatuses.Running, agent),
            Attempt(AttemptStatuses.Uncertain, agent),
            Attempt(AttemptStatuses.Succeeded, agent),
            Attempt(AttemptStatuses.Running, other),
            Attempt(AttemptStatuses.Running, agent),
        };
        await server.WithDbAsync(async db =>
        {
            db.Agents.AddRange(
                new AgentRecord { Id = agent, Name = "heartbeat", CertificateSerial = "heartbeat" },
                new AgentRecord { Id = other, Name = "other", CertificateSerial = "other" });
            db.Attempts.AddRange(attempts);
            await db.SaveChangesAsync();
        });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = server.Engine.ExecuteAsync(async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return true;
        }, CancellationToken.None);
        await entered.Task;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await server.Engine.OnHeartbeatAsync(agent, attempts.Take(5).Select(a => a.Id.ToString()), timeout.Token);
            var saved = await server.QueryAsync(db => db.Attempts.AsNoTracking().ToDictionaryAsync(a => a.Id));
            foreach (var attempt in attempts)
                Assert.Equal(attempt == attempts[0] || attempt == attempts[1] ? clock.GetUtcNow().AddSeconds(60) : oldLease,
                    saved[attempt.Id].LeaseExpiresAt);
            Assert.Equal(clock.GetUtcNow(), await server.QueryAsync(db => db.Agents.Where(a => a.Id == agent).Select(a => a.LastSeenAt).SingleAsync()));
        }
        finally
        {
            release.TrySetResult();
            await holder;
        }
    }

    [Fact]
    public async Task A_heartbeat_and_log_batches_on_the_stream_do_not_wait_for_the_engine_lock()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        var agent = await server.ConnectAgentAsync("home");
        server.Services.GetRequiredService<AgentConnections>().Disconnect(agent.Id);
        var oldLease = DateTimeOffset.UtcNow.AddSeconds(-1);
        var attempt = new Attempt
        {
            Id = Guid.NewGuid(),
            Executor = "proxmox",
            Action = "observe",
            AgentId = agent.Id,
            Status = AttemptStatuses.Running,
            LeaseExpiresAt = oldLease,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await server.WithDbAsync(async db =>
        {
            db.Attempts.Add(attempt);
            await db.SaveChangesAsync();
        });
        var stream = new AgentStream();
        stream.Send(new AgentMessage { Hello = new Hello { AgentVersion = "test", Executors = { "proxmox" } } });
        var service = ActivatorUtilities.CreateInstance<AgentChannelService>(server.Services);
        var connected = service.Connect(stream, stream, await AgentStream.CallAsAsync(server, agent));
        await stream.ReadingMessages.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = server.Engine.ExecuteAsync(async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return true;
        }, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // As an agent sends them: started, which waits for the engine lock, log batches and state snapshots, more than a
            // queue of 256 holds, and a result. Neither the heartbeat behind them nor the log batches may wait for the lock.
            stream.Send(new AgentMessage { Started = new AttemptStarted { AttemptId = attempt.Id.ToString() } });
            for (var i = 0; i < 300; i++)
                stream.Send(new AgentMessage { Log = new AttemptLog { AttemptId = attempt.Id.ToString(), FirstSequence = (ulong)i, Lines = { new LogLine { Text = "held" } } } });
            // An apply offers each new state it writes: many, while started still waits.
            for (var i = 0; i < 300; i++)
                stream.Send(new AgentMessage
                {
                    StateSnapshot = new AttemptStateSnapshot
                    {
                        AttemptId = attempt.Id.ToString(),
                        State = new Artifact { Name = "terraform.tfstate", Content = Google.Protobuf.ByteString.CopyFromUtf8($"{{\"serial\":{i}}}"), Sha256 = "" }
                    }
                });
            stream.Send(new AgentMessage { Result = new AttemptResult { AttemptId = attempt.Id.ToString(), Outcome = Outcome.Failed, Error = "late" } });
            stream.Send(new AgentMessage { Heartbeat = new Heartbeat { RunningAttemptIds = { attempt.Id.ToString() } } });
            var lease = oldLease;
            for (var i = 0; i < 100 && lease <= oldLease; i++)
            {
                await Task.Delay(50);
                lease = await server.QueryAsync(db => db.Attempts.Where(a => a.Id == attempt.Id).Select(a => a.LeaseExpiresAt!.Value).SingleAsync());
            }

            Assert.True(lease > oldLease, "The heartbeat waited for the engine lock behind the agent's other messages.");
            // The log batches are kept while the engine lock is still held.
            var store = server.Services.GetRequiredService<AttemptLogStore>();
            var logged = 0;
            for (var i = 0; i < 200 && logged < 300; i++)
            {
                await Task.Delay(50);
                logged = (await store.ReadAsync(attempt.Id, -1, 1000, CancellationToken.None)).Count;
            }

            Assert.Equal(300, logged);
        }
        finally
        {
            release.TrySetResult();
            await holder;
            stream.Complete();
            await connected.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
