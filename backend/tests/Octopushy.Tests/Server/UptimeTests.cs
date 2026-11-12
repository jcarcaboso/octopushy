using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Uptime;
using Services = Octopushy.Tests.Definitions.ServiceDefinitionTests;

namespace Octopushy.Tests.Server;

/// <summary>
/// Uptime: agents check the Proxmox API of each trusted installation's hosts, each guest's SSH and each service's links
/// about once a minute, and the server keeps what they answer. See docs/uptime.md.
/// </summary>
[Collection(PostgresCollection.Name)]
public class UptimeTests(PostgresFixture postgres)
{
    private const string Linked = """
        schemaVersion: 1
        workflow: web@1
        links:
          - https://web.lab.test/health?full=1
          - http://web.lab.test:8080/
        inputs: { port: 9000, hostname: web-01 }
        secrets:
          web_password: { store: web/password }
        """;

    private static readonly DateTimeOffset Start = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private static async Task<(ServerHarness Server, TestClock Clock, UptimeMonitor Monitor)> StartAsync(PostgresFixture postgres)
    {
        var clock = new TestClock { Now = Start };
        var server = await ServerHarness.StartAsync(postgres, Services.Repository(Linked), clock: clock);
        return (server, clock, server.Services.GetRequiredService<UptimeMonitor>());
    }

    /// <summary>Answers as a lab where the Proxmox API answers, SSH refuses, and the service's first link says 503.</summary>
    private static ReachabilityReport Lab(ReachabilityProbe probe) => probe.Protocol switch
    {
        "tls" => new() { ProbeId = probe.ProbeId, Status = "reachable", ElapsedMs = 7 },
        "ssh" => new() { ProbeId = probe.ProbeId, Status = "refused", Detail = "10.1.0.31:22 refused the connection" },
        _ when probe.Path == "/health?full=1" => new() { ProbeId = probe.ProbeId, Status = "reachable", HttpStatus = 503, ElapsedMs = 40 },
        _ => new() { ProbeId = probe.ProbeId, Status = "reachable", HttpStatus = 200, ElapsedMs = 12 },
    };

    [Fact]
    public async Task Nothing_is_checked_until_the_installation_is_trusted()
    {
        var (server, clock, monitor) = await StartAsync(postgres);
        await using var _ = server;
        var probes = new List<ReachabilityProbe>();
        await server.ConnectUptimeAgentAsync("checker", p => { probes.Add(p); return Lab(p); });
        await server.SyncAsync();
        await monitor.TickAsync(CancellationToken.None);
        Assert.Empty(monitor.Targets);
        Assert.Empty(probes);

        await server.SyncAndTrustAsync();
        clock.Now = Start.AddMinutes(1);
        await monitor.TickAsync(CancellationToken.None);
        Assert.Equal(
            ["proxmox-api|home/korriban/korriban|https://10.1.0.10:8006/", "guest-ssh|home/korriban/krb-app-01|ssh://10.1.0.31:22",
             "service-http|home/korriban/krb-app-01/web|https://web.lab.test/health?full=1", "service-http|home/korriban/krb-app-01/web|http://web.lab.test:8080/"],
            monitor.Targets.Select(t => t.Key));
    }

    [Fact]
    public async Task Each_check_goes_to_an_uptime_agent_from_its_own_process_and_records_what_it_answers()
    {
        var (server, clock, monitor) = await StartAsync(postgres);
        await using var _ = server;
        await server.SyncAndTrustAsync();
        var older = await server.ConnectProbedAgentAsync("older");
        var probes = new List<ReachabilityProbe>();
        await server.ConnectUptimeAgentAsync("checker", p => { probes.Add(p); return Lab(p); });
        await monitor.TickAsync(CancellationToken.None);
        // New checks are spread over the minute: the first goes at once, the last 45 seconds later.
        clock.Now = Start.AddSeconds(50);
        await monitor.TickAsync(CancellationToken.None);

        Assert.Empty(older.TakeProbes());
        Assert.Equal(4, probes.Count);
        Assert.All(probes, p => Assert.Equal("agent", p.Context));
        Assert.All(probes, p => Assert.StartsWith(UptimeMonitor.ProbePrefix, p.ProbeId));
        var api = Assert.Single(probes, p => p.Protocol == "tls");
        Assert.Equal(("10.1.0.10", 8006u, "collect"), (api.Host, api.Port, api.TlsTrust));
        var ssh = Assert.Single(probes, p => p.Protocol == "ssh");
        Assert.Equal(("10.1.0.31", 22u), (ssh.Host, ssh.Port));
        var https = Assert.Single(probes, p => p.Protocol == "http" && p.Tls);
        Assert.Equal(("web.lab.test", 443u, "web.lab.test", "/health?full=1", "system"), (https.Host, https.Port, https.ServerName, https.Path, https.TlsTrust));
        var plain = Assert.Single(probes, p => p.Protocol == "http" && !p.Tls);
        Assert.Equal((8080u, "/"), (plain.Port, plain.Path));

        var checks = await server.QueryAsync(db => db.UptimeChecks.AsNoTracking().ToDictionaryAsync(c => c.Key));
        Assert.True(checks["proxmox-api|home/korriban/korriban|https://10.1.0.10:8006/"].Up);
        var guest = checks["guest-ssh|home/korriban/krb-app-01|ssh://10.1.0.31:22"];
        Assert.False(guest.Up);
        Assert.Equal("10.1.0.31:22 refused the connection", guest.Detail);
        Assert.Equal("checker", guest.Agent);
        var health = checks["service-http|home/korriban/krb-app-01/web|https://web.lab.test/health?full=1"];
        Assert.Equal((false, "HTTP 503"), (health.Up, health.Detail));
        Assert.True(checks["service-http|home/korriban/krb-app-01/web|http://web.lab.test:8080/"].Up);

        // Each is due again a minute after it went out.
        probes.Clear();
        clock.Now = Start.AddSeconds(59);
        await monitor.TickAsync(CancellationToken.None);
        Assert.Empty(probes);
        clock.Now = Start.AddSeconds(60);
        await monitor.TickAsync(CancellationToken.None);
        Assert.Equal("tls", Assert.Single(probes).Protocol);
    }

    [Fact]
    public async Task A_check_goes_to_the_agents_that_reach_its_target_and_one_without_a_route_says_nothing_about_it()
    {
        var (server, clock, monitor) = await StartAsync(postgres);
        await using var _ = server;
        await server.SyncAndTrustAsync();
        const string guestKey = "guest-ssh|home/korriban/krb-app-01|ssh://10.1.0.31:22";
        const string healthKey = "service-http|home/korriban/krb-app-01/web|https://web.lab.test/health?full=1";
        static ReachabilityReport TimedOut(ReachabilityProbe p) =>
            new() { ProbeId = p.ProbeId, Status = "timeout", Detail = "connecting to 10.1.0.31:22 timed out after 10s" };
        // "a-far" is first by name and has no route to the guest; "b-near" reaches it until it goes down.
        var guestDown = false;
        var far = new List<ReachabilityProbe>();
        var near = new List<ReachabilityProbe>();
        await server.ConnectUptimeAgentAsync("a-far", p => { far.Add(p); return p.Protocol == "ssh" ? TimedOut(p) : Lab(p); });
        var nearAgent = await server.ConnectUptimeAgentAsync("b-near", p =>
        {
            near.Add(p);
            return p.Protocol != "ssh" ? Lab(p) : guestDown ? TimedOut(p) : new() { ProbeId = p.ProbeId, Status = "reachable", ElapsedMs = 3 };
        });
        async Task<Octopushy.Server.Data.UptimeCheck?> Check(string key) =>
            await server.QueryAsync(db => db.UptimeChecks.AsNoTracking().FirstOrDefaultAsync(c => c.Key == key));

        // The first round asks "a-far", which cannot connect: nothing is recorded, and "b-near" is asked next.
        await monitor.TickAsync(CancellationToken.None);
        clock.Now = Start.AddSeconds(50);
        await monitor.TickAsync(CancellationToken.None);
        Assert.Single(far, p => p.Protocol == "ssh");
        Assert.Null(await Check(guestKey));
        clock.Now = Start.AddSeconds(55);
        await monitor.TickAsync(CancellationToken.None);
        var guest = await Check(guestKey);
        Assert.Equal((true, "b-near"), (guest!.Up, guest.Agent));

        // An agent that connects stands by what the target answers: a 503 is down, and nobody else is asked.
        var health = await Check(healthKey);
        Assert.Equal((false, "HTTP 503", "a-far"), (health!.Up, health.Detail, health.Agent));
        Assert.DoesNotContain(near, p => p.Path == "/health?full=1");

        // From then on the check goes to "b-near", which reached it.
        far.Clear();
        near.Clear();
        clock.Now = Start.AddSeconds(110);
        await monitor.TickAsync(CancellationToken.None);
        Assert.Single(near, p => p.Protocol == "ssh");
        Assert.DoesNotContain(far, p => p.Protocol == "ssh");

        // When "b-near" cannot connect either, every agent is asked before the guest is down, with what "b-near" saw.
        guestDown = true;
        clock.Now = Start.AddSeconds(170);
        await monitor.TickAsync(CancellationToken.None);
        Assert.True((await Check(guestKey))!.Up);
        clock.Now = Start.AddSeconds(175);
        await monitor.TickAsync(CancellationToken.None);
        guest = await Check(guestKey);
        Assert.Equal((false, "b-near", Start.AddSeconds(170)), (guest!.Up, guest.Agent, guest.Since));

        // With only "a-far" connected, which never reached the guest, a failed connection records nothing.
        server.Services.GetRequiredService<AgentConnections>().Remove(nearAgent.Session);
        guestDown = false;
        far.Clear();
        clock.Now = Start.AddSeconds(240);
        await monitor.TickAsync(CancellationToken.None);
        Assert.Single(far, p => p.Protocol == "ssh");
        Assert.Equal(Start.AddSeconds(170), (await Check(guestKey))!.CheckedAt);
    }

    [Fact]
    public async Task A_failure_in_the_pool_is_not_down_while_another_agent_of_the_pool_could_not_check()
    {
        var (server, clock, monitor) = await StartAsync(postgres);
        await using var _ = server;
        await server.SyncAndTrustAsync();
        const string guestKey = "guest-ssh|home/korriban/krb-app-01|ssh://10.1.0.31:22";
        // Both agents reach the guest once, then "b" cannot connect and "a", also in the pool, cannot check.
        var phase = 1;
        static ReachabilityReport Reachable(ReachabilityProbe p) => new() { ProbeId = p.ProbeId, Status = "reachable", ElapsedMs = 3 };
        static ReachabilityReport TimedOut(ReachabilityProbe p) => new() { ProbeId = p.ProbeId, Status = "timeout", Detail = "timed out" };
        await server.ConnectUptimeAgentAsync("a", p => p.Protocol != "ssh" ? Lab(p) : phase switch
        {
            1 => Reachable(p),
            2 => TimedOut(p),
            _ => new() { ProbeId = p.ProbeId, Status = "unsupported", Detail = "through a proxy" },
        });
        await server.ConnectUptimeAgentAsync("b", p => p.Protocol != "ssh" ? Lab(p) : phase == 3 ? TimedOut(p) : Reachable(p));
        async Task<Octopushy.Server.Data.UptimeCheck?> Check() =>
            await server.QueryAsync(db => db.UptimeChecks.AsNoTracking().FirstOrDefaultAsync(c => c.Key == guestKey));

        await monitor.TickAsync(CancellationToken.None);
        clock.Now = Start.AddSeconds(50);
        await monitor.TickAsync(CancellationToken.None);
        Assert.Equal("a", (await Check())!.Agent);
        phase = 2;
        clock.Now = Start.AddSeconds(110);
        await monitor.TickAsync(CancellationToken.None);
        clock.Now = Start.AddSeconds(115);
        await monitor.TickAsync(CancellationToken.None);
        Assert.Equal((true, "b"), ((await Check())!.Up, (await Check())!.Agent));

        phase = 3;
        clock.Now = Start.AddSeconds(170);
        await monitor.TickAsync(CancellationToken.None);
        clock.Now = Start.AddSeconds(175);
        await monitor.TickAsync(CancellationToken.None);
        var guest = await Check();
        Assert.Equal((true, Start.AddSeconds(115)), (guest!.Up, guest.CheckedAt));
    }

    [Theory]
    [InlineData("reachable", 200u, true, "HTTP 200")]
    [InlineData("reachable", 302u, true, "HTTP 302")]
    [InlineData("reachable", 401u, true, "HTTP 401")]
    [InlineData("reachable", 404u, false, "HTTP 404")]
    [InlineData("reachable", 502u, false, "HTTP 502")]
    [InlineData("reachable", 0u, false, "no HTTP status was read")]
    [InlineData("tls-untrusted", 0u, false, "the certificate was not accepted")]
    [InlineData("timeout", 0u, false, "no answer within 10s")]
    public void An_http_answer_is_up_for_success_redirects_and_logins(string status, uint code, bool up, string detail)
    {
        var target = new UptimeTarget("k", UptimeTarget.ServiceHttp, "home/korriban", "korriban", "home/korriban/g/web", "https://web.lab.test/",
            "http", "web.lab.test", 443, "web.lab.test", "/", true);
        var report = new ReachabilityReport { Status = status, HttpStatus = code, Detail = status == "reachable" ? "" : detail };
        Assert.Equal((up, detail), UptimeMonitor.Judge(target, report));
    }

    [Fact]
    public void An_agent_that_cannot_check_faithfully_says_nothing_about_the_target()
    {
        var target = new UptimeTarget("k", UptimeTarget.GuestSsh, "home/korriban", "korriban", "home/korriban/g", "ssh://10.1.0.31:22",
            "ssh", "10.1.0.31", 22, null, null, false);
        Assert.Null(UptimeMonitor.Judge(target, new ReachabilityReport { Status = "unsupported", Detail = "through a proxy" }));
    }

    [Fact]
    public async Task Uptime_answers_are_never_reachability_evidence_and_others_are_not_taken_as_uptime()
    {
        var (server, clock, monitor) = await StartAsync(postgres);
        await using var _ = server;
        await server.SyncAndTrustAsync();
        var agent = await server.ConnectProbedAgentAsync("plain");
        var reachability = server.Services.GetRequiredService<WorkerReachability>();
        Assert.False(await reachability.AcceptAsync(agent.Session, new ReachabilityReport { ProbeId = "up-123", Status = "reachable" }, CancellationToken.None));
        Assert.True(await monitor.AcceptAsync(agent.Session, new ReachabilityReport { ProbeId = "up-123", Status = "reachable" }, CancellationToken.None));
        Assert.False(await monitor.AcceptAsync(agent.Session, new ReachabilityReport { ProbeId = "0123abcd", Status = "reachable" }, CancellationToken.None));
        Assert.Empty(await server.QueryAsync(db => db.UptimeChecks.ToListAsync()));
    }

    [Fact]
    public async Task The_api_reports_quarter_hours_days_and_percentages_from_the_answers()
    {
        var (server, clock, monitor) = await StartAsync(postgres);
        await using var _ = server;
        await server.SyncAndTrustAsync();
        await server.ConnectUptimeAgentAsync("checker", Lab);
        const string Api = "proxmox-api|home/korriban/korriban|https://10.1.0.10:8006/";
        // Two days ago down once and up once; a quarter hour ago up once and down once. Then the first tick checks the API at 09:00.
        await server.WithDbAsync(async db =>
        {
            await UptimeMonitor.RecordAsync(db, Api, false, "timeout", null, "checker", Start.AddDays(-2), CancellationToken.None);
            await UptimeMonitor.RecordAsync(db, Api, true, "TLS handshake completed", 6, "checker", Start.AddDays(-2).AddMinutes(5), CancellationToken.None);
            await UptimeMonitor.RecordAsync(db, Api, true, "TLS handshake completed", 6, "checker", Start.AddMinutes(-14), CancellationToken.None);
            await UptimeMonitor.RecordAsync(db, Api, false, "timeout", null, "checker", Start.AddMinutes(-13), CancellationToken.None);
        });
        await monitor.TickAsync(CancellationToken.None);
        clock.Now = Start.AddSeconds(50);
        await monitor.TickAsync(CancellationToken.None);

        var client = await server.SignedInClientAsync();
        var uptime = (await client.GetFromJsonAsync<JsonObject>("/api/uptime"))!;
        Assert.True(uptime["checking"]!.GetValue<bool>());
        var api = uptime["checks"]!.AsArray().Single(c => c!["key"]!.GetValue<string>() == Api)!;
        Assert.Equal("up", api["status"]!.GetValue<string>());
        Assert.Equal("korriban", api["host"]!.GetValue<string>());
        var beats = api["beats"]!.GetValue<string>();
        Assert.Equal(96, beats.Length);
        // The last beat is 09:00 (the first tick's answer), the one before 08:45 (one up, one down).
        Assert.Equal("pu", beats[^2..]);
        Assert.Equal(new string('-', 94), beats[..94]);
        Assert.Equal(6, api["latency"]![94]!.GetValue<int>());
        // Today: up, up, down. 7 and 30 days add two days ago: down, up.
        Assert.Equal(66.667, api["uptime"]!["day"]!.GetValue<double>());
        Assert.Equal(60, api["uptime"]!["week"]!.GetValue<double>());
        var daily = api["daily"]!.AsArray();
        Assert.Equal(30, daily.Count);
        Assert.Equal(50, daily[27]!.GetValue<double>());
        Assert.Null(daily[0]);

        var guest = uptime["checks"]!.AsArray().Single(c => c!["kind"]!.GetValue<string>() == "guest-ssh")!;
        Assert.Equal("down", guest["status"]!.GetValue<string>());
        Assert.Equal("home/korriban/krb-app-01", guest["resource"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_inventory_lists_links_unmanaged_guests_host_endpoints_and_guest_addresses()
    {
        var (server, _, _) = await StartAsync(postgres);
        await using var _ = server;
        await server.SyncAndTrustAsync();
        var client = await server.SignedInClientAsync();
        var installation = (await client.GetFromJsonAsync<JsonObject>("/api/inventory"))!["labs"]![0]!["installations"]![0]!;
        Assert.Equal("https://10.1.0.10:8006/", installation["hosts"]![0]!["endpoint"]!.GetValue<string>());
        Assert.Empty(installation["unmanagedGuests"]!.AsArray());
        var guest = installation["guests"]![0]!;
        Assert.Equal("10.1.0.31", guest["accessAddress"]!.GetValue<string>());
        var service = guest["services"]![0]!;
        Assert.Equal("web", service["name"]!.GetValue<string>());
        Assert.Equal(["https://web.lab.test/health?full=1", "http://web.lab.test:8080/"], service["links"]!.AsArray().Select(l => l!.GetValue<string>()));
    }

    [Fact]
    public async Task The_names_a_services_stage_sets_are_shown_opened_and_checked()
    {
        var clock = new TestClock { Now = Start };
        await using var server = await ServerHarness.StartAsync(postgres,
            Octopushy.Tests.Definitions.InterfaceTests.Repository(dnsStage: ", names: 'inputs.records[].name'"), clock: clock);
        var monitor = server.Services.GetRequiredService<UptimeMonitor>();
        await server.SyncAndTrustAsync();
        await monitor.TickAsync(CancellationToken.None);
        Assert.Contains("service-http|home/korriban/krb-app-01/web|https://web.example.com/", monitor.Targets.Select(t => t.Key));

        var client = await server.SignedInClientAsync();
        var service = (await client.GetFromJsonAsync<JsonObject>("/api/inventory"))!["labs"]![0]!["installations"]![0]!["guests"]![0]!["services"]![0]!;
        Assert.Equal(["https://web.example.com/"], service["links"]!.AsArray().Select(l => l!.GetValue<string>()));
        var name = Assert.Single(service["names"]!.AsArray())!;
        Assert.Equal("web.example.com", name["name"]!.GetValue<string>());
        Assert.Equal("dns", name["stage"]!.GetValue<string>());
        Assert.Equal("https://web.example.com/", name["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_tcp_link_is_checked_by_opening_a_connection_and_only_by_agents_that_can()
    {
        var clock = new TestClock { Now = Start };
        await using var server = await ServerHarness.StartAsync(postgres,
            Services.Repository(Linked.Replace("  - http://web.lab.test:8080/", "  - tcp://10.1.0.31:5432")), clock: clock);
        var monitor = server.Services.GetRequiredService<UptimeMonitor>();
        await server.SyncAndTrustAsync();
        const string key = "service-tcp|home/korriban/krb-app-01/web|tcp://10.1.0.31:5432";
        async Task<Octopushy.Server.Data.UptimeCheck?> Check() =>
            await server.QueryAsync(db => db.UptimeChecks.AsNoTracking().FirstOrDefaultAsync(c => c.Key == key));
        async Task MinuteAsync(int from)
        {
            for (var second = from; second < from + 60; second += 5)
            {
                clock.Now = Start.AddSeconds(second);
                await monitor.TickAsync(CancellationToken.None);
            }
        }

        // An agent without uptime-tcp-v1 is never asked, and nothing is recorded.
        var older = new List<ReachabilityProbe>();
        await server.ConnectUptimeAgentAsync("a-older", p => { older.Add(p); return Lab(p); });
        await MinuteAsync(0);
        var target = Assert.Single(monitor.Targets, t => t.Key == key);
        Assert.Equal(("tcp", "10.1.0.31", 5432, UptimeTarget.ServiceTcp, "home/korriban/krb-app-01/web"),
            (target.Protocol, target.HostName, target.Port, target.Kind, target.Resource));
        Assert.Contains(older, p => p.Protocol == "http");
        Assert.DoesNotContain(older, p => p.Protocol == "tcp");
        Assert.Null(await Check());

        // One that announces it opens a connection from its own process, and that is up.
        var newer = new List<ReachabilityProbe>();
        await server.ConnectUptimeAgentAsync("b-newer", p =>
        {
            newer.Add(p);
            return p.Protocol == "tcp" ? new() { ProbeId = p.ProbeId, Status = "reachable", ElapsedMs = 2 } : Lab(p);
        }, tcp: true);
        await MinuteAsync(60);
        var probe = Assert.Single(newer, p => p.Protocol == "tcp");
        Assert.Equal(("10.1.0.31", 5432u, "agent", false, "", ""), (probe.Host, probe.Port, probe.Context, probe.Tls, probe.Path, probe.TlsTrust));
        var check = await Check();
        Assert.Equal((true, "TCP connection opened", "b-newer"), (check!.Up, check.Detail, check.Agent));
    }

    [Theory]
    [InlineData("reachable", true, "TCP connection opened")]
    [InlineData("refused", false, "10.1.0.31:5432 refused the connection")]
    public void A_tcp_check_is_up_when_the_connection_opens(string status, bool up, string detail)
    {
        var target = new UptimeTarget("k", UptimeTarget.ServiceTcp, "home/korriban", "korriban", "home/korriban/krb-app-01/web", "tcp://10.1.0.31:5432",
            "tcp", "10.1.0.31", 5432, null, null, false);
        var report = new ReachabilityReport { ProbeId = "up-1", Status = status, Detail = status == "refused" ? detail : "" };
        Assert.Equal((up, detail), UptimeMonitor.Judge(target, report));
    }
}
