using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Octopushy.Tests.Server;

/// <summary>Revealing values under Variables: the active value of each key asked for, secret or plain, and never a system one.</summary>
[Collection(PostgresCollection.Name)]
public class VariableRevealTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Reveal_returns_the_active_value_of_each_key_and_none_for_a_missing_or_revoked_one()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await SignInAsync(server);
        await server.SetSecretAsync("app/db-password", "hunter2");
        await server.SetPlainAsync("app/region", "eu-west");
        await server.SetSecretAsync("app/old-token", "revoked value");
        (await client.PostAsJsonAsync("/api/variables/revoke", new { key = "app/old-token", version = 1, reason = "leaked" })).EnsureSuccessStatusCode();

        // The list still conceals the secret.
        var list = await client.GetFromJsonAsync<JsonObject>("/api/variables");
        var listed = list!["entries"]!.AsArray().Single(e => e!["key"]!.GetValue<string>() == "app/db-password");
        Assert.Null(listed!["value"]);

        var response = await client.PostAsJsonAsync("/api/variables/reveal",
            new { keys = new[] { "app/db-password", "app/region", "app/old-token", "app/missing", "app/db-password" } });
        response.EnsureSuccessStatusCode();
        var values = (await response.Content.ReadFromJsonAsync<JsonObject>())!["values"]!.AsArray()
            .ToDictionary(v => v!["key"]!.GetValue<string>(), v => v!["value"]?.GetValue<string>());

        Assert.Equal(new Dictionary<string, string?>
        {
            ["app/db-password"] = "hunter2",
            ["app/region"] = "eu-west",
            ["app/old-token"] = null,
            ["app/missing"] = null,
        }, values);
    }

    [Fact]
    public async Task Reveal_refuses_system_keys_and_an_empty_request()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await SignInAsync(server);

        var system = await client.PostAsJsonAsync("/api/variables/reveal", new { keys = new[] { "system/anything" } });
        Assert.Equal(HttpStatusCode.BadRequest, system.StatusCode);
        var empty = await client.PostAsJsonAsync("/api/variables/reveal", new { keys = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task Reveal_needs_a_signed_in_operator()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SetSecretAsync("app/db-password", "hunter2");
        using var anonymous = server.CreateClient();

        var response = await anonymous.PostAsJsonAsync("/api/variables/reveal", new { keys = new[] { "app/db-password" } });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<HttpClient> SignInAsync(ServerHarness server)
    {
        var client = server.CreateClient();
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await client.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long fixture password" })).EnsureSuccessStatusCode();
        return client;
    }
}
