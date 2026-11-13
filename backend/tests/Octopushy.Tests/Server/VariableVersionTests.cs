using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Agent.V1;
using Octopushy.Server.Data;

namespace Octopushy.Tests.Server;

/// <summary>
/// One version of a key under Variables: whether it counts as newer than the active one, revealing its value to compare it, and
/// deleting it once nothing needs it.
/// </summary>
[Collection(PostgresCollection.Name)]
public class VariableVersionTests(PostgresFixture postgres)
{
    private const string Token = "proxmox/korriban/read-only-token";

    [Fact]
    public async Task A_revoked_version_after_the_active_one_is_not_newer()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await server.SignedInClientAsync();
        (await client.PostAsJsonAsync("/api/variables/candidates", new { key = Token, value = "candidate" })).EnsureSuccessStatusCode();
        var listed = await EntryAsync(client);
        Assert.Equal(2, listed["newerVersion"]!.GetValue<int>());

        (await client.PostAsJsonAsync("/api/variables/revoke", new { key = Token, version = 2, reason = "not from here" })).EnsureSuccessStatusCode();
        listed = await EntryAsync(client);
        Assert.Null(listed["newerVersion"]);
        Assert.Equal(2, listed["latestVersion"]!.GetValue<int>());
        Assert.Equal(1, listed["version"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_version_is_revealed_whatever_its_state_and_only_outside_the_system_scope()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await server.SignedInClientAsync();
        await server.SetSecretAsync("app/token", "first");
        await server.SetSecretAsync("app/token", "second");
        (await client.PostAsJsonAsync("/api/variables/revoke", new { key = "app/token", version = 2, reason = "leaked" })).EnsureSuccessStatusCode();

        Assert.Equal("first", await RevealAsync(client, "app/token", 1));
        Assert.Equal("second", await RevealAsync(client, "app/token", 2));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/variables/versions/reveal", new { key = "app/token", version = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/variables/versions/reveal", new { key = "system/anything", version = 1 })).StatusCode);

        using var anonymous = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/variables/versions/reveal", new { key = "app/token", version = 1 })).StatusCode);
    }

    [Fact]
    public async Task A_version_that_is_not_active_is_deleted_and_its_number_never_given_again()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await server.SignedInClientAsync();
        await server.SetSecretAsync("app/token", "first");
        await server.SetSecretAsync("app/token", "second");
        (await client.PostAsJsonAsync("/api/variables/candidates", new { key = "app/token", value = "third" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync("/api/variables/versions?key=app/token&version=2")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync("/api/variables/versions?key=app/token&version=9")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/variables/versions?key=app/token&version=3")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/variables/versions?key=app/token&version=1")).StatusCode);

        var versions = (await client.GetFromJsonAsync<JsonArray>("/api/variables/versions?key=app/token"))!;
        Assert.Equal([2], versions.Select(v => v!["version"]!.GetValue<int>()));
        Assert.Equal("second", await RevealAsync(client, "app/token", 2));

        await server.SetSecretAsync("app/token", "fourth");
        versions = (await client.GetFromJsonAsync<JsonArray>("/api/variables/versions?key=app/token"))!;
        Assert.Equal([4, 2], versions.Select(v => v!["version"]!.GetValue<int>()));
    }

    [Fact]
    public async Task A_version_a_run_in_progress_pinned_is_not_deleted()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await server.SignedInClientAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(client);
        var plan = await agent.NextAsync("preview");
        (await client.PostAsJsonAsync("/api/variables/candidates", new { key = Token, value = "replacement" })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/variables/promote", new { key = Token, version = 2, expectedCurrentVersion = 1 })).EnsureSuccessStatusCode();

        var refused = await client.DeleteAsync($"/api/variables/versions?key={Token}&version=1");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("run #", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/variables/versions/reveal", new { key = Token, version = 1 })).StatusCode);

        await agent.ReportAsync(plan, Outcome.Failed, error: "fixture failure");
        await server.TickAsync();
        // The observation queued with the plan pinned v1 too, and holds it until it reports.
        var still = await client.DeleteAsync($"/api/variables/versions?key={Token}&version=1");
        Assert.Contains("observation of home/korriban", await still.Content.ReadAsStringAsync());
        var observations = new List<Guid>();
        await server.WithDbAsync(async db => observations.AddRange(await db.Attempts
            .Where(a => a.StepId == null && (a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running)).Select(a => a.Id).ToListAsync()));
        Assert.NotEmpty(observations);
        foreach (var observation in observations)
        {
            await agent.ReportAsync(observation.ToString(), Outcome.Failed, error: "connection refused");
        }
        await server.TickAsync();
        var after = await client.DeleteAsync($"/api/variables/versions?key={Token}&version=1");
        Assert.True(after.StatusCode == HttpStatusCode.NoContent, await after.Content.ReadAsStringAsync());
    }

    private static async Task<JsonObject> EntryAsync(HttpClient client)
    {
        var list = await client.GetFromJsonAsync<JsonObject>("/api/variables");
        return list!["entries"]!.AsArray().Single(e => e!["key"]!.GetValue<string>() == Token)!.AsObject();
    }

    private static async Task<string> RevealAsync(HttpClient client, string key, int version)
    {
        var response = await client.PostAsJsonAsync("/api/variables/versions/reveal", new { key, version });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["value"]!.GetValue<string>();
    }
}
