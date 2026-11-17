using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
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
        await using var server = await ServerHarness.StartAsync(postgres);
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
        Assert.Equal(["proxmox", "ansible"], record.Executors);
        Assert.Equal("cannot run podman", AgentAvailability.Deserialize(record.Unavailable)["opentofu"]);

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
}
