using System.Net;
using System.Security.Cryptography;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Octopushy.Server.Auth;
using Octopushy.Server.Store;

namespace Octopushy.Tests.Server;

/// <summary>Sessions that sign-out revokes everywhere, that end however often they are used, and the keys that sign them.</summary>
[Collection(PostgresCollection.Name)]
public class SessionTests(PostgresFixture postgres)
{
    private const string Grants = "/api/auth/operator-tokens";

    [Fact]
    public async Task Signing_out_in_one_browser_ends_the_session_in_another_but_not_an_operator_grant()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        await server.SyncAsync();
        var first = await server.SignedInClientAsync();
        using var second = server.CreateClient();
        (await second.PostAsJsonAsync("/api/auth/login", new { username = "fixture-trust", password = "synthetic trust password" })).EnsureSuccessStatusCode();
        using var cli = await GrantAsync(server, first);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync(Grants)).StatusCode);
        // A new install's key is written encrypted.
        Assert.All(KeyFiles(server.DataDirectory), file => Assert.Contains("urn:octopushy:key-ring:v1", File.ReadAllText(file)));

        // An operator grant signing out is not the browser signing out.
        Assert.Equal(HttpStatusCode.NoContent, (await cli.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync(Grants)).StatusCode);

        (await second.PostAsync("/api/auth/logout", null)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync(Grants)).StatusCode);
        Assert.False((await first.GetFromJsonAsync<JsonObject>("/api/auth/state"))!["authenticated"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, (await cli.GetAsync("/api/inventory")).StatusCode);

        // Signing in again gives a session with the new stamp.
        (await first.PostAsJsonAsync("/api/auth/login", new { username = "fixture-trust", password = "synthetic trust password" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync(Grants)).StatusCode);
    }

    [Fact]
    public async Task A_session_in_use_ends_at_its_absolute_lifetime_and_an_operator_grant_does_not()
    {
        var start = DateTimeOffset.UtcNow;
        var clock = new TestClock { Now = start };
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false, clock: clock);
        await server.SyncAsync();
        var browser = await server.SignedInClientAsync();
        using var cli = await GrantAsync(server, browser);

        // Used every seven hours, so the 12-hour sliding expiry alone would keep it alive.
        for (var hours = 7; hours < 7 * 24; hours += 7)
        {
            clock.Now = start.AddHours(hours);
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(Grants)).StatusCode);
        }

        clock.Now = start.AddDays(7).AddMinutes(1);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync(Grants)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cli.GetAsync("/api/inventory")).StatusCode);
    }

    [Fact]
    public async Task An_unencrypted_key_ring_is_encrypted_in_place_and_keeps_its_keys()
    {
        string knownBrowser = "", stamplessSession = "";
        string[] keys = [];
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false, dataDirectory: data =>
        {
            // What an earlier release wrote: a plain-text ring, a known-browser cookie and a session without a stamp.
            var ring = new DirectoryInfo(Path.Combine(data, "data-protection"));
            var earlier = DataProtectionProvider.Create(ring, builder => builder.SetApplicationName("octopushy"));
            knownBrowser = earlier.CreateProtector("octopushy.known-browser").Protect("fixture-trust");
            var tickets = new TicketDataFormat(earlier.CreateProtector("Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationMiddleware", "Cookies", "v2"));
            stamplessSession = tickets.Protect(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "fixture-trust"), new Claim(ClaimTypes.NameIdentifier, "1")], "Cookies")),
                new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) },
                "Cookies"));
            keys = KeyFiles(data);
            Assert.Contains("<value>", File.ReadAllText(Assert.Single(keys)));
        });

        AssertEncrypted(server, keys);
        var protection = server.Services.GetRequiredService<IDataProtectionProvider>();
        Assert.Equal("fixture-trust", protection.CreateProtector("octopushy.known-browser").Unprotect(knownBrowser));

        // The account is user 1, so the old session would name it, but it carries no stamp.
        _ = await server.SignedInClientAsync();
        using var old = server.CreateClient();
        old.DefaultRequestHeaders.Add("Cookie", $"octopushy_session={stamplessSession}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await old.GetAsync(Grants)).StatusCode);

        // The encrypted ring reads back after a restart with the same keys, and no new one is made.
        var sealedBefore = protection.CreateProtector("test").Protect("value");
        await server.RestartFixtureAsync();
        AssertEncrypted(server, keys);
        protection = server.Services.GetRequiredService<IDataProtectionProvider>();
        Assert.Equal("value", protection.CreateProtector("test").Unprotect(sealedBefore));
        Assert.Equal("fixture-trust", protection.CreateProtector("octopushy.known-browser").Unprotect(knownBrowser));
        Assert.Equal(keys, KeyFiles(server.DataDirectory));
    }

    [Fact]
    public void A_key_the_current_kek_cannot_open_is_reported_and_left_as_it_is()
    {
        var data = Path.Combine(Path.GetTempPath(), "octopushy-ring-" + Guid.NewGuid().ToString("N"));
        try
        {
            DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(data, "data-protection")), b => b.SetApplicationName("octopushy"))
                .CreateProtector("test").Protect("value");
            var ring = Path.Combine(data, "data-protection");
            var logger = new ErrorLogger();
            KeyRingCipher.EncryptExisting(ring, new KeyEncryptionKey(RandomNumberGenerator.GetBytes(KeyEncryptionKey.Size)), logger);
            var encrypted = File.ReadAllText(Assert.Single(KeyFiles(data)));
            Assert.Empty(logger.Errors);

            KeyRingCipher.EncryptExisting(ring, new KeyEncryptionKey(RandomNumberGenerator.GetBytes(KeyEncryptionKey.Size)), logger);
            Assert.Contains("cannot be decrypted with the current key-encryption key", Assert.Single(logger.Errors));
            Assert.Equal(encrypted, File.ReadAllText(Assert.Single(KeyFiles(data))));
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    private sealed class ErrorLogger : ILogger
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                Errors.Add(formatter(state, exception));
            }
        }
    }

    private static string[] KeyFiles(string data) =>
        Directory.GetFiles(Path.Combine(data, "data-protection"), "*.xml").Order().ToArray();

    private static void AssertEncrypted(ServerHarness server, string[] keys)
    {
        Assert.Equal(keys, KeyFiles(server.DataDirectory));
        foreach (var file in keys)
        {
            var xml = File.ReadAllText(file);
            Assert.DoesNotContain("<value>", xml);
            Assert.DoesNotContain("masterKey", xml);
            Assert.Contains("urn:octopushy:key-ring:v1", xml);
        }
    }

    private static async Task<HttpClient> GrantAsync(ServerHarness server, HttpClient console)
    {
        var created = (await (await console.PostAsJsonAsync(Grants, new { name = "cli", expiresInDays = 30 }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonObject>())!;
        var cli = server.CreateClient();
        cli.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created["token"]!.GetValue<string>());
        return cli;
    }
}
