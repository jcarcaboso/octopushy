using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Api;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Uptime;
using Services = Octopushy.Tests.Definitions.ServiceDefinitionTests;

namespace Octopushy.Tests.Server;

/// <summary>
/// Notifications: an uptime check that turns and a run that fails are each told once, in the save that records them.
/// See docs/notifications.md.
/// </summary>
public class NotificationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Linked = """
        schemaVersion: 1
        workflow: web@1
        links:
          - https://web.lab.test/
        inputs: { port: 9000, hostname: web-01 }
        secrets:
          web_password: { store: web/password }
        """;

    private static readonly DateTimeOffset Start = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static async Task<JsonArray> ListAsync(ServerHarness server) =>
        (await (await server.SignedInClientAsync()).GetFromJsonAsync<JsonObject>("/api/notifications"))!["items"]!.AsArray();

    [Fact]
    public async Task A_check_that_turns_down_and_up_again_is_told_once_each_and_a_first_answer_is_not()
    {
        var clock = new TestClock { Now = Start };
        await using var server = await ServerHarness.StartAsync(postgres, Services.Repository(Linked), clock: clock);
        var monitor = server.Services.GetRequiredService<UptimeMonitor>();
        await server.SyncAndTrustAsync();
        var status = 200u;
        await server.ConnectUptimeAgentAsync("checker", p => new() { ProbeId = p.ProbeId, Status = "reachable", HttpStatus = status, ElapsedMs = 5 });

        async Task MinuteAsync(int minute)
        {
            clock.Now = Start.AddMinutes(minute);
            await monitor.TickAsync(CancellationToken.None);
        }

        // New checks are spread over the first minute; by the second tick each has its first answer.
        await MinuteAsync(0);
        await MinuteAsync(1);
        Assert.Empty(await ListAsync(server));

        status = 503;
        await MinuteAsync(3);
        await MinuteAsync(5);
        status = 200;
        await MinuteAsync(7);

        var items = await ListAsync(server);
        Assert.Equal(["check-up", "check-down"], items.Select(i => i!["kind"]!.GetValue<string>()));
        Assert.True(items[0]!["id"]!.GetValue<long>() > items[1]!["id"]!.GetValue<long>());
        Assert.Equal("home/korriban/krb-app-01/web is up again", items[0]!["title"]!.GetValue<string>());
        Assert.Equal("home/korriban/krb-app-01/web is down", items[1]!["title"]!.GetValue<string>());
        Assert.Equal("https://web.lab.test/ · HTTP 503", items[1]!["body"]!.GetValue<string>());
        Assert.Equal("service", items[1]!["resourceKind"]!.GetValue<string>());
        Assert.Equal((await server.ResourceAsync(Services.Address)).Id, items[1]!["resourceId"]!.GetValue<Guid>());
        Assert.Equal(Start.AddMinutes(3), items[1]!["createdAt"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task A_run_that_fails_is_told_once_with_its_run_and_resource()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        Assert.Empty(await ListAsync(server));

        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Failed, error: "Error: Unsupported argument");
        await server.TickAsync();
        await server.TickAsync();

        var run = await server.QueryAsync(db => db.Runs.AsNoTracking().SingleAsync(r => r.Status == RunStatuses.Failed));
        var resource = await server.QueryAsync(db => db.Resources.AsNoTracking().SingleAsync(r => r.Id == run.ResourceId));
        var item = Assert.Single(await ListAsync(server))!;
        Assert.Equal("run-failed", item["kind"]!.GetValue<string>());
        Assert.Equal("Provision run failed", item["title"]!.GetValue<string>());
        Assert.Equal(resource.Address, item["body"]!.GetValue<string>());
        Assert.Equal(run.Id, item["runId"]!.GetValue<Guid>());
        Assert.Equal("guest", item["resourceKind"]!.GetValue<string>());
        // The text never carries what the tool printed.
        Assert.DoesNotContain("Unsupported", item.ToJsonString());
    }

    [Fact]
    public async Task A_run_that_waits_for_approval_is_told_once_and_one_that_succeeds_is_not()
    {
        using var repository = TestRepository.Minimal();
        repository.Write("labs/home/lab.yaml", "schemaVersion: 1\nexecution: { agentLabels: [home] }\npolicy: { approval: manual }\n");
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var artifact = FakeAgent.PlanArtifact();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: artifact);
        await server.TickAsync();
        // It goes on waiting, and is not told again for it.
        await server.TickAsync();

        var run = await server.QueryAsync(db => db.Runs.AsNoTracking().Include(r => r.Steps).SingleAsync(r => r.Status == RunStatuses.AwaitingApproval));
        var item = Assert.Single(await ListAsync(server))!;
        Assert.Equal("run-approval", item["kind"]!.GetValue<string>());
        Assert.Equal("Provision run awaits approval", item["title"]!.GetValue<string>());
        Assert.Equal(run.Id, item["runId"]!.GetValue<Guid>());

        await server.Engine.ApproveAsync(run.Id, run.Steps.Single(s => s.Name == StepNames.Approval).Id, artifact.Sha256, "ops", null, CancellationToken.None);
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await server.TickAsync();
        Assert.Single(await ListAsync(server));
    }

    /// <summary>Adds notifications as they are given, oldest first, and returns their ids.</summary>
    private static async Task<long[]> AddAsync(ServerHarness server, params Notification[] notifications)
    {
        await server.WithDbAsync(async db =>
        {
            db.Notifications.AddRange(notifications);
            await db.SaveChangesAsync();
        });
        return notifications.Select(n => n.Id).ToArray();
    }

    private static Notification Told(string title, string? body = null, string kind = NotificationKinds.CheckDown) =>
        new() { Kind = kind, Title = title, Body = body, CreatedAt = Start };

    private static async Task<NotificationsDto> PageAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync($"/api/notifications{query}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<NotificationsDto>())!;
    }

    [Fact]
    public async Task Paging_with_next_before_lists_each_notification_once_newest_first_and_after_lists_the_newer()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var client = await server.SignedInClientAsync();
        var ids = await AddAsync(server, Enumerable.Range(1, 7).Select(i => Told($"check {i}")).ToArray());

        var listed = new List<long>();
        var pages = new List<int>();
        var page = await PageAsync(client, "?limit=3");
        while (true)
        {
            listed.AddRange(page.Items.Select(n => n.Id));
            pages.Add(page.Items.Count);
            if (page.NextBefore is not { } next)
                break;
            Assert.Equal(page.Items[^1].Id, next);
            page = await PageAsync(client, $"?limit=3&before={next}");
        }

        Assert.Equal([3, 3, 1], pages);
        Assert.Equal(Enumerable.Reverse(ids), listed);

        // A page that ends exactly at the last notification says no page follows.
        Assert.Null((await PageAsync(client, $"?limit=3&before={ids[3]}")).NextBefore);

        var newer = await PageAsync(client, $"?after={ids[4]}");
        Assert.Equal([ids[6], ids[5]], newer.Items.Select(n => n.Id));
        Assert.Null(newer.NextBefore);
        Assert.Equal([ids[4], ids[3]], (await PageAsync(client, $"?after={ids[2]}&before={ids[5]}")).Items.Select(n => n.Id));
    }

    [Fact]
    public async Task With_no_parameters_it_lists_the_latest_fifty_and_says_when_more_follow()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var client = await server.SignedInClientAsync();
        var ids = await AddAsync(server, Enumerable.Range(1, 50).Select(i => Told($"check {i}")).ToArray());

        var page = await PageAsync(client);
        Assert.Equal(50, page.Items.Count);
        Assert.Null(page.NextBefore);

        var newest = await AddAsync(server, Told("check 51"));
        page = await PageAsync(client);
        Assert.Equal(newest.Concat(Enumerable.Reverse(ids[1..])), page.Items.Select(n => n.Id));
        Assert.Equal(ids[1], page.NextBefore);
        Assert.Equal([ids[0]], (await PageAsync(client, $"?before={page.NextBefore}")).Items.Select(n => n.Id));
    }

    [Fact]
    public async Task Kind_keeps_the_kinds_named_and_a_kind_or_limit_or_text_out_of_bounds_is_refused()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var client = await server.SignedInClientAsync();
        var ids = await AddAsync(server,
            Told("down", kind: NotificationKinds.CheckDown),
            Told("failed", kind: NotificationKinds.RunFailed),
            Told("offline", kind: NotificationKinds.AgentOffline),
            Told("up", kind: NotificationKinds.CheckUp));

        Assert.Equal([ids[1]], (await PageAsync(client, "?kind=run-failed")).Items.Select(n => n.Id));
        Assert.Equal([ids[3], ids[0]], (await PageAsync(client, "?kind=check-down&kind=check-up")).Items.Select(n => n.Id));
        Assert.Equal([ids[3], ids[2], ids[0]],
            (await PageAsync(client, "?kind=check-down&kind=agent-offline&kind=check-up&kind=check-up")).Items.Select(n => n.Id));

        foreach (var query in new[] { "?kind=check-sideways", "?kind=check-down&kind=Run-Failed", "?limit=0", "?limit=201", $"?q={new string('x', 201)}" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/notifications{query}")).StatusCode);
        Assert.Equal(4, (await PageAsync(client, "?limit=200")).Items.Count);
        Assert.Single((await PageAsync(client, "?limit=1")).Items);
    }

    [Fact]
    public async Task Q_matches_title_or_body_ignoring_case_and_its_percent_and_underscore_match_only_themselves()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var client = await server.SignedInClientAsync();
        var ids = await AddAsync(server,
            Told("home/web is DOWN", "https://web.lab.test/"),
            Told("Agent Korriban is offline"),
            Told("Disk at 100% on krb-app-01", "Over the 90% mark"),
            Told("Disk at 1000 on krb-app-01"),
            Told("Provision run failed", "home/korriban/krb_app_02"),
            Told("Provision run failed", "home/korriban/krb-app-03"));

        async Task<IEnumerable<long>> SearchAsync(string q) =>
            (await PageAsync(client, $"?q={Uri.EscapeDataString(q)}")).Items.Select(n => n.Id);

        Assert.Equal([ids[0]], await SearchAsync("Is Down"));
        Assert.Equal([ids[1]], await SearchAsync("KORRIBAN IS"));
        Assert.Equal([ids[0]], await SearchAsync("WEB.LAB"));
        Assert.Equal([ids[5], ids[4]], await SearchAsync("  HOME/korriban  "));
        Assert.Equal([ids[2]], await SearchAsync("100%"));
        Assert.Equal([ids[2]], await SearchAsync("%"));
        Assert.Equal([ids[4]], await SearchAsync("krb_app"));
        Assert.Equal([ids[4]], await SearchAsync("_"));
        Assert.Empty(await SearchAsync("100%_"));
        Assert.Equal(6, (await SearchAsync("   ")).Count());
    }

    [Fact]
    public async Task A_notification_names_its_run_resource_and_agent_until_their_rows_are_gone()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var client = await server.SignedInClientAsync();
        var resource = new Resource { Id = Guid.NewGuid(), Address = "home/korriban/krb-app-09", Kind = ResourceKinds.Guest, Lab = "home" };
        var run = new Run { Id = Guid.NewGuid(), ResourceId = resource.Id, Operation = Operations.Provision, Reason = "manual", Status = RunStatuses.Succeeded };
        var agent = new AgentRecord { Id = Guid.NewGuid(), Name = "korriban", CertificateSerial = "korriban" };
        await server.WithDbAsync(async db =>
        {
            db.Resources.Add(resource);
            db.Runs.Add(run);
            db.Agents.Add(agent);
            await db.SaveChangesAsync();
        });
        var number = await server.QueryAsync(db => db.Runs.Where(r => r.Id == run.Id).Select(r => r.Number).SingleAsync());
        var ids = await AddAsync(server,
            new Notification { Kind = NotificationKinds.RunFailed, Title = "Provision run failed", RunId = run.Id, ResourceId = resource.Id, CreatedAt = Start },
            new Notification { Kind = NotificationKinds.CheckDown, Title = "down", ResourceId = resource.Id, CreatedAt = Start },
            new Notification { Kind = NotificationKinds.AgentOffline, Title = "Agent korriban is offline", AgentId = agent.Id, CreatedAt = Start });

        var items = (await PageAsync(client)).Items.ToDictionary(n => n.Id);
        Assert.Equal((number, resource.Address, "guest", (string?)null), (items[ids[0]].RunNumber, items[ids[0]].ResourceAddress, items[ids[0]].ResourceKind, items[ids[0]].AgentName));
        Assert.Equal(((long?)null, resource.Address, (string?)null), (items[ids[1]].RunNumber, items[ids[1]].ResourceAddress, items[ids[1]].AgentName));
        Assert.Equal(((long?)null, (string?)null, "korriban"), (items[ids[2]].RunNumber, items[ids[2]].ResourceAddress, items[ids[2]].AgentName));

        await server.WithDbAsync(async db =>
        {
            await db.Runs.Where(r => r.Id == run.Id).ExecuteDeleteAsync();
            await db.Resources.Where(r => r.Id == resource.Id).ExecuteDeleteAsync();
            await db.Agents.Where(a => a.Id == agent.Id).ExecuteDeleteAsync();
        });
        items = (await PageAsync(client)).Items.ToDictionary(n => n.Id);
        Assert.All(items.Values, n => Assert.Equal(((long?)null, (string?)null, (string?)null, (string?)null), (n.RunNumber, n.ResourceAddress, n.ResourceKind, n.AgentName)));
        // The ids it was about stay, so a link can still say what is gone.
        Assert.Equal(run.Id, items[ids[0]].RunId);
        Assert.Equal(resource.Id, items[ids[1]].ResourceId);
        Assert.Equal(agent.Id, items[ids[2]].AgentId);
    }
}
