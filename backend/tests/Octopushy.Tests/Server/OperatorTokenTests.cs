using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class OperatorTokenTests(PostgresFixture postgres)
{
    private const string Path = "/api/auth/operator-tokens";

    [Fact]
    public async Task A_console_grant_authenticates_the_cli_until_revoked_without_exposing_its_secret_again()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        await server.SyncAsync();
        using var console = server.CreateClient();
        console.BaseAddress = new Uri("https://localhost:8080");
        var setupCode = (await File.ReadAllTextAsync(System.IO.Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await console.PostAsJsonAsync("/api/auth/setup",
            new { setupCode, username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        var response = await console.PostAsJsonAsync(Path, new { name = "assistant", expiresInDays = 30 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        var token = created["token"]!.GetValue<string>();
        Assert.StartsWith("opy_op_", token);
        var id = created["id"]!.GetValue<Guid>();
        var hash = await server.QueryAsync(db => db.OperatorTokens.Where(t => t.Id == id).Select(t => t.TokenHash).SingleAsync());
        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain(token, hash);

        using var cli = server.CreateClient();
        cli.BaseAddress = console.BaseAddress;
        cli.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await cli.GetAsync("/api/inventory")).StatusCode);
        var auth = (await cli.GetFromJsonAsync<JsonObject>("/api/auth/state"))!;
        Assert.True(auth["authenticated"]!.GetValue<bool>());
        Assert.Contains("assistant", auth["username"]!.GetValue<string>());
        using (var cleartext = server.CreateClient())
        {
            cleartext.BaseAddress = new Uri("http://localhost:8080");
            cleartext.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(HttpStatusCode.Unauthorized, (await cleartext.GetAsync("/api/inventory")).StatusCode);
        }
        // A stolen automation grant cannot mint replacement grants or manage other grants.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await cli.PostAsJsonAsync(Path, new { name = "replacement", expiresInDays = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await cli.GetAsync(Path)).StatusCode);
        console.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await console.GetAsync("/api/inventory")).StatusCode);
        console.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.OK, (await console.GetAsync("/api/inventory")).StatusCode);
        var listed = await console.GetStringAsync(Path);
        Assert.DoesNotContain(token, listed);
        Assert.DoesNotContain("tokenHash", listed, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NoContent, (await console.DeleteAsync($"{Path}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await cli.GetAsync("/api/inventory")).StatusCode);

        var second = (await (await console.PostAsJsonAsync(Path, new { name = "expiring" })).Content.ReadFromJsonAsync<JsonObject>())!;
        var secondId = second["id"]!.GetValue<Guid>();
        cli.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second["token"]!.GetValue<string>());
        await server.WithDbAsync(db => db.OperatorTokens.Where(t => t.Id == secondId)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.ExpiresAt, DateTimeOffset.UtcNow.AddDays(-1))));
        Assert.Equal(HttpStatusCode.Unauthorized, (await cli.GetAsync("/api/inventory")).StatusCode);

        var third = (await (await console.PostAsJsonAsync(Path, new { name = "self-revoking" })).Content.ReadFromJsonAsync<JsonObject>())!;
        cli.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", third["token"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, (await cli.DeleteAsync($"{Path}/self")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await cli.GetAsync("/api/inventory")).StatusCode);
    }

    [Theory]
    [InlineData("disk-reporting-token")]
    [InlineData("agent-enrollment-token")]
    [InlineData("opy_op_00000000000000000000000000000000_invalid")]
    public async Task Other_credentials_are_not_operator_authority(string token)
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        using var cli = server.CreateClient();
        cli.BaseAddress = new Uri("https://localhost:8080");
        cli.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await cli.GetAsync("/api/inventory")).StatusCode);
    }
}
