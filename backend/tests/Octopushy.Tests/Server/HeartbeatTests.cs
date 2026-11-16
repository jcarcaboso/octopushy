using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class HeartbeatTests(PostgresFixture postgres)
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
            Id = Guid.NewGuid(), Executor = "proxmox", Action = "observe", AgentId = owner,
            Status = status, LeaseExpiresAt = oldLease, CreatedAt = clock.GetUtcNow(),
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
}
