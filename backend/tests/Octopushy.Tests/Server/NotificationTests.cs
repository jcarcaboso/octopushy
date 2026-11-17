using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
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
}
