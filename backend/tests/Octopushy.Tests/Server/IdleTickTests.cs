using System.Data.Common;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Xunit.Abstractions;

namespace Octopushy.Tests.Server;

/// <summary>What a tick costs when nothing happens: runs that only wait, and configured guests that follow no store value.</summary>
public class IdleTickTests(PostgresFixture postgres, ITestOutputHelper output) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Counts the SQL commands every context of the server sends.</summary>
    internal sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }

        public async Task<int> CountAsync(Func<Task> action)
        {
            var before = Count;
            await action();
            return Count - before;
        }
    }

    private static Task<ServerHarness> StartAsync(PostgresFixture postgres, TestRepository repository, CommandCounter counter, TestClock clock) =>
        ServerHarness.StartAsync(postgres, repository, clock: clock,
            services: s => s.ConfigureDbContext<OctopushyDb>(o => o.AddInterceptors(counter)));

    /// <summary>Adds copies of a waiting run, with its steps and none of its attempts.</summary>
    private static Task CopyAsync(ServerHarness server, Guid runId, int copies) =>
        server.WithDbAsync(async db =>
        {
            for (var i = 0; i < copies; i++)
            {
                var run = await db.Runs.AsNoTracking().Include(r => r.Steps).SingleAsync(r => r.Id == runId);
                run.Id = Guid.CreateVersion7();
                run.Number = 0;
                run.CreatedAt = run.CreatedAt.AddMilliseconds(i + 1);
                foreach (var step in run.Steps)
                {
                    step.Id = Guid.CreateVersion7();
                    step.RunId = run.Id;
                    step.Attempts = [];
                }

                db.Runs.Add(run);
            }

            await db.SaveChangesAsync();
        });

    private static Task<List<(Guid Run, Guid Step)>> WaitingAsync(ServerHarness server) =>
        server.QueryAsync(async db => (await db.Steps.AsNoTracking()
            .Where(s => s.Status == StepStatuses.AwaitingApproval)
            .Select(s => new { s.RunId, s.Id }).ToListAsync()).Select(s => (s.RunId, s.Id)).ToList());

    /// <summary>A tick after the one that settles what changed: the cost of a tick that has nothing to do.</summary>
    private static async Task<int> IdleTickAsync(ServerHarness server, CommandCounter counter)
    {
        await server.TickAsync();
        return await counter.CountAsync(server.TickAsync);
    }

    private static TestRepository ManualApproval()
    {
        var repository = TestRepository.Minimal();
        repository.Write("labs/home/lab.yaml", "schemaVersion: 1\nexecution: { agentLabels: [home] }\npolicy: { approval: manual }\n");
        return repository;
    }

    /// <summary>One provision run that waits for approval, as a plan with an update leaves it.</summary>
    private static async Task<(Run Run, FakeAgent Agent, string PlanSha256)> WaitingRunAsync(ServerHarness server)
    {
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var artifact = FakeAgent.PlanArtifact();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: artifact);
        await server.TickAsync();
        var run = await server.LatestRunAsync("home/korriban/krb-app-01", Operations.Provision);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        return (run, agent, artifact.Sha256);
    }

    /// <summary>The commands of an idle tick with no run, one, 5 and 20 runs waiting for approval.</summary>
    private async Task<int[]> IdleTicksByWaitingRunsAsync(ServerHarness server, CommandCounter counter, Run run)
    {
        var one = await IdleTickAsync(server, counter);
        await CopyAsync(server, run.Id, 4);
        var five = await IdleTickAsync(server, counter);
        await CopyAsync(server, run.Id, 15);
        var twenty = await IdleTickAsync(server, counter);
        Assert.Equal(20, (await WaitingAsync(server)).Count);
        foreach (var (runId, stepId) in await WaitingAsync(server))
            await server.Engine.RejectAsync(runId, stepId, "ops", null, CancellationToken.None);
        var none = await IdleTickAsync(server, counter);
        output.WriteLine($"idle tick commands by waiting runs: 0 -> {none}, 1 -> {one}, 5 -> {five}, 20 -> {twenty}");
        return [none, one, five, twenty];
    }

    [Fact]
    public async Task An_idle_tick_costs_the_same_however_many_runs_wait_for_approval()
    {
        var counter = new CommandCounter();
        using var repository = ManualApproval();
        await using var server = await StartAsync(postgres, repository, counter, new TestClock { Now = Start });
        var (run, _, _) = await WaitingRunAsync(server);

        var counts = await IdleTicksByWaitingRunsAsync(server, counter, run);
        Assert.Single(counts.Distinct());
    }

    [Fact]
    public async Task The_pass_every_minute_advances_every_waiting_run_and_costs_by_run()
    {
        var counter = new CommandCounter();
        var clock = new TestClock { Now = Start };
        using var repository = ManualApproval();
        await using var server = await StartAsync(postgres, repository, counter, clock);
        var (run, _, _) = await WaitingRunAsync(server);
        await CopyAsync(server, run.Id, 4);
        await IdleTickAsync(server, counter);

        var idle = await counter.CountAsync(server.TickAsync);
        clock.Now = Start + RunEngine.FullPass;
        var full = await counter.CountAsync(server.TickAsync);
        // Each of the five runs is loaded and asked again; nothing is written.
        Assert.True(full >= idle + 5 * 4, $"idle {idle}, full {full}");
        Assert.Equal(idle, await counter.CountAsync(server.TickAsync));
    }

    [Fact]
    public async Task Heartbeats_with_disk_figures_add_no_write_and_leave_an_idle_tick_as_it_was()
    {
        var counter = new CommandCounter();
        using var repository = ManualApproval();
        await using var server = await StartAsync(postgres, repository, counter, new TestClock { Now = Start });
        var (run, agent, _) = await WaitingRunAsync(server);
        await CopyAsync(server, run.Id, 4);
        await IdleTickAsync(server, counter);

        // Agents connected over the stream, each sending heartbeats with and without its room, as an agent does.
        var connections = server.Services.GetRequiredService<Octopushy.Server.Agents.AgentConnections>();
        connections.Disconnect(agent.Id);
        var stream = new AgentStream();
        stream.Send(new AgentMessage { Hello = new Hello { AgentVersion = "test", Executors = { "opentofu", "proxmox", "ansible" } } });
        var service = ActivatorUtilities.CreateInstance<Octopushy.Server.Agents.AgentChannelService>(server.Services);
        _ = service.Connect(stream, stream, await AgentStream.CallAsAsync(server, agent));
        await stream.ReadingMessages.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await IdleTickAsync(server, counter);
        var idle = await counter.CountAsync(server.TickAsync);
        var writes = server.Services.GetRequiredService<SavedWrites>();
        var saved = writes.Count;
        var session = connections.Get(agent.Id)!;
        async Task<int> HeartbeatAsync(StorageReport? storage)
        {
            var before = counter.Count;
            var seen = session.Storage;
            stream.Send(new AgentMessage { Heartbeat = new Heartbeat { Storage = storage } });
            stream.Send(new AgentMessage { Heartbeat = new Heartbeat() });
            for (var i = 0; i < 200 && counter.Count < before + 4; i++)
                await Task.Delay(25);
            Assert.True(storage is null || session.Storage != seen, "the room was not kept");
            return counter.Count - before;
        }

        var plain = await HeartbeatAsync(null);
        var withRoom = await HeartbeatAsync(new StorageReport
        {
            Free = { new FreeSpace { Name = "state", FreeBytes = 1 << 30, TotalBytes = 4L << 30 } },
            Caches = { new CacheSize { Name = "providers", Bytes = 1 << 20, Complete = true } },
        });
        output.WriteLine($"heartbeat pair commands: plain {plain}, with room {withRoom}; idle tick {idle}");
        Assert.Equal(plain, withRoom);
        Assert.Equal(saved, writes.Count);
        Assert.Equal(idle, await counter.CountAsync(server.TickAsync));
        stream.Complete();
    }

    /// <summary>Approves a waiting run's step behind the engine's back, as a save it is not told of would.</summary>
    private static Task ApproveUnseenAsync(ServerHarness server, Guid runId) =>
        server.WithDbAsync(db => db.Steps.Where(s => s.RunId == runId && s.Status == StepStatuses.AwaitingApproval)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, StepStatuses.Succeeded).SetProperty(s => s.Decision, "approved")));

    [Fact]
    public async Task An_approval_through_the_API_takes_effect_on_the_next_tick()
    {
        using var repository = ManualApproval();
        await using var server = await StartAsync(postgres, repository, new CommandCounter(), new TestClock { Now = Start });
        var (run, agent, sha) = await WaitingRunAsync(server);
        await server.TickAsync();
        await server.TickAsync();
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);

        var http = await server.SignedInClientAsync();
        (await http.PostAsJsonAsync($"/api/runs/{run.Id}/steps/{approval.Id}/approve", new { planSha256 = sha })).EnsureSuccessStatusCode();
        await server.TickAsync();

        Assert.Contains(agent.TakeAssignments(), a => a.Action == "apply");
        Assert.Equal(RunStatuses.Running, (await server.LatestRunAsync("home/korriban/krb-app-01", Operations.Provision)).Status);
    }

    [Fact]
    public async Task Trusting_the_installation_through_the_API_starts_its_waiting_run_on_the_next_tick()
    {
        await using var server = await StartAsync(postgres, TestRepository.Minimal(), new CommandCounter(), new TestClock { Now = Start });
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAsync();
        await server.TickAsync();
        await server.TickAsync();
        Assert.Equal(RunStatuses.Blocked, (await server.LatestRunAsync("home/korriban/krb-app-01", Operations.Provision)).Status);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "preview");

        await server.SyncAndTrustAsync();
        await server.TickAsync();

        Assert.Contains(agent.TakeAssignments(), a => a.Action == "preview");
    }

    [Fact]
    public async Task A_waiting_run_moves_on_the_pass_every_minute_when_nothing_said_it_changed()
    {
        var clock = new TestClock { Now = Start };
        using var repository = ManualApproval();
        await using var server = await StartAsync(postgres, repository, new CommandCounter(), clock);
        var (run, agent, _) = await WaitingRunAsync(server);
        await server.TickAsync();

        await ApproveUnseenAsync(server, run.Id);
        await server.TickAsync();
        Assert.Equal(RunStatuses.AwaitingApproval, (await server.LatestRunAsync("home/korriban/krb-app-01", Operations.Provision)).Status);

        clock.Now = Start + RunEngine.FullPass;
        await server.TickAsync();
        Assert.Contains(agent.TakeAssignments(), a => a.Action == "apply");
    }

    [Fact]
    public async Task After_a_restart_every_waiting_run_is_advanced_once()
    {
        using var repository = ManualApproval();
        await using var server = await StartAsync(postgres, repository, new CommandCounter(), new TestClock { Now = Start });
        var (run, _, _) = await WaitingRunAsync(server);
        await server.TickAsync();
        await ApproveUnseenAsync(server, run.Id);

        await server.RestartFixtureAsync();
        await server.TickAsync();

        var advanced = await server.LatestRunAsync("home/korriban/krb-app-01", Operations.Provision);
        Assert.Equal(RunStatuses.Running, advanced.Status);
        Assert.Single(advanced.Steps.Single(s => s.Name == StepNames.Apply).Attempts);
    }

    private const string Workflow = """
        schemaVersion: 1
        version: "1.0"
        manages: vm
        inputs: inputs.schema.json
        stages:
          - name: machine
            executor: opentofu
            root: ../../modules/opentofu/test-vm
          - name: setup
            executor: ansible
            root: ../../modules/ansible/app
            playbook: site.yml
        """;

    /// <summary>Guests whose workflow sets them up, that name their own login key and follow no store value.</summary>
    private static TestRepository ConfiguredGuests(int count)
    {
        var repository = TestRepository.Minimal();
        repository.Write("workflows/app/workflow.yaml", Workflow);
        repository.Write("workflows/app/inputs.schema.json", """{ "type": "object" }""");
        repository.Write("modules/ansible/app/site.yml", "- hosts: all\n");
        for (var i = 0; i < count; i++)
            repository.Write($"labs/home/proxmox/korriban/guests/krb-cfg-{i:00}/guest.yaml", $$"""
                schemaVersion: 1
                type: vm
                host: korriban
                vmid: {{400 + i}}
                hardware: { cores: 1, memory: 1024 }
                network: { bridge: vmbr0, mode: static, address: 10.1.1.{{10 + i}}/24, gateway: 10.1.1.1 }
                workflow: app@1
                access:
                  user: ops
                  sshKey: { store: access/app/ssh-key }
                  hostKey: { store: access/app/host-key }
                """);
        return repository;
    }

    /// <summary>
    /// Makes guests provisioned and configured as the scheduler sees them: bound, caught up, their provision run succeeded,
    /// their setup proposed for its current inputs and its followed values recorded.
    /// </summary>
    private static Task ConfigureAsync(ServerHarness server, IEnumerable<string> addresses) =>
        server.WithDbAsync(async db =>
        {
            foreach (var address in addresses)
            {
                var resource = await db.Resources.SingleAsync(r => r.Address == address && r.RetiredAt == null);
                resource.BoundInstallation = resource.ParentAddress;
                resource.BoundVmid = int.Parse(address[^2..]) + 400;
                resource.SyncedInputsHash = resource.DesiredInputsHash;
                foreach (var run in await db.Runs.Include(r => r.Steps).ThenInclude(s => s.Attempts).Where(r => r.ResourceId == resource.Id).ToListAsync())
                {
                    run.Status = RunStatuses.Succeeded;
                    foreach (var attempt in run.Steps.SelectMany(s => s.Attempts).Where(a => !AttemptStatuses.IsTerminal(a.Status)))
                        attempt.Status = AttemptStatuses.Cancelled;
                }

                var configuration = await db.GuestConfigurations.SingleAsync(c => c.ResourceId == resource.Id);
                Assert.NotNull(configuration.DesiredHash);
                configuration.ProposedHash = configuration.DesiredHash;
                configuration.SecretVersions = "{}";
            }

            await db.SaveChangesAsync();
        });

    [Fact]
    public async Task Configure_scheduling_costs_the_same_however_many_configured_guests_follow_no_store_value()
    {
        const int guests = 20;
        var counter = new CommandCounter();
        using var repository = ConfiguredGuests(guests);
        await using var server = await StartAsync(postgres, repository, counter, new TestClock { Now = Start });
        await server.SetSecretAsync("access/app/ssh-key", "ssh-key");
        await server.SetPlainAsync("access/app/host-key", "ssh-ed25519 AAAA host");
        await server.SyncAndTrustAsync();
        var planner = server.Services.GetRequiredService<Planner>();
        var addresses = Enumerable.Range(0, guests).Select(i => $"home/korriban/krb-cfg-{i:00}").ToList();

        async Task<int> ScheduleAsync()
        {
            await planner.ScheduleConfigurationsAsync(CancellationToken.None);
            return await counter.CountAsync(() => planner.ScheduleConfigurationsAsync(CancellationToken.None));
        }

        var none = await ScheduleAsync();
        await ConfigureAsync(server, addresses.Take(5));
        var five = await ScheduleAsync();
        await ConfigureAsync(server, addresses.Skip(5));
        var twenty = await ScheduleAsync();
        Assert.Equal(0, await server.QueryAsync(db => db.Runs.CountAsync(r => r.Operation == Operations.Configure)));

        output.WriteLine($"configure scheduling commands by configured guests: 0 -> {none}, 5 -> {five}, 20 -> {twenty}");
        // Reading which guests are due and which are followed, once each, is all; none of the guests' runs is read.
        Assert.Equal(five, twenty);
    }
}
