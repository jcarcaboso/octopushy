using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Agent.V1;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class GuestFilesystemRunTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";

    [Theory]
    [InlineData("grow", false)]
    [InlineData("verify", false)]
    [InlineData("grow", true)]
    public async Task Guest_step_retries_keep_the_successful_infrastructure_and_prior_attempts(string failedAction, bool uncertain)
    {
        using var repo = Definitions.GuestFilesystemTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repo);
        using var http = server.CreateClient();
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await http.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded);
        var growth = await agent.NextAsync("grow");
        Assert.Equal("nixos-rootfs", growth.Executor);
        var input = JsonNode.Parse(growth.InputJson)!;
        Assert.Equal(27917287424L, input["diskBytes"]!.GetValue<long>());
        Assert.Equal(Guest, input["address"]!.GetValue<string>());
        Assert.Single(growth.Secrets);
        if (failedAction == "verify")
        {
            await agent.ReportAsync(growth, Outcome.Succeeded);
            growth = await agent.NextAsync("verify");
        }
        await agent.ReportAsync(growth, uncertain ? Outcome.Uncertain : Outcome.Failed, error: "controlled guest failure");
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        var retryUrl = $"/api/runs/{run.Id}/retry-filesystem";
        var response = await http.PostAsync(retryUrl, null);
        if (uncertain)
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            (await http.PostAsync($"/api/runs/{run.Id}/close", null)).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync(retryUrl, null)).StatusCode);
            return;
        }
        response.EnsureSuccessStatusCode();
        var retry = await agent.NextAsync(failedAction);
        Assert.Equal("nixos-rootfs", retry.Executor);
        await agent.ReportAsync(retry, Outcome.Succeeded);
        if (failedAction == "grow")
            await agent.ReportAsync(await agent.NextAsync("verify"), Outcome.Succeeded);
        await server.TickAsync();
        var detail = (await http.GetFromJsonAsync<JsonObject>($"/api/runs/{run.Id}"))!;
        Assert.Equal("succeeded", detail["run"]!["status"]!.GetValue<string>());
        var steps = detail["steps"]!.AsArray();
        foreach (var name in new[] { "plan", "apply", "verify" })
            Assert.Single(steps.Single(s => s!["name"]!.GetValue<string>() == name)!["attempts"]!.AsArray());
        var attempts = steps.Single(s => s!["name"]!.GetValue<string>() == (failedAction == "grow" ? "grow-root" : "verify-root"))!["attempts"]!.AsArray();
        Assert.Equal(2, attempts.Count);
        Assert.Equal("failed", attempts[0]!["status"]!.GetValue<string>());
        Assert.Equal("succeeded", attempts[1]!["status"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync(retryUrl, null)).StatusCode);
    }

    [Theory]
    [InlineData("verify", true)]
    [InlineData("grow", false)]
    public async Task A_guest_attempt_that_lost_its_agent_blocks_the_retry_only_when_it_could_have_changed_something(string action, bool retryable)
    {
        using var repo = Definitions.GuestFilesystemTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repo);
        using var http = server.CreateClient();
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await http.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded);
        if (action == "verify")
            await agent.ReportAsync(await agent.NextAsync("grow"), Outcome.Succeeded);

        // Read-only attempts are retried until they run out; a mutating one is never retried.
        var losses = retryable ? 3 : 1;
        for (var i = 0; i < losses; i++)
        {
            var lost = await agent.NextAsync(action);
            await server.Engine.OnStartedAsync(agent.Id, Guid.Parse(lost.AttemptId), CancellationToken.None);
            await server.WithDbAsync(db => db.Attempts.Where(a => a.Id == Guid.Parse(lost.AttemptId))
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
            await server.TickAsync();
        }

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(retryable ? RunStatuses.Failed : RunStatuses.Uncertain, run.Status);
        var response = await http.PostAsync($"/api/runs/{run.Id}/retry-filesystem", null);
        if (!retryable)
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            // Closing the run leaves it failed, so only the lost mutating attempt can still refuse the retry.
            (await http.PostAsync($"/api/runs/{run.Id}/close", null)).EnsureSuccessStatusCode();
            Assert.Equal(RunStatuses.Failed, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
            Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync($"/api/runs/{run.Id}/retry-filesystem", null)).StatusCode);
            return;
        }

        response.EnsureSuccessStatusCode();
        await agent.ReportAsync(await agent.NextAsync("verify"), Outcome.Succeeded);
        await server.TickAsync();
        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        var attempts = run.Steps.Single(s => s.Name == StepNames.VerifyRoot).Attempts.OrderBy(a => a.Number).ToList();
        Assert.Equal(4, attempts.Count);
        Assert.All(attempts.Take(3), a => Assert.Equal(AttemptStatuses.Uncertain, a.Status));
        Assert.Equal(AttemptStatuses.Succeeded, attempts[3].Status);
    }

    [Fact]
    public async Task A_noop_infrastructure_plan_still_checks_guest_capacity_and_stale_retries_are_refused()
    {
        using var repo = Definitions.GuestFilesystemTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repo);
        using var http = server.CreateClient();
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await http.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan());
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded);
        await agent.ReportAsync(await agent.NextAsync("grow"), Outcome.Failed, error: "unit unavailable");
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        var detail = (await http.GetFromJsonAsync<JsonObject>($"/api/runs/{run.Id}"))!;
        Assert.Empty(detail["steps"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == "apply")!["attempts"]!.AsArray());
        const string file = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
        repo.Write(file, File.ReadAllText(Path.Combine(repo.Root, file)).Replace("size: 26", "size: 27"));
        server.Commit("Different desired capacity");
        await server.SyncAndTrustAsync(http);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync($"/api/runs/{run.Id}/retry-filesystem", null)).StatusCode);
    }
}
