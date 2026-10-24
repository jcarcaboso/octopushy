using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class ApiTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_account_is_created_with_the_setup_code_and_guards_the_api()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = server.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/inventory")).StatusCode);
        var state = await client.GetFromJsonAsync<JsonObject>("/api/auth/state");
        Assert.True(state!["setupRequired"]!.GetValue<bool>());

        var wrong = await client.PostAsJsonAsync("/api/auth/setup", new { setupCode = "WRONG", username = "ops", password = "a long password" });
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);

        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        var setup = await client.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long password" });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        Assert.False(File.Exists(Path.Combine(server.DataDirectory, "setup-code")));

        await server.SyncAsync();
        var inventory = await client.GetFromJsonAsync<JsonObject>("/api/inventory");
        var lab = inventory!["labs"]![0]!;
        Assert.Equal("Home", lab["name"]!.GetValue<string>());
        var guest = lab["installations"]![0]!["guests"]![0]!;
        Assert.Equal("home/korriban/krb-app-01", guest["address"]!.GetValue<string>());
        Assert.Equal("blocked", guest["status"]!.GetValue<string>());

        var detail = await client.GetFromJsonAsync<JsonObject>($"/api/guests/{guest["id"]}");
        Assert.Contains("vmid: 310", detail!["definitionText"]!.GetValue<string>());
        Assert.Equal("automatic", detail["policy"]!["approval"]!.GetValue<string>());

        var variables = await client.GetFromJsonAsync<JsonObject>("/api/variables");
        var token = variables!["entries"]!.AsArray().Single(e => e!["key"]!.GetValue<string>() == "proxmox/korriban/read-only-token")!;
        Assert.Null(token["value"]);
        Assert.Contains("home/korriban", token["usedBy"]!.AsArray().Select(u => u!.GetValue<string>()));

        var created = await client.PostAsJsonAsync("/api/agents/tokens", new { name = "lab-agent", labels = new[] { "home" } });
        var enrollment = await created.Content.ReadFromJsonAsync<JsonObject>();
        Assert.StartsWith("opy1.", enrollment!["token"]!.GetValue<string>());
        Assert.Contains("--server https://octopushy.test:8443", enrollment["command"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/inventory")).StatusCode);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "ops", password = "a long password" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task The_api_port_does_not_serve_agent_endpoints()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = server.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/agent/ca.pem")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/octopushy.agent.v1.Enrollment/Enroll", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
    }

    [Fact]
    public async Task System_keys_cannot_be_set_through_the_api()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = server.CreateClient();
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        await client.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long password" });

        var response = await client.PutAsJsonAsync("/api/variables", new { key = "system/agent-ca/key", value = "x", visibility = "secret" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var variables = await client.GetFromJsonAsync<JsonObject>("/api/variables");
        Assert.Contains(variables!["system"]!.AsArray(), e => e!["key"]!.GetValue<string>() == "system/agent-ca/key");
        Assert.DoesNotContain(variables["entries"]!.AsArray(), e => e!["key"]!.GetValue<string>().StartsWith("system/"));
    }
}
