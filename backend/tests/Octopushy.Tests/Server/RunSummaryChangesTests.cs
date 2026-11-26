using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using static Octopushy.Tests.Server.ConfigureRunTests;
using Repository = Octopushy.Tests.Definitions.GuestConfigurationTests;

namespace Octopushy.Tests.Server;

/// <summary>A list of runs says what each run ran and what it changed, from what the run stored when it learned it.</summary>
public class RunSummaryChangesTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Guest = "home/korriban/krb-app-01";

    private static async Task<JsonObject> ListedAsync(HttpClient http, Guid runId) =>
        (await http.GetFromJsonAsync<JsonArray>("/api/runs"))!.OfType<JsonObject>().Single(r => r["id"]!.GetValue<Guid>() == runId);

    private static string? Changes(JsonObject summary) =>
        summary["changes"] is JsonArray changes
            ? string.Join(" | ", changes.Select(c => $"{c!["name"]}: {c["before"]?.GetValue<string>() ?? "null"} -> {c["after"]?.GetValue<string>() ?? "null"}"))
            : null;

    [Fact]
    public async Task A_configure_run_lists_its_action_from_the_start_and_what_its_preview_prepared_once_it_succeeded()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        var resource = await server.ResourceAsync(Guest);

        await server.Services.GetRequiredService<Planner>().ScheduleConfigurationsAsync(CancellationToken.None);
        var first = await server.LatestRunAsync(Guest, Operations.Configure);
        var listed = await ListedAsync(http, first.Id);
        Assert.Equal("setup", listed["action"]!.GetValue<string>());
        Assert.Null(listed["changes"]);

        // The first run has nothing to compare with.
        await PreviewAsync(agent, new JsonObject { ["version"] = "1.2.3" });
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap());
        await server.TickAsync();
        Assert.Null((await ListedAsync(http, first.Id))["changes"]);

        (await http.PostAsJsonAsync($"/api/guests/{resource.Id}/configure", new { action = "setup" })).EnsureSuccessStatusCode();
        await server.TickAsync();
        var second = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("setup", second.Action);
        Assert.Null(second.Changes);

        await PreviewAsync(agent, new JsonObject { ["version"] = "2.0.0" });
        listed = await ListedAsync(http, second.Id);
        Assert.Equal("setup", listed["action"]!.GetValue<string>());
        Assert.Equal("version: 1.2.3 -> 2.0.0", Changes(listed));

        // The run page's summary is the same, and it outlives the run's end.
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap());
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Configure)).Status);
        var detail = (await http.GetFromJsonAsync<JsonObject>($"/api/runs/{second.Id}"))!["run"]!.AsObject();
        Assert.Equal("version: 1.2.3 -> 2.0.0", Changes(detail));
        Assert.Equal("version: 1.2.3 -> 2.0.0", Changes(await ListedAsync(http, second.Id)));
    }

    [Fact]
    public async Task A_provision_run_lists_what_its_plan_changes()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        await SetKeysAsync(server);
        using var http = await server.SignedInClientAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        var listed = await ListedAsync(http, run.Id);
        Assert.Null(listed["action"]);
        Assert.Null(listed["changes"]);

        var plan = FakeAgent.Plan(create: 1, update: 1);
        var changes = plan["changes"]!.AsArray();
        changes[0]!["actions"] = new JsonArray("create");
        changes[1]!["actions"] = new JsonArray("update");
        changes[1]!["attributes"] = new JsonArray(
            new JsonObject { ["name"] = "memory.dedicated", ["before"] = 2048, ["after"] = 4096 },
            new JsonObject { ["name"] = "cpu.cores", ["before"] = 2, ["after"] = 4, ["sensitive"] = true });
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, plan, artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        Assert.Equal("memory: 2 GiB -> 4 GiB | resources: null -> 1 to create", Changes(await ListedAsync(http, run.Id)));
        // Stored on the run, which is what the list reads.
        Assert.Contains("\"memory\"", await server.QueryAsync(db => db.Runs.AsNoTracking().Where(r => r.Id == run.Id).Select(r => r.Changes).SingleAsync()));
    }
}
