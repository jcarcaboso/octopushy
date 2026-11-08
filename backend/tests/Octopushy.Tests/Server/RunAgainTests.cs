using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Repository = Octopushy.Tests.Definitions.GuestConfigurationTests;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class RunAgainTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";

    private static async Task<bool> CanRunAgainAsync(HttpClient http, Guid runId) =>
        (await http.GetFromJsonAsync<JsonObject>($"/api/runs/{runId}"))!["canRunAgain"]!.GetValue<bool>();

    [Fact]
    public async Task A_failed_configure_run_runs_its_action_again_as_a_new_run_once()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        await server.Services.GetRequiredService<Planner>().ScheduleConfigurationsAsync(CancellationToken.None);
        await ConfigureRunTests.PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Failed, ConfigureRunTests.Recap(), "a task failed");
        await server.TickAsync();
        var failed = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.Failed, failed.Status);
        Assert.True(await CanRunAgainAsync(http, failed.Id));

        var started = await http.PostAsync($"/api/runs/{failed.Id}/run-again", null);
        started.EnsureSuccessStatusCode();
        var runId = (await started.Content.ReadFromJsonAsync<JsonObject>())!["runId"]!.GetValue<Guid>();
        var again = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(runId, again.Id);
        Assert.Equal("manual", again.Reason);
        Assert.Equal(RunContext.Parse(failed.Context).Ansible!.Action, RunContext.Parse(again.Context).Ansible!.Action);

        // The failed run is no longer the latest of its work, so it is not started a second time.
        Assert.False(await CanRunAgainAsync(http, failed.Id));
        var refused = await http.PostAsync($"/api/runs/{failed.Id}/run-again", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains($"Run #{again.Number} is newer", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_failed_provision_run_plans_again_and_a_run_that_did_not_fail_cannot_be_run_again()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var http = server.CreateClient();
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await http.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Failed, error: "Error: Unsupported argument");
        await server.TickAsync();
        var failed = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, failed.Status);

        (await http.PostAsync($"/api/runs/{failed.Id}/run-again", null)).EnsureSuccessStatusCode();
        var again = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.NotEqual(failed.Id, again.Id);
        Assert.NotNull(await agent.NextAsync("preview"));

        Assert.False(await CanRunAgainAsync(http, again.Id));
        var refused = await http.PostAsync($"/api/runs/{again.Id}/run-again", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Only a failed run", await refused.Content.ReadAsStringAsync());
    }
}
