using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Server.Auth;

namespace Octopushy.Tests.Server;

/// <summary>Login, the OpenAPI document and the proxy trust on the API port.</summary>
[Collection(PostgresCollection.Name)]
public class FrontDoorTests(PostgresFixture postgres)
{
    private const string Peer = "X-Test-Peer";

    [Fact]
    public async Task The_openapi_document_needs_a_session()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        using var anonymous = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/openapi/v1.json")).StatusCode);

        var client = await server.SignedInClientAsync();
        var document = await client.GetAsync("/api/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        Assert.Contains("/api/auth/login", await document.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_user_name_gets_a_limited_number_of_attempts_from_any_address()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false, services: WithPeerHeader);
        var operatorBrowser = await server.SignedInClientAsync();
        (await operatorBrowser.PostAsync("/api/auth/logout", null)).EnsureSuccessStatusCode();
        using var client = server.CreateClient();

        // Each attempt comes from its own address, so the limit per address never applies; padding still names the user.
        for (var i = 0; i < AccountLoginLimiter.PermitLimit; i++)
        {
            var name = i % 2 == 0 ? "fixture-trust" : "  fixture-trust ";
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, $"10.3.0.{i + 1}", name, "a wrong password")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await LoginAsync(client, "10.3.1.1", "fixture-trust", "synthetic trust password")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, "10.3.1.2", "someone-else", "a wrong password")).StatusCode);

        // The browser the operator signed in from before is counted apart, so the attack does not keep it out.
        Assert.Equal(HttpStatusCode.OK,
            (await LoginAsync(operatorBrowser, "10.3.1.3", "fixture-trust", "synthetic trust password")).StatusCode);
    }

    [Fact]
    public async Task A_forwarded_address_counts_only_from_the_trusted_proxy()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false, services: WithPeerHeader, settings: new()
        {
            ["Octopushy:TrustForwardedHeaders"] = "true",
            ["Octopushy:TrustedProxies:0"] = "10.1.0.0/24",
        });
        using var client = server.CreateClient();

        // A client that reaches the API port itself names a new address each time; it still has one address's attempts.
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
        {
            statuses.Add((await LoginAsync(client, "10.2.0.9", $"user-{i}", "a wrong password", forwardedFor: $"192.0.2.{i + 1}")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);

        // Through the proxy, each forwarded address has its own.
        for (var i = 0; i < 11; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await LoginAsync(client, "10.1.0.5", $"user-{i}", "a wrong password", forwardedFor: $"198.51.100.{i + 1}")).StatusCode);
        }
    }

    [Fact]
    public void The_trusted_proxies_default_to_the_networks_the_api_listens_on()
    {
        (IPAddress, int)[] interfaces = [(IPAddress.Loopback, 8), (IPAddress.Parse("172.18.0.2"), 16), (IPAddress.Parse("172.19.0.2"), 16)];

        Assert.Equal([IPNetwork.Parse("172.18.0.0/16")],
            ApiListener.TrustedNetworks([], [IPAddress.Parse("172.18.0.2")], interfaces));
        Assert.Equal([IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("172.18.0.0/16"), IPNetwork.Parse("172.19.0.0/16")],
            ApiListener.TrustedNetworks([], [IPAddress.Any], interfaces));
        Assert.Equal([IPNetwork.Parse("10.1.0.0/24"), IPNetwork.Parse("10.2.0.7/32")],
            ApiListener.TrustedNetworks(["10.1.0.0/24", "10.2.0.7"], [IPAddress.Parse("172.18.0.2")], interfaces));
    }

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string peer, string username, string password, string? forwardedFor = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { username, password }) };
        request.Headers.Add(Peer, peer);
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return await client.SendAsync(request);
    }

    /// <summary>The test server has no socket, so a header says which address a request connects from.</summary>
    private static void WithPeerHeader(IServiceCollection services) => services.AddSingleton<IStartupFilter, PeerFilter>();

    private sealed class PeerFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, inner) =>
            {
                if (context.Request.Headers.TryGetValue(Peer, out var peer))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(peer.ToString());
                }

                await inner();
            });
            next(app);
        };
    }
}
