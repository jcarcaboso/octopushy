using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;

namespace Octopushy.Tests.Server;

/// <summary>
/// An assignment the server never began to write to its agent was never sent: it goes back in the queue, with the secrets it
/// recorded as released and the reservation it made taken back. One whose write began is decided by its result or its lease.
/// </summary>
public class UndeliveredAssignmentTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Guest = "home/korriban/krb-app-01";

    public static TheoryData<string[]> Capabilities => new()
    {
        { new[] { TofuStateStore.AgentCapability } },
        { new[] { TofuStateStore.AgentCapability, WorkerReachability.Capability, AgentSession.ForcedMutationStopCapability } },
    };

    [Theory]
    [MemberData(nameof(Capabilities))]
    public async Task A_change_never_written_before_the_stream_dropped_is_queued_again_and_sent_once(string[] capabilities)
    {
        var (server, agent, _) = await ApprovedAsync();
        await using var __ = server;
        var held = await ConnectHeldAtWelcomeAsync(server, agent, capabilities);
        await server.TickAsync();
        var apply = Assert.Single(await AppliesAsync(server));
        Assert.Equal(AttemptStatuses.Assigned, apply.Status);
        Assert.NotEmpty(await ReleasesAsync(server, apply.Id));
        Assert.NotNull((await server.ResourceAsync(Guest)).ReservedAt);

        // The connection drops while the welcome is written: the writer never ran.
        await held.DropAsync();

        apply = Assert.Single(await AppliesAsync(server));
        Assert.Equal((AttemptStatuses.Queued, 1, (Guid?)null), (apply.Status, apply.Number, apply.AgentId));
        Assert.Equal(StepStatuses.Queued, (await ApplyStepAsync(server)).Status);
        Assert.Empty(await ReleasesAsync(server, apply.Id));
        Assert.Null((await server.ResourceAsync(Guest)).ReservedAt);

        // Back, it gets the same attempt once, with its plan, and its releases are recorded again.
        var back = await server.ConnectAgentAsync("home");
        await server.TickAsync();
        await server.TickAsync();
        var sent = back.TakeAssignments().Where(a => a.Action == "apply").ToList();
        Assert.Equal(apply.Id.ToString(), Assert.Single(sent).AttemptId);
        Assert.Contains(sent[0].Artifacts, a => a.Name == AssignmentBuilder.PlanArtifact);
        Assert.Equal(AttemptStatuses.Assigned, Assert.Single(await AppliesAsync(server)).Status);
        Assert.NotEmpty(await ReleasesAsync(server, apply.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_change_whose_write_began_stays_assigned_and_is_uncertain_at_its_lease(bool writeThrows)
    {
        var (server, agent, clock) = await ApprovedAsync();
        await using var _ = server;
        var stream = new AgentStream
        {
            OnWrite = m => writeThrows && m.Assignment?.Action == "apply" ? throw new IOException("the connection dropped") : Task.CompletedTask,
        };
        var connection = await ConnectAsync(server, agent, stream, Hello([TofuStateStore.AgentCapability]));
        await server.TickAsync();
        if (writeThrows)
            await Assert.ThrowsAsync<IOException>(() => connection.WaitAsync(TimeSpan.FromSeconds(10)));
        else
        {
            Assert.Equal("apply", (await NextAssignmentAsync(stream)).Action);
            stream.Complete();
            await connection.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var apply = Assert.Single(await AppliesAsync(server));
        Assert.Equal(AttemptStatuses.Assigned, apply.Status);
        Assert.NotEmpty(await ReleasesAsync(server, apply.Id));
        await ExpireAsync(server, clock);
        Assert.Equal(AttemptStatuses.Uncertain, Assert.Single(await AppliesAsync(server)).Status);
        Assert.Equal(RunStatuses.Uncertain, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
    }

    [Fact]
    public async Task An_assignment_queued_on_a_connection_a_newer_one_replaced_is_queued_again_once_and_sent_to_the_newer()
    {
        var (server, agent, _) = await ApprovedAsync();
        await using var __ = server;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new AgentStream
        {
            OnWrite = m => m.ResultAck is not null ? Block(entered, release) : Task.CompletedTask,
        };
        var oldConnection = await ConnectAsync(server, agent, old, Hello([TofuStateStore.AgentCapability]));
        var connections = server.Services.GetRequiredService<AgentConnections>();
        // The writer is busy with a message ahead of the assignment.
        Assert.True(connections.Get(agent.Id)!.TrySend(new ServerMessage { ResultAck = new ResultAck { AttemptId = "x" } }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await server.TickAsync();
        var apply = Assert.Single(await AppliesAsync(server));
        Assert.Equal(AttemptStatuses.Assigned, apply.Status);

        var fresh = new AgentStream();
        await ConnectAsync(server, agent, fresh, Hello([TofuStateStore.AgentCapability]));
        release.SetResult();
        await oldConnection.WaitAsync(TimeSpan.FromSeconds(10));

        await server.TickAsync();
        await server.TickAsync();
        Assert.Equal(apply.Id.ToString(), (await NextAssignmentAsync(fresh)).AttemptId);
        await Task.Delay(200);
        Assert.DoesNotContain(Drain(fresh), m => m.Assignment is not null);
        Assert.DoesNotContain(Drain(old), m => m.Assignment is not null);
        apply = Assert.Single(await AppliesAsync(server));
        Assert.Equal((AttemptStatuses.Assigned, 1), (apply.Status, apply.Number));
    }

    [Fact]
    public async Task A_read_never_written_is_queued_again_once()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        server.Services.GetRequiredService<AgentConnections>().Disconnect(agent.Id);
        await server.SyncAndTrustAsync();
        var held = await ConnectHeldAtWelcomeAsync(server, agent, [TofuStateStore.AgentCapability]);
        await server.TickAsync();
        Assert.Equal(AttemptStatuses.Assigned, (await PreviewAsync(server)).Status);

        await held.DropAsync();
        // A later Hello without it re-queues reads too: it finds this one already queued.
        await ConnectAsync(server, agent, new AgentStream(), Hello([TofuStateStore.AgentCapability]));

        var preview = await PreviewAsync(server);
        Assert.Equal((AttemptStatuses.Queued, 1), (preview.Status, preview.Number));
        Assert.Single(await server.QueryAsync(db => db.Attempts.Where(a => a.Action == "preview").ToListAsync()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task What_a_session_never_wrote_is_not_kept_as_released_even_once_its_read_was_queued_or_handed_off_again(bool handedOffAgain)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var connections = server.Services.GetRequiredService<AgentConnections>();
        connections.Disconnect(agent.Id);
        // Records of no more use are looked for at every pass: the one of a hand-off never written is still of use.
        LookEveryPass(server);
        await server.SyncAndTrustAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new AgentStream
        {
            OnWrite = m => m.ResultAck is not null ? Block(entered, release) : Task.CompletedTask,
        };
        var oldConnection = await ConnectAsync(server, agent, old, Hello([TofuStateStore.AgentCapability]));
        // The writer is busy with a message ahead of the preview, so the preview is handed off and never written.
        Assert.True(connections.Get(agent.Id)!.TrySend(new ServerMessage { ResultAck = new ResultAck { AttemptId = "x" } }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await server.TickAsync();
        var preview = await PreviewAsync(server);
        Assert.Equal(AttemptStatuses.Assigned, preview.Status);
        var first = (await ReleasesAsync(server, preview.Id)).Select(r => r.Id).ToList();
        Assert.NotEmpty(first);

        // A newer connection's Hello queues the read again before the older session has closed and said what it never wrote.
        var fresh = new AgentStream();
        await ConnectAsync(server, agent, fresh, Hello([TofuStateStore.AgentCapability]));
        Assert.Equal(AttemptStatuses.Queued, (await PreviewAsync(server)).Status);
        var second = new List<long>();
        if (handedOffAgain)
        {
            await server.TickAsync();
            Assert.Equal(preview.Id.ToString(), (await NextAssignmentAsync(fresh)).AttemptId);
            second = (await ReleasesAsync(server, preview.Id)).Select(r => r.Id).Except(first).ToList();
            Assert.NotEmpty(second);
        }

        release.SetResult();
        await oldConnection.WaitAsync(TimeSpan.FromSeconds(10));

        // What the older session recorded as released was never sent, and is gone; what the newer one sent stays.
        Assert.Equal(second, (await ReleasesAsync(server, preview.Id)).Select(r => r.Id).ToList());
        preview = await PreviewAsync(server);
        Assert.Equal((handedOffAgain ? AttemptStatuses.Assigned : AttemptStatuses.Queued, 1), (preview.Status, preview.Number));
        Assert.DoesNotContain(Drain(old), m => m.Assignment is not null);
        await Task.Delay(200);
        Assert.DoesNotContain(Drain(fresh), m => m.Assignment is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_read_a_newer_session_wrote_is_not_put_back_by_the_older_session_that_never_did(bool newerClosedFirst)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var connections = server.Services.GetRequiredService<AgentConnections>();
        connections.Disconnect(agent.Id);
        LookEveryPass(server);
        await server.SyncAndTrustAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new AgentStream
        {
            OnWrite = m => m.ResultAck is not null ? Block(entered, release) : Task.CompletedTask,
        };
        var oldConnection = await ConnectAsync(server, agent, old, Hello([TofuStateStore.AgentCapability]));
        Assert.True(connections.Get(agent.Id)!.TrySend(new ServerMessage { ResultAck = new ResultAck { AttemptId = "x" } }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await server.TickAsync();
        var preview = await PreviewAsync(server);
        Assert.Equal(AttemptStatuses.Assigned, preview.Status);

        // The newer connection's Hello queues the read again, and it is handed off and written there.
        var fresh = new AgentStream();
        var freshConnection = await ConnectAsync(server, agent, fresh, Hello([TofuStateStore.AgentCapability]));
        await server.TickAsync();
        Assert.Equal(preview.Id.ToString(), (await NextAssignmentAsync(fresh)).AttemptId);
        var sent = (await ReleasesAsync(server, preview.Id)).Select(r => r.Id).ToList();
        // Passes that look for records of no more use keep the newer one: its attempt is assigned and not started.
        await server.TickAsync();
        if (newerClosedFirst)
        {
            fresh.Complete();
            await freshConnection.WaitAsync(TimeSpan.FromSeconds(10));
            await server.TickAsync();
        }

        release.SetResult();
        await oldConnection.WaitAsync(TimeSpan.FromSeconds(10));

        // The agent may have the read from the newer session: it stays assigned, to be decided by its result or its lease.
        preview = await PreviewAsync(server);
        Assert.Equal((AttemptStatuses.Assigned, 1), (preview.Status, preview.Number));
        var kept = (await ReleasesAsync(server, preview.Id)).Select(r => r.Id).ToList();
        Assert.NotEmpty(kept);
        Assert.All(kept, id => Assert.Contains(id, sent));
        Assert.DoesNotContain(Drain(old), m => m.Assignment is not null);
    }

    [Fact]
    public async Task A_hand_off_record_is_kept_while_its_attempt_is_assigned_and_not_started_and_dropped_once_it_started()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        server.Services.GetRequiredService<AgentConnections>().Disconnect(agent.Id);
        var engine = LookEveryPass(server);
        await server.SyncAndTrustAsync();
        var stream = new AgentStream();
        await ConnectAsync(server, agent, stream, Hello([TofuStateStore.AgentCapability]));
        await server.TickAsync();
        var preview = await PreviewAsync(server);
        Assert.Equal(preview.Id.ToString(), (await NextAssignmentAsync(stream)).AttemptId);

        await server.TickAsync();
        await server.TickAsync();
        Assert.True(engine.KeepsHandOff(preview.Id));

        await engine.OnStartedAsync(agent.Id, preview.Id, CancellationToken.None);
        await server.TickAsync();
        Assert.False(engine.KeepsHandOff(preview.Id));
    }

    [Fact]
    public async Task A_read_its_agents_next_Hello_queued_again_is_not_handed_to_the_earlier_connection_a_second_time()
    {
        var pause = new PauseRequeue();
        await using var server = await ServerHarness.StartAsync(postgres,
            services: s => s.ConfigureDbContext<OctopushyDb>(options => options.AddInterceptors(pause)));
        var agent = await server.ConnectAgentAsync("home");
        var connections = server.Services.GetRequiredService<AgentConnections>();
        connections.Disconnect(agent.Id);
        await server.SyncAndTrustAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new AgentStream
        {
            OnWrite = m => m.ResultAck is not null ? Block(entered, release) : Task.CompletedTask,
        };
        var oldConnection = await ConnectAsync(server, agent, old, Hello([TofuStateStore.AgentCapability]));
        Assert.True(connections.Get(agent.Id)!.TrySend(new ServerMessage { ResultAck = new ResultAck { AttemptId = "x" } }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await server.TickAsync();
        var preview = await PreviewAsync(server);
        Assert.Equal(AttemptStatuses.Assigned, preview.Status);
        var first = (await ReleasesAsync(server, preview.Id)).Select(r => r.Id).ToList();
        Assert.NotEmpty(first);

        // The newer connection's Hello is held while it queues the read again, and a dispatch waits behind it.
        pause.Armed = true;
        var fresh = new AgentStream();
        fresh.Send(new AgentMessage { Hello = Hello([TofuStateStore.AgentCapability]) });
        var service = ActivatorUtilities.CreateInstance<AgentChannelService>(server.Services);
        _ = service.Connect(fresh, fresh, await AgentStream.CallAsAsync(server, agent));
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var dispatch = server.Engine.DispatchAsync(CancellationToken.None);
        await Task.Delay(300);
        Assert.False(dispatch.IsCompleted);
        pause.Release.SetResult();
        await dispatch.WaitAsync(TimeSpan.FromSeconds(10));

        // That dispatch found the earlier connection closed already: the read went to the newer one, once.
        Assert.Equal(preview.Id.ToString(), (await NextAssignmentAsync(fresh)).AttemptId);
        var second = (await ReleasesAsync(server, preview.Id)).Select(r => r.Id).Except(first).ToList();
        Assert.NotEmpty(second);
        release.SetResult();
        await oldConnection.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(second, (await ReleasesAsync(server, preview.Id)).Select(r => r.Id).ToList());
        preview = await PreviewAsync(server);
        Assert.Equal((AttemptStatuses.Assigned, 1), (preview.Status, preview.Number));
        Assert.DoesNotContain(Drain(old), m => m.Assignment is not null);
    }

    [Fact]
    public async Task A_registration_whose_commit_went_through_and_then_failed_leaves_no_earlier_connection_to_hand_the_read_to()
    {
        var commit = new FailsAfterCommit();
        await using var server = await ServerHarness.StartAsync(postgres,
            services: s => s.ConfigureDbContext<OctopushyDb>(options => options.AddInterceptors(commit)));
        var agent = await server.ConnectAgentAsync("home");
        var connections = server.Services.GetRequiredService<AgentConnections>();
        connections.Disconnect(agent.Id);
        await server.SyncAndTrustAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new AgentStream
        {
            OnWrite = m => m.ResultAck is not null ? Block(entered, release) : Task.CompletedTask,
        };
        var oldConnection = await ConnectAsync(server, agent, old, Hello([TofuStateStore.AgentCapability]));
        Assert.True(connections.Get(agent.Id)!.TrySend(new ServerMessage { ResultAck = new ResultAck { AttemptId = "x" } }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await server.TickAsync();
        var preview = await PreviewAsync(server);
        Assert.Equal(AttemptStatuses.Assigned, preview.Status);
        Assert.NotEmpty(await ReleasesAsync(server, preview.Id));

        // The newer connection's Hello queues the read again and is committed, and the commit is then reported as failed.
        commit.Armed = true;
        var fresh = new AgentStream();
        fresh.Send(new AgentMessage { Hello = Hello([TofuStateStore.AgentCapability]) });
        var service = ActivatorUtilities.CreateInstance<AgentChannelService>(server.Services);
        var call = await AgentStream.CallAsAsync(server, agent);
        await Record.ExceptionAsync(() => service.Connect(fresh, fresh, call));
        Assert.False(commit.Armed);
        Assert.Equal(AttemptStatuses.Queued, (await PreviewAsync(server)).Status);

        // The earlier connection is the agent's no more, so a dispatch cannot hand it the read a second time.
        Assert.Null(connections.Get(agent.Id));
        await server.TickAsync();
        release.SetResult();
        await oldConnection.WaitAsync(TimeSpan.FromSeconds(10));

        preview = await PreviewAsync(server);
        Assert.Equal((AttemptStatuses.Queued, 1), (preview.Status, preview.Number));
        Assert.Empty(await ReleasesAsync(server, preview.Id));
        Assert.DoesNotContain(Drain(old), m => m.Assignment is not null);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("save")]
    public async Task What_was_never_written_is_taken_back_by_a_later_pass_when_taking_it_back_failed(string failing)
    {
        var failure = new TakeBackFails(failing);
        await using var server = await ServerHarness.StartAsync(postgres,
            services: s => s.ConfigureDbContext<OctopushyDb>(options => options.AddInterceptors(failure, failure.Saves)));
        var agent = await server.ConnectAgentAsync("home");
        server.Services.GetRequiredService<AgentConnections>().Disconnect(agent.Id);
        await server.SyncAndTrustAsync();
        var held = await ConnectHeldAtWelcomeAsync(server, agent, [TofuStateStore.AgentCapability]);
        await server.TickAsync();
        var preview = await PreviewAsync(server);
        Assert.Equal(AttemptStatuses.Assigned, preview.Status);
        var released = (await ReleasesAsync(server, preview.Id)).Select(r => r.Id).ToList();
        Assert.NotEmpty(released);

        // The session closes, and taking back what it never wrote fails: nothing is changed, and nothing is forgotten.
        failure.Armed = true;
        await held.DropAsync();
        Assert.False(failure.Armed);
        Assert.Equal(AttemptStatuses.Assigned, (await PreviewAsync(server)).Status);
        Assert.Equal(released, (await ReleasesAsync(server, preview.Id)).Select(r => r.Id).ToList());

        // The next pass does it.
        await server.TickAsync();
        preview = await PreviewAsync(server);
        Assert.Equal((AttemptStatuses.Queued, 1), (preview.Status, preview.Number));
        Assert.Empty(await ReleasesAsync(server, preview.Id));
        Assert.False(server.Services.GetRequiredService<RunEngine>().KeepsHandOff(preview.Id));
    }

    [Fact]
    public async Task A_change_put_back_has_its_plan_checked_again()
    {
        var (server, agent, _) = await ApprovedAsync();
        await using var __ = server;
        var held = await ConnectHeldAtWelcomeAsync(server, agent, [TofuStateStore.AgentCapability]);
        await server.TickAsync();
        await held.DropAsync();
        Assert.Equal(AttemptStatuses.Queued, Assert.Single(await AppliesAsync(server)).Status);
        var plan = await server.QueryAsync(db => db.Attempts.SingleAsync(a => a.Action == "preview" && a.Status == AttemptStatuses.Succeeded));
        await server.Services.GetRequiredService<ArtifactStore>().SaveAsync(plan.Id, AssignmentBuilder.PlanArtifact, "another plan"u8.ToArray(), CancellationToken.None);

        var back = await server.ConnectAgentAsync("home");
        await server.TickAsync();

        Assert.DoesNotContain(back.TakeAssignments(), a => a.Action == "apply");
        var apply = Assert.Single(await AppliesAsync(server));
        Assert.Equal(AttemptStatuses.Failed, apply.Status);
        Assert.Contains("does not match its recorded checksum", apply.Error);
    }

    [Theory]
    [InlineData("force stop")]
    [InlineData("lease")]
    public async Task A_change_settled_before_its_session_closed_is_not_put_back(string how)
    {
        var (server, agent, clock) = await ApprovedAsync();
        await using var __ = server;
        var held = await ConnectHeldAtWelcomeAsync(server, agent, [TofuStateStore.AgentCapability, AgentSession.ForcedMutationStopCapability]);
        await server.TickAsync();
        if (how == "force stop")
            await server.Engine.ForceStopAsync((await server.LatestRunAsync(Guest, Operations.Provision)).Id, "operator", CancellationToken.None);
        else
            await ExpireAsync(server, clock);
        Assert.Equal(AttemptStatuses.Uncertain, Assert.Single(await AppliesAsync(server)).Status);

        await held.DropAsync();

        Assert.Equal(AttemptStatuses.Uncertain, Assert.Single(await AppliesAsync(server)).Status);
        Assert.Equal(RunStatuses.Uncertain, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
    }

    [Fact]
    public async Task No_secret_release_or_reservation_is_kept_for_an_assignment_the_channel_refused()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        // The connection is gone, but the server has not noticed yet: the hand-off fails.
        agent.Session.Close();
        await server.TickAsync();

        var apply = Assert.Single(await AppliesAsync(server));
        Assert.Equal(AttemptStatuses.Queued, apply.Status);
        Assert.Empty(await ReleasesAsync(server, apply.Id));
        Assert.Null((await server.ResourceAsync(Guest)).ReservedAt);

        server.Services.GetRequiredService<AgentConnections>().Disconnect(agent.Id);
        var back = await server.ConnectAgentAsync("home");
        await back.NextAsync("apply");
        Assert.NotEmpty(await ReleasesAsync(server, apply.Id));
        Assert.NotNull((await server.ResourceAsync(Guest)).ReservedAt);
    }

    [Fact]
    public async Task The_only_agent_that_could_take_the_work_gets_it_without_waiting_for_evidence()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var solo = await server.ConnectProbedAgentAsync("solo", "podman", "home");
        await server.SyncAndTrustAsync();
        await server.TickAsync();

        Assert.Single(solo.TakeAssignments(), a => a.Action == "preview");
    }

    [Fact]
    public async Task Another_agent_that_could_take_the_work_keeps_the_wait_for_evidence_even_while_offline()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var solo = await server.ConnectProbedAgentAsync("solo", "podman", "home");
        await WorkerRoutingTests.OfflineSpareAsync(server);
        await server.SyncAndTrustAsync();
        await server.TickAsync();

        Assert.Empty(solo.TakeAssignments());
        Assert.NotEmpty(solo.TakeProbes());
        Assert.Contains("fresh evidence", (await PreviewAsync(server)).WaitingReason);
    }

    private sealed class HeldConnection(Task connection, TaskCompletionSource welcome)
    {
        /// <summary>Ends the connection by failing the write of the welcome, before the writer ever ran.</summary>
        public async Task DropAsync()
        {
            welcome.SetException(new IOException("the connection dropped"));
            await connection.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>A connection registered for dispatch whose welcome is still being written, so nothing queued is written yet.</summary>
    private static async Task<HeldConnection> ConnectHeldAtWelcomeAsync(ServerHarness server, FakeAgent agent, string[] capabilities)
    {
        var welcome = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new AgentStream { OnWrite = m => m.Welcome is not null ? Block(entered, welcome) : throw new InvalidOperationException("written") };
        stream.Send(new AgentMessage { Hello = Hello(capabilities) });
        var service = ActivatorUtilities.CreateInstance<AgentChannelService>(server.Services);
        var connection = service.Connect(stream, stream, await AgentStream.CallAsAsync(server, agent));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(server.Services.GetRequiredService<AgentConnections>().Get(agent.Id));
        return new HeldConnection(connection, welcome);
    }

    /// <summary>Holds the save by which a Hello queues a read again, once, until released.</summary>
    private sealed class PauseRequeue : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<Attempt>()
                    .Any(e => e.State == EntityState.Modified && e.Entity.Status == AttemptStatuses.Queued))
            {
                Armed = false;
                Entered.TrySetResult();
                await Release.Task;
            }
            return result;
        }
    }

    /// <summary>Reports the commit of a Hello's transaction as failed, once, after it went through.</summary>
    private sealed class FailsAfterCommit : DbTransactionInterceptor
    {
        public bool Armed { get; set; }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (!Armed || !eventData.Context!.ChangeTracker.Entries<Attempt>().Any())
                return Task.CompletedTask;
            Armed = false;
            throw new InvalidOperationException("The connection dropped before the commit was acknowledged.");
        }
    }

    /// <summary>
    /// Fails, once, the taking back of what a hand-off recorded: the read of its release rows ("query"), or the save that
    /// removes them ("save").
    /// </summary>
    private sealed class TakeBackFails(string what) : DbCommandInterceptor
    {
        public bool Armed { get; set; }

        private string What { get; } = what;

        public SaveChangesInterceptor Saves => new FailingSave(this);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Armed || What != "query" || !command.CommandText.Contains("FROM secret_resolutions"))
                return ValueTask.FromResult(result);
            Armed = false;
            throw new InvalidOperationException("The release rows could not be read.");
        }

        private sealed class FailingSave(TakeBackFails owner) : SaveChangesInterceptor
        {
            public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
                DbContextEventData eventData,
                InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                if (!owner.Armed || owner.What != "save"
                    || !eventData.Context!.ChangeTracker.Entries<SecretResolution>().Any(e => e.State == EntityState.Deleted))
                    return ValueTask.FromResult(result);
                owner.Armed = false;
                throw new InvalidOperationException("The taking back could not be saved.");
            }
        }
    }

    /// <summary>Has the engine look for hand-off records of no more use at every pass, and not once in so many hand-offs.</summary>
    private static RunEngine LookEveryPass(ServerHarness server)
    {
        var engine = server.Services.GetRequiredService<RunEngine>();
        engine.HandOffsBetweenPrunes = 0;
        return engine;
    }

    private static Task Block(TaskCompletionSource entered, TaskCompletionSource release)
    {
        entered.TrySetResult();
        return release.Task;
    }

    /// <summary>A provision run whose plan was approved, its apply about to be dispatched, and its agent disconnected.</summary>
    private async Task<(ServerHarness Server, FakeAgent Agent, TestClock Clock)> ApprovedAsync()
    {
        var clock = new TestClock();
        var server = await ServerHarness.StartAsync(postgres, clock: clock);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        server.Services.GetRequiredService<AgentConnections>().Disconnect(agent.Id);
        return (server, agent, clock);
    }

    private static async Task<Task> ConnectAsync(ServerHarness server, FakeAgent agent, AgentStream stream, Hello hello)
    {
        stream.Send(new AgentMessage { Hello = hello });
        var service = ActivatorUtilities.CreateInstance<AgentChannelService>(server.Services);
        var connection = service.Connect(stream, stream, await AgentStream.CallAsAsync(server, agent));
        await stream.ReadingMessages.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return connection;
    }

    private static Hello Hello(string[] capabilities) => new()
    {
        AgentVersion = "test",
        Executors = { "opentofu", "proxmox", "ansible" },
        Capabilities = { capabilities },
    };

    /// <summary>The next assignment the session's writer wrote.</summary>
    private static async Task<Assignment> NextAssignmentAsync(AgentStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
            if ((await stream.Sent.Reader.ReadAsync(timeout.Token)).Assignment is { Action: "apply" or "preview" } assignment)
                return assignment;
    }

    private static List<ServerMessage> Drain(AgentStream stream)
    {
        var messages = new List<ServerMessage>();
        while (stream.Sent.Reader.TryRead(out var message))
            messages.Add(message);
        return messages;
    }

    private static Task<List<Attempt>> AppliesAsync(ServerHarness server) =>
        server.QueryAsync(db => db.Attempts.Where(a => a.Action == "apply").OrderBy(a => a.Number).ToListAsync());

    private static Task<Attempt> PreviewAsync(ServerHarness server) =>
        server.QueryAsync(db => db.Attempts.SingleAsync(a => a.Action == "preview"));

    private static Task<Step> ApplyStepAsync(ServerHarness server) =>
        server.QueryAsync(db => db.Steps.SingleAsync(s => s.Name == StepNames.Apply));

    private static Task<List<SecretResolution>> ReleasesAsync(ServerHarness server, Guid attemptId) =>
        server.QueryAsync(db => db.SecretResolutions.Where(r => r.AttemptId == attemptId).ToListAsync());

    private static async Task ExpireAsync(ServerHarness server, TestClock clock)
    {
        clock.Now = DateTimeOffset.UtcNow.AddMinutes(5);
        await server.TickAsync();
    }
}

/// <summary>Where a session's writer draws the line between a message whose write began and one never written.</summary>
public class AgentSessionWriteTests
{
    private static ServerMessage Assignment(Guid id) => new() { Assignment = new Assignment { AttemptId = id.ToString(), Action = "apply" } };

    [Fact]
    public async Task A_message_whose_write_began_is_never_reported_unwritten_and_what_follows_after_the_close_is()
    {
        var session = new AgentSession(Guid.NewGuid(), "a", []);
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        Assert.True(session.TrySend(Assignment(first)));
        Assert.True(session.TrySend(Assignment(second)));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = new List<string>();
        var writer = session.WriteAllAsync(async m =>
        {
            written.Add(m.Assignment.AttemptId);
            entered.TrySetResult();
            await release.Task;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Open, nothing is reported; the write in progress has begun, and the one queued behind it has not.
        Assert.Empty(session.TakeUnwritten());
        Assert.True(session.WriteBegan(first));
        Assert.False(session.WriteBegan(second));
        session.Close();
        release.SetResult();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([first.ToString()], written);
        Assert.Equal([second], session.TakeUnwritten());
        Assert.Empty(session.TakeUnwritten());
        // Never written stays so, also once reported.
        Assert.False(session.WriteBegan(second));
    }

    [Fact]
    public async Task A_cancelled_call_writes_nothing_more_and_reports_it_once_closed()
    {
        var session = new AgentSession(Guid.NewGuid(), "a", []);
        var attempt = Guid.NewGuid();
        Assert.True(session.TrySend(Assignment(attempt)));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var written = 0;

        // The cancellation is seen before the write: either the wait for a message throws, or the message is set aside.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.WriteAllAsync(_ => { written++; return Task.CompletedTask; }, cancelled.Token));
        Assert.Equal(0, written);

        Assert.Empty(session.TakeUnwritten());
        session.Close();
        Assert.Equal([attempt], session.TakeUnwritten());
    }

    [Fact]
    public async Task A_message_taken_after_the_cancellation_is_set_aside_unwritten()
    {
        var session = new AgentSession(Guid.NewGuid(), "a", []);
        var attempt = Guid.NewGuid();
        using var call = new CancellationTokenSource();
        var written = new List<string>();
        // The first write cancels the call, as a stream that fails would; the next message is taken and never written.
        Assert.True(session.TrySend(new ServerMessage { ResultAck = new ResultAck { AttemptId = "x" } }));
        Assert.True(session.TrySend(Assignment(attempt)));
        await session.WriteAllAsync(async m =>
        {
            written.Add(m.KindCase.ToString());
            await call.CancelAsync();
        }, call.Token);

        Assert.Equal(["ResultAck"], written);
        session.Close();
        Assert.Equal([attempt], session.TakeUnwritten());
    }
}
