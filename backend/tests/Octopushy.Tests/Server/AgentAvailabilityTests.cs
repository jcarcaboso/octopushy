using System.Data.Common;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>Why an agent cannot take work: its unavailable executors with their reasons, and the room it reports.</summary>
public class AgentAvailabilityTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Guest = "home/korriban/krb-app-01";
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Holds the first assignment build at its check of the agent, until released.</summary>
    private sealed class BuildPause : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task HoldAsync(DbCommand command)
        {
            // AssignmentBuilder asks whether the chosen agent is disabled or draining; dispatch's own agent query has no EXISTS.
            if (!Armed || !command.CommandText.Contains("EXISTS") || !command.CommandText.Contains("draining"))
                return;
            Armed = false;
            Entered.SetResult();
            await Release.Task;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await HoldAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            await HoldAsync(command);
            return result;
        }
    }

    /// <summary>Counts the engine's "now offers" lines.</summary>
    private sealed class OfferLogs : ILoggerProvider, ILogger
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public ILogger CreateLogger(string categoryName) =>
            categoryName == typeof(RunEngine).FullName ? this : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains(" now offers "))
                Interlocked.Increment(ref _count);
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Connects <paramref name="agent"/> again over a stream, as a real agent does, with its Hello.</summary>
    private static async Task<(AgentStream Stream, AgentSession Session)> StreamAsync(ServerHarness server, FakeAgent agent, Hello hello)
    {
        var connections = server.Services.GetRequiredService<AgentConnections>();
        connections.Disconnect(agent.Id);
        var stream = new AgentStream();
        stream.Send(new AgentMessage { Hello = hello });
        var service = ActivatorUtilities.CreateInstance<AgentChannelService>(server.Services);
        _ = service.Connect(stream, stream, await AgentStream.CallAsAsync(server, agent));
        await stream.ReadingMessages.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return (stream, connections.Get(agent.Id)!);
    }

    private static Task<Attempt> PreviewAsync(ServerHarness server) =>
        server.QueryAsync(db => db.Attempts.AsNoTracking().Where(a => a.Action == "preview").OrderByDescending(a => a.CreatedAt).FirstAsync());

    private static async Task WaitUntilAsync(string what, Func<Task<bool>> condition, Func<Task<string?>>? explain = null)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition())
                return;
            await Task.Delay(25);
        }

        Assert.Fail($"Timed out waiting until {what}. {(explain is null ? "" : await explain())}");
    }

    [Fact]
    public void A_reason_is_one_line_without_control_characters_and_bounded()
    {
        Assert.Equal("cannot run tofu: [31mgone ok", AgentAvailability.Clean("  cannot run tofu:\r\n\t\u001b[31mgone‮ ok\u0000 "));
        var cleaned = AgentAvailability.Clean(new string('é', 5000));
        Assert.Equal(AgentAvailability.MaxReasonChars, cleaned.Length);
        Assert.EndsWith("…", cleaned);
        Assert.Equal("", AgentAvailability.Clean(null));

        var kept = AgentAvailability.Unavailable(new Dictionary<string, string>
        {
            ["ansible"] = "missing\nprogram", ["<script>"] = "x", ["Ansible"] = "case matters",
        });
        Assert.Equal(["ansible"], kept.Keys);
        Assert.Equal("missing program", kept["ansible"]);
    }

    [Fact]
    public async Task A_run_waiting_for_an_executor_names_the_agents_without_it_and_why_and_is_not_rewritten_while_nothing_changes()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.ConnectAgentLackingAsync("bravo", new Dictionary<string, string> { ["opentofu"] = "cannot run podman: No such file" }, "home");
        await server.ConnectAgentLackingAsync("alpha", new Dictionary<string, string> { ["opentofu"] = "podman --version exited with 125" }, "home");
        await server.ConnectOlderAgentAsync("elsewhere");
        await server.SyncAndTrustAsync();
        await server.TickAsync();

        var waiting = (await PreviewAsync(server)).WaitingReason;
        Assert.Equal("Waiting for a connected agent advertising executor opentofu with the required labels and built-in OpenTofu state; "
                     + "update the agent if it is older. Without opentofu: alpha: podman --version exited with 125; bravo: cannot run podman: No such file.",
            waiting);

        // The same agents give the same text: a tick saves nothing, so the engine is not woken by it.
        var writes = server.Services.GetRequiredService<SavedWrites>();
        await server.TickAsync();
        var before = writes.Count;
        await server.TickAsync();
        Assert.Equal(before, writes.Count);
        Assert.Equal(waiting, (await PreviewAsync(server)).WaitingReason);
    }

    [Fact]
    public async Task A_waiting_text_names_at_most_five_agents()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        for (var i = 0; i < 7; i++)
            await server.ConnectAgentLackingAsync($"agent-{i}", new Dictionary<string, string> { ["opentofu"] = new('x', 300) }, "home");
        await server.SyncAndTrustAsync();
        await server.TickAsync();

        var waiting = (await PreviewAsync(server)).WaitingReason!;
        Assert.Contains("agent-4: ", waiting);
        Assert.DoesNotContain("agent-5: ", waiting);
        Assert.EndsWith("; and 2 more.", waiting);
        Assert.True(waiting.Length < 2000, waiting.Length.ToString());
    }

    [Fact]
    public async Task An_agent_that_loses_an_executor_stops_being_offered_its_work_and_gets_it_again_without_reconnecting()
    {
        var clock = new TestClock { Now = Start };
        await using var server = await ServerHarness.StartAsync(postgres, clock: clock);
        var agent = await server.ConnectAgentAsync("home");
        await server.Engine.OnExecutorsChangedAsync(agent.Session, new ExecutorsChanged
        {
            Executors = { "proxmox", "ansible" },
            Unavailable = { ["opentofu"] = "cannot run podman" },
        }, CancellationToken.None);
        await server.SyncAndTrustAsync();
        await server.TickAsync();

        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Executor == "opentofu");
        Assert.EndsWith("Without opentofu: fake: cannot run podman.", (await PreviewAsync(server)).WaitingReason);
        var record = await server.QueryAsync(db => db.Agents.AsNoTracking().SingleAsync(a => a.Id == agent.Id));
        Assert.Equal(["ansible", "proxmox"], record.Executors);
        Assert.Equal("cannot run podman", AgentAvailability.Deserialize(record.Unavailable)["opentofu"]);

        // Past the interval, so the change is stored too, not only applied.
        clock.Now += RunEngine.OfferEffectsEvery;
        await server.Engine.OnExecutorsChangedAsync(agent.Session, new ExecutorsChanged { Executors = { "opentofu", "proxmox", "ansible" } },
            CancellationToken.None);
        await server.TickAsync();

        Assert.Same(agent.Session, server.Services.GetRequiredService<AgentConnections>().Get(agent.Id));
        Assert.Contains(agent.TakeAssignments(), a => a.Action == "preview");
        Assert.Null(await server.QueryAsync(db => db.Agents.Where(a => a.Id == agent.Id).Select(a => a.Unavailable).SingleAsync()));
    }

    [Fact]
    public async Task Reasons_and_room_arrive_on_the_stream_and_reach_dispatch_and_the_agents_api()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        server.Services.GetRequiredService<AgentConnections>().Disconnect(agent.Id);
        var stream = new AgentStream();
        var hostile = "cannot run tofu\u001b[2J\r\n<b>bold</b>" + new string('z', 1000);
        stream.Send(new AgentMessage
        {
            Hello = new Hello
            {
                AgentVersion = "test", Executors = { "proxmox", "ansible" }, Capabilities = { "opentofu-builtin-state", WorkerReachability.Capability },
                Unavailable = { ["opentofu"] = hostile, ["made-up"] = "dropped" },
            },
        });
        var service = ActivatorUtilities.CreateInstance<AgentChannelService>(server.Services);
        _ = service.Connect(stream, stream, await AgentStream.CallAsAsync(server, agent));
        await stream.ReadingMessages.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var connections = server.Services.GetRequiredService<AgentConnections>();
        var session = connections.Get(agent.Id)!;
        stream.Send(new AgentMessage
        {
            Heartbeat = new Heartbeat
            {
                Storage = new StorageReport
                {
                    Free = { new FreeSpace { Name = "state", FreeBytes = 5L << 30, TotalBytes = 20L << 30 }, new FreeSpace { Name = "/etc", FreeBytes = 1 } },
                    Caches = { new CacheSize { Name = "providers", Bytes = ulong.MaxValue, Complete = false } },
                },
            },
        });
        await WaitUntilAsync("the room is kept", () => Task.FromResult(session.Storage is not null));

        var client = await server.SignedInClientAsync();
        var shown = (await client.GetFromJsonAsync<JsonArray>("/api/agents"))!.Single(a => a!["id"]!.GetValue<Guid>() == agent.Id)!;
        var reason = shown["unavailable"]!["opentofu"]!.GetValue<string>();
        Assert.StartsWith("cannot run tofu [2J <b>bold</b>zzz", reason);
        Assert.Equal(AgentAvailability.MaxReasonChars, reason.Length);
        Assert.Single(shown["unavailable"]!.AsObject());
        var free = shown["storage"]!["free"]!.AsArray().Single()!;
        Assert.Equal(("state", 5L << 30), (free["name"]!.GetValue<string>(), free["freeBytes"]!.GetValue<long>()));
        var cache = shown["storage"]!["caches"]!.AsArray().Single()!;
        Assert.Equal((long.MaxValue, false), (cache["bytes"]!.GetValue<long>(), cache["complete"]!.GetValue<bool>()));

        // A re-probe finds the runtime back: the open stream says so, and the work goes to it.
        await server.SyncAndTrustAsync();
        await server.TickAsync();
        Assert.Contains("Without opentofu: fake: cannot run tofu", (await PreviewAsync(server)).WaitingReason);
        stream.Send(new AgentMessage { ExecutorsChanged = new ExecutorsChanged { Executors = { "opentofu", "proxmox", "ansible" } } });
        await WaitUntilAsync("the change is applied", () => Task.FromResult(session.Executors.Contains("opentofu")));
        await server.TickAsync();
        Assert.Same(session, connections.Get(agent.Id));
        // The new connection has no reachability evidence yet: it answers the probes as the agent would.
        var previewSent = false;
        await WaitUntilAsync("the preview is sent", async () =>
        {
            while (stream.Sent.Reader.TryRead(out var sent))
            {
                previewSent |= sent.Assignment?.Action == "preview";
                if (sent.Probe is { } probe)
                    stream.Send(new AgentMessage { Reachability = FakeAgent.ReachEverything(probe) });
            }

            await server.TickAsync();
            return previewSent;
        }, () => PreviewAsync(server).ContinueWith(t => t.Result.WaitingReason));
        stream.Complete();
    }

    [Fact]
    public async Task An_older_agent_reports_neither_reasons_nor_room_and_the_api_shows_nothing_for_them()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectOlderAgentAsync("home");
        server.Services.GetRequiredService<AgentConnections>().Disconnect(agent.Id);
        var stream = new AgentStream();
        stream.Send(new AgentMessage { Hello = new Hello { AgentVersion = "0.9.0", Executors = { "proxmox" } } });
        var service = ActivatorUtilities.CreateInstance<AgentChannelService>(server.Services);
        var connected = service.Connect(stream, stream, await AgentStream.CallAsAsync(server, agent));
        await stream.ReadingMessages.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stream.Send(new AgentMessage { Heartbeat = new Heartbeat() });

        var client = await server.SignedInClientAsync();
        var shown = (await client.GetFromJsonAsync<JsonArray>("/api/agents"))!.Single(a => a!["id"]!.GetValue<Guid>() == agent.Id)!;
        Assert.True(shown["connected"]!.GetValue<bool>());
        Assert.Empty(shown["unavailable"]!.AsObject());
        Assert.Null(shown["storage"]);
        Assert.Null(await server.QueryAsync(db => db.Agents.Where(a => a.Id == agent.Id).Select(a => a.Unavailable).SingleAsync()));

        // Offline: what it said last about executors stays; its room, never a stale figure, is not shown.
        stream.Complete();
        await connected.WaitAsync(TimeSpan.FromSeconds(10));
        shown = (await client.GetFromJsonAsync<JsonArray>("/api/agents"))!.Single(a => a!["id"]!.GetValue<Guid>() == agent.Id)!;
        Assert.False(shown["connected"]!.GetValue<bool>());
        Assert.Null(shown["storage"]);
    }

    [Fact]
    public void An_offer_is_a_set()
    {
        Assert.Equal(["ansible", "opentofu", "proxmox"], AgentAvailability.Executors(["proxmox", "opentofu", "ansible", "opentofu", ""]));
        var session = new AgentSession(Guid.NewGuid(), "a", AgentAvailability.Executors(["opentofu", "proxmox"]));
        Assert.False(session.SetExecutors(AgentAvailability.Executors(["proxmox", "opentofu", "proxmox"]), new SortedDictionary<string, string>()));
        Assert.True(session.SetExecutors(AgentAvailability.Executors(["proxmox"]), new SortedDictionary<string, string>()));
    }

    [Fact]
    public async Task Removing_an_executor_waits_for_a_dispatch_that_is_building_its_assignment()
    {
        var pause = new BuildPause();
        await using var server = await ServerHarness.StartAsync(postgres,
            services: s => s.ConfigureDbContext<OctopushyDb>(o => o.AddInterceptors(pause)));
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();

        pause.Armed = true;
        var tick = server.TickAsync();
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Dispatch has chosen this agent for its opentofu preview and is building the assignment, holding the engine's gate.
        List<Assignment>? sentBeforeChange = null;
        var change = server.Engine.OnExecutorsChangedAsync(agent.Session, new ExecutorsChanged
        {
            Executors = { "proxmox", "ansible" },
            Unavailable = { ["opentofu"] = "cannot run podman" },
        }, CancellationToken.None).ContinueWith(t =>
        {
            t.GetAwaiter().GetResult();
            // What had been handed to the agent when the change was applied: the preview, if it came first.
            sentBeforeChange = agent.TakeAssignments();
        }, TaskContinuationOptions.ExecuteSynchronously);
        await Task.Delay(300);
        Assert.False(change.IsCompleted, "The change was applied while dispatch held the gate.");
        Assert.Contains("opentofu", agent.Session.Executors);

        pause.Release.SetResult();
        await tick.WaitAsync(TimeSpan.FromSeconds(30));
        await change.WaitAsync(TimeSpan.FromSeconds(30));

        // Handed over while the agent still offered opentofu, then the change: never the change, then the handoff.
        Assert.Contains(sentBeforeChange!, a => a.Action == "preview");
        Assert.Equal(AttemptStatuses.Assigned, (await PreviewAsync(server)).Status);
        Assert.DoesNotContain("opentofu", agent.Session.Executors);
    }

    [Fact]
    public async Task A_change_waits_for_the_gate_behind_the_agents_ordered_messages_while_heartbeats_go_on_and_the_last_wins()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var (stream, session) = await StreamAsync(server, await server.ConnectAgentAsync("home"),
            new Hello { AgentVersion = "test", Executors = { "opentofu", "proxmox", "ansible" } });
        var seen = await server.QueryAsync(db => db.Agents.Where(a => a.Id == session.AgentId).Select(a => a.LastSeenAt).SingleAsync());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = server.Engine.ExecuteAsync(async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return true;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        stream.Send(new AgentMessage { ExecutorsChanged = new ExecutorsChanged { Executors = { "proxmox" }, Unavailable = { ["opentofu"] = "first" } } });
        stream.Send(new AgentMessage { ExecutorsChanged = new ExecutorsChanged { Executors = { "proxmox", "ansible" }, Unavailable = { ["opentofu"] = "last" } } });
        await Task.Delay(50);
        stream.Send(new AgentMessage { Heartbeat = new Heartbeat() });
        await WaitUntilAsync("the heartbeat is handled while the gate is held", async () =>
            await server.QueryAsync(db => db.Agents.Where(a => a.Id == session.AgentId).Select(a => a.LastSeenAt).SingleAsync()) > seen);
        Assert.Equal(["ansible", "opentofu", "proxmox"], session.Executors);

        release.SetResult();
        await holder;
        await WaitUntilAsync("the last change is applied", () => Task.FromResult(session.Unavailable.GetValueOrDefault("opentofu") == "last"));
        Assert.Equal(["ansible", "proxmox"], session.Executors);
        stream.Complete();
    }

    [Fact]
    public async Task A_flapping_agent_costs_one_log_line_one_wake_and_one_write_per_interval_and_the_last_offer_is_stored()
    {
        var clock = new TestClock { Now = Start };
        var logs = new OfferLogs();
        await using var server = await ServerHarness.StartAsync(postgres, clock: clock, services: s => s.AddSingleton<ILoggerProvider>(logs));
        var (stream, session) = await StreamAsync(server, await server.ConnectAgentAsync("home"),
            new Hello { AgentVersion = "test", Executors = { "opentofu", "proxmox", "ansible" } });
        var signal = server.Services.GetRequiredService<OrchestratorSignal>();
        var wakes = signal.Wakes;
        var lines = logs.Count;
        Task<string?> StoredAsync() => server.QueryAsync(db => db.Agents.Where(a => a.Id == session.AgentId).Select(a => a.Unavailable).SingleAsync());

        // 200 reports in one interval: every other one drops opentofu with a new reason; the rest reorder and repeat the full set.
        for (var i = 0; i < 200; i++)
            stream.Send(new AgentMessage
            {
                ExecutorsChanged = i % 2 == 1
                    ? new ExecutorsChanged { Executors = { "proxmox", "ansible" }, Unavailable = { ["opentofu"] = $"flap {i}" } }
                    : new ExecutorsChanged { Executors = { "proxmox", "ansible", "opentofu", "proxmox" } },
            });
        await WaitUntilAsync("the burst is applied", () => Task.FromResult(session.Unavailable.GetValueOrDefault("opentofu") == "flap 199"));

        Assert.Equal(["ansible", "proxmox"], session.Executors);
        Assert.Equal(1, logs.Count - lines);
        Assert.Equal(1, signal.Wakes - wakes);
        Assert.Equal("flap 1", AgentAvailability.Deserialize(await StoredAsync())["opentofu"]);
        Assert.True(session.OfferUnsaved);

        // A heartbeat inside the interval stores nothing; the first one after it stores the last offer.
        stream.Send(new AgentMessage { Heartbeat = new Heartbeat() });
        await Task.Delay(200);
        Assert.Equal("flap 1", AgentAvailability.Deserialize(await StoredAsync())["opentofu"]);
        clock.Now += RunEngine.OfferEffectsEvery;
        stream.Send(new AgentMessage { Heartbeat = new Heartbeat() });
        await WaitUntilAsync("the last offer is stored", async () => AgentAvailability.Deserialize(await StoredAsync())["opentofu"] == "flap 199");
        Assert.Equal(["ansible", "proxmox"],
            await server.QueryAsync(db => db.Agents.Where(a => a.Id == session.AgentId).Select(a => a.Executors).SingleAsync()));
        Assert.False(session.OfferUnsaved);
        Assert.Equal(2, logs.Count - lines);
        Assert.Equal(2, signal.Wakes - wakes);
        stream.Complete();
    }
}
