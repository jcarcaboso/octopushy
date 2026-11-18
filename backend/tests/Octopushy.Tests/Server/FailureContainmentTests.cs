using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

public class FailureContainmentTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Guest = "home/korriban/krb-app-01";

    [Fact]
    public async Task A_bad_run_does_not_stop_the_next_run_advancing_in_the_same_tick()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAndTrustAsync();
        var good = await server.LatestRunAsync(Guest);
        var bad = Guid.NewGuid();
        await server.WithDbAsync(async db =>
        {
            var installation = await db.Resources.SingleAsync(r => r.Address == "home/korriban");
            db.Runs.Add(new Run
            {
                Id = bad,
                ResourceId = installation.Id,
                RevisionId = good.RevisionId,
                Commit = good.Commit,
                Operation = Operations.Provision,
                Reason = "manual",
                Context = "[]",
                CreatedAt = good.CreatedAt.AddMinutes(-1),
            });
            await db.SaveChangesAsync();
        });
        await server.TickAsync();
        Assert.NotEmpty((await server.LatestRunAsync(Guest)).Steps.Single(s => s.Name == StepNames.Plan).Attempts);
        Assert.Contains("failed", (await server.QueryAsync(db => db.Runs.SingleAsync(r => r.Id == bad))).Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unexpected_dispatch_failure_does_not_strand_other_assignments()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAndTrustAsync();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        await server.WithDbAsync(async db =>
        {
            var original = await db.Attempts.FirstAsync(a => a.StepId == null && a.Status == AttemptStatuses.Queued);
            // Keep the pass to these three observations, ordered good, bad, good.
            db.Attempts.Remove(original);
            for (var i = 0; i < ids.Length; i++)
                db.Attempts.Add(new Attempt
                {
                    Id = ids[i],
                    Executor = original.Executor,
                    Action = original.Action,
                    ResourceId = original.ResourceId,
                    RevisionId = original.RevisionId,
                    Context = i == 1 ? "[]" : original.Context,
                    RequiredLabels = original.RequiredLabels,
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-3 + i),
                    Number = 1,
                });
            await db.SaveChangesAsync();
        });
        var agent = await server.ConnectAgentAsync("home");
        await server.Engine.DispatchAsync(CancellationToken.None);
        var sent = agent.TakeAssignments().Select(a => Guid.Parse(a.AttemptId)).ToList();
        Assert.Contains(ids[0], sent);
        Assert.DoesNotContain(ids[1], sent);
        Assert.Contains(ids[2], sent);
        Assert.Equal(AttemptStatuses.Failed, await server.QueryAsync(db => db.Attempts.Where(a => a.Id == ids[1]).Select(a => a.Status).SingleAsync()));
    }

    [Fact]
    public async Task A_failed_engine_phase_does_not_skip_configure_scheduling()
    {
        var fault = new LeaseQueryFailure();
        await using var server = await ServerHarness.StartAsync(postgres,
            Definitions.GuestConfigurationTests.Repository(), services: services =>
                services.AddDbContext<OctopushyDb>(options => options.AddInterceptors(fault)));
        var (_, _, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        fault.Armed = true;
        var orchestrator = ActivatorUtilities.CreateInstance<Orchestrator>(server.Services);
        await orchestrator.RunOnceAsync(false, CancellationToken.None);
        Assert.True(fault.Thrown);
        Assert.Equal(Operations.Configure, (await server.LatestRunAsync(Guest)).Operation);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_poison_result_does_not_close_the_stream_and_is_settled_and_acknowledged_after_three_failures(bool mutating, bool reconnect)
    {
        var fault = new ResultSaveFailure();
        await using var server = await ServerHarness.StartAsync(postgres, services: services =>
            services.AddDbContext<OctopushyDb>(options => options.AddInterceptors(fault)));
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var assignment = await agent.NextAsync("preview");
        if (mutating)
        {
            await agent.ReportAsync(assignment, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
            assignment = await agent.NextAsync("apply");
        }
        var poison = Guid.Parse(assignment.AttemptId);
        var healthy = await server.QueryAsync(db => db.Attempts.FirstAsync(a => a.AgentId == agent.Id && a.StepId == null && a.Status == AttemptStatuses.Assigned));
        fault.Attempt = poison;
        var stream = new AgentStream();
        var hello = new Hello { AgentVersion = "test", Executors = { "opentofu", "proxmox", "ansible" } };
        hello.RunningAttemptIds.Add(assignment.AttemptId);
        hello.RunningAttemptIds.Add(healthy.Id.ToString());
        stream.Send(new AgentMessage { Hello = hello });
        var service = ActivatorUtilities.CreateInstance<AgentChannelService>(server.Services);
        var connected = service.Connect(stream, stream, await AgentStream.CallAsAsync(server, agent));
        try
        {
            await stream.ReadingMessages.Task.WaitAsync(TimeSpan.FromSeconds(10));
            stream.Send(new AgentMessage { Result = new AttemptResult { AttemptId = assignment.AttemptId, Outcome = Outcome.Succeeded } });
            stream.Send(new AgentMessage { Result = new AttemptResult { AttemptId = healthy.Id.ToString(), Outcome = Outcome.Failed, Error = "healthy result" } });
            async Task<string> NextAckAsync()
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (true)
                {
                    var message = await stream.Sent.Reader.ReadAsync(timeout.Token);
                    if (message.ResultAck is { } ack) return ack.AttemptId;
                }
            }
            Assert.Equal(healthy.Id.ToString(), await NextAckAsync());
            Assert.False(connected.IsCompleted);
            for (var i = 0; i < 2; i++)
            {
                if (reconnect)
                {
                    stream.Complete();
                    await connected.WaitAsync(TimeSpan.FromSeconds(10));
                    stream = new AgentStream();
                    stream.Send(new AgentMessage { Hello = hello });
                    connected = service.Connect(stream, stream, await AgentStream.CallAsAsync(server, agent));
                    await stream.ReadingMessages.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
                stream.Send(new AgentMessage { Result = new AttemptResult { AttemptId = assignment.AttemptId, Outcome = Outcome.Succeeded } });
            }
            Assert.Equal(assignment.AttemptId, await NextAckAsync());
            Assert.Equal(3, fault.Failures);
            var saved = await server.QueryAsync(db => db.Attempts.SingleAsync(a => a.Id == poison));
            Assert.Equal(mutating ? AttemptStatuses.Uncertain : AttemptStatuses.Failed, saved.Status);
            Assert.Contains("result could not be recorded", saved.Error);
            Assert.Null(saved.LeaseExpiresAt);
            Assert.NotNull(saved.ResultSha256);
            // Lost acknowledgement: the same poison payload is acknowledged without recording or consuming a late result.
            stream.Send(new AgentMessage { Result = new AttemptResult { AttemptId = assignment.AttemptId, Outcome = Outcome.Succeeded } });
            Assert.Equal(assignment.AttemptId, await NextAckAsync());
            Assert.Equal(3, fault.Failures);
            Assert.Equal(AttemptStatuses.Failed, await server.QueryAsync(db => db.Attempts.Where(a => a.Id == healthy.Id).Select(a => a.Status).SingleAsync()));
            if (mutating)
            {
                // Uncertain work still takes one later result once recording works again.
                fault.Attempt = Guid.Empty;
                Assert.True(await agent.ReportAsync(assignment, Outcome.Succeeded));
                var late = await server.QueryAsync(db => db.Attempts.SingleAsync(a => a.Id == poison));
                Assert.Equal(AttemptStatuses.Succeeded, late.Status);
                Assert.True(late.LateResult);
            }
        }
        finally
        {
            stream.Complete();
            await connected.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class ResultSaveFailure : SaveChangesInterceptor
    {
        public Guid Attempt { get; set; }
        public int Failures { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Attempt>().Any(e =>
                    e.Entity.Id == Attempt && e.Entity.ResultSha256 is not null && e.State == EntityState.Modified
                    && e.Entity.Error?.StartsWith("The result could not be recorded", StringComparison.Ordinal) != true))
            {
                Failures++;
                throw new InvalidOperationException("synthetic result storage failure");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class LeaseQueryFailure : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool Thrown { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && !Thrown && command.CommandText.Contains("lease_expires_at") && command.CommandText.Contains("FROM attempts"))
            {
                Thrown = true;
                throw new InvalidOperationException("synthetic lease query failure");
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
