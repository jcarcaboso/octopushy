using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class RequestOriginTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("same-origin", null, true)]
    [InlineData("same-origin", "http://attacker.test", true)]
    [InlineData(null, "http://localhost:8080", true)]
    [InlineData(null, "HTTP://LOCALHOST:8080", true)]
    [InlineData("cross-site", "http://localhost:8080", false)]
    [InlineData("same-site", "http://localhost:8080", false)]
    [InlineData("none", "http://localhost:8080", false)]
    [InlineData("unknown", "http://localhost:8080", false)]
    [InlineData("same-origin, same-site", "http://localhost:8080", false)]
    [InlineData(null, null, false)]
    [InlineData(null, "null", false)]
    [InlineData(null, "http://attacker.localhost:8080", false)]
    [InlineData(null, "https://localhost:8080", false)]
    [InlineData(null, "http://localhost:8081", false)]
    [InlineData(null, "http://localhost:8080.attacker.test", false)]
    [InlineData(null, "http://localhost:8080, http://attacker.test", false)]
    [InlineData(null, "http://localhost:8080/path", false)]
    [InlineData(null, "http://localhost:8080#fragment", false)]
    [InlineData(null, "http://user@localhost:8080", false)]
    public async Task Cookie_authenticated_writes_require_the_same_origin(string? fetchSite, string? origin, bool allowed)
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        var client = await server.SignedInClientAsync();
        client.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        if (fetchSite is not null) request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", fetchSite);
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);

        var response = await client.SendAsync(request);

        Assert.Equal(allowed ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden, response.StatusCode);
        // A rejected logout must not clear the operator's session.
        Assert.Equal(allowed ? HttpStatusCode.Unauthorized : HttpStatusCode.OK,
            (await client.GetAsync("/api/inventory")).StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/auth/logout")]
    [InlineData("PUT", "/api/variables")]
    [InlineData("PATCH", "/api/agents/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/api/agents/00000000-0000-0000-0000-000000000001")]
    public async Task Every_unsafe_method_rejects_cross_origin_cookie_requests(string method, string path)
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        var client = await server.SignedInClientAsync();
        client.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("Sec-Fetch-Site", "same-site");
        request.Headers.Add("Origin", "http://attacker.localhost:8080");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Cookie_authenticated_reads_do_not_require_origin_headers()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        var client = await server.SignedInClientAsync();
        client.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/inventory")).StatusCode);
    }

    [Fact]
    public async Task Operator_tokens_are_exempt_but_cannot_bypass_a_cookie_only_endpoints_origin_check()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        var console = await server.SignedInClientAsync();
        var created = await console.PostAsJsonAsync("/api/auth/operator-tokens", new { name = "origin-test" });
        created.EnsureSuccessStatusCode();
        var token = (await created.Content.ReadFromJsonAsync<JsonObject>())!["token"]!.GetValue<string>();
        using var cli = server.CreateClient();
        cli.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
        cli.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var value = new { key = "origin-test", value = "test", visibility = "plain" };
        Assert.Equal(HttpStatusCode.NoContent, (await cli.PutAsJsonAsync("/api/variables", value)).StatusCode);
        cli.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        cli.DefaultRequestHeaders.Add("Origin", "http://attacker.test");
        Assert.Equal(HttpStatusCode.NoContent, (await cli.PutAsJsonAsync("/api/variables", value)).StatusCode);

        // This route explicitly authenticates the cookie, even with a valid Bearer header.
        console.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        console.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
        console.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-site");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await console.PostAsJsonAsync("/api/auth/operator-tokens", new { name = "forged" })).StatusCode);
    }
}
