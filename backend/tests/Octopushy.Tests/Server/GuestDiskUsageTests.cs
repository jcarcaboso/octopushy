using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Api;
using Octopushy.Server.Data;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class GuestDiskUsageTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Only_a_scoped_token_can_replace_the_latest_reading_and_rotation_and_revocation_take_effect()
    {
        var repository = TestRepository.Minimal();
        const string file = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
        repository.Write(file, File.ReadAllText(Path.Combine(repository.Root, file))
            .Replace("memory: 2048", "memory: 2048, disks: [{ size: 16 }, { size: 64 }]"));
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        await server.SyncAsync();
        var guest = await server.ResourceAsync("home/korriban/krb-app-01");
        var path = $"/api/guests/{guest.Id}/disk-usage";
        using var browser = server.CreateClient();
        browser.BaseAddress = new Uri("https://localhost:8080");
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsJsonAsync(path + "/token", new { })).StatusCode);
        await SignInAsync(server, browser);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsJsonAsync(path + "/token", new { intervalSeconds = 0 })).StatusCode);
        var created = await browser.PostAsJsonAsync(path + "/token", new { intervalSeconds = 300 });
        created.EnsureSuccessStatusCode();
        Assert.True(created.Headers.CacheControl!.NoStore);
        var token = (await created.Content.ReadFromJsonAsync<DiskReporterToken>())!;
        Assert.Equal(64, token.Token.Length);
        Assert.Equal(path, token.ReportPath);
        Assert.Equal(300, token.IntervalSeconds);
        Assert.NotEqual(token.Token, await server.QueryAsync(db =>
            db.GuestDiskUsages.Where(d => d.ResourceId == guest.Id).Select(d => d.TokenHash).SingleAsync()));

        // A logged-in browser cannot submit a measurement without the separate credential.
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsJsonAsync(path, new { usedBytes = 1 })).StatusCode);
        using var reporter = server.CreateClient();
        reporter.BaseAddress = browser.BaseAddress;
        reporter.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await reporter.GetAsync($"/api/guests/{guest.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await reporter.PostAsJsonAsync(path + "/token", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reporter.PostAsJsonAsync(path, new { usedBytes = 4L * 1024 * 1024 * 1024 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reporter.PostAsJsonAsync(path, new { usedBytes = 8L * 1024 * 1024 * 1024 })).StatusCode);
        var detail = await browser.GetFromJsonAsync<JsonObject>($"/api/guests/{guest.Id}");
        var usage = detail!["diskUsage"]!;
        Assert.Equal(16L * 1024 * 1024 * 1024, usage["configuredBytes"]!.GetValue<long>());
        Assert.Equal(8L * 1024 * 1024 * 1024, usage["usedBytes"]!.GetValue<long>());
        var received = usage["receivedAt"]!.GetValue<DateTimeOffset>();
        Assert.Equal(received.AddSeconds(720), usage["staleAfter"]!.GetValue<DateTimeOffset>());
        Assert.True(usage["enabled"]!.GetValue<bool>());
        Assert.Equal(1, await server.QueryAsync(db => db.GuestDiskUsages.CountAsync()));

        var other = new Resource { Id = Guid.NewGuid(), Kind = ResourceKinds.Guest, Address = "home/korriban/other", Lab = "home" };
        await server.WithDbAsync(async db => { db.Resources.Add(other); await db.SaveChangesAsync(); });
        var otherPath = $"/api/guests/{other.Id}/disk-usage";
        (await browser.PostAsJsonAsync(otherPath + "/token", new { })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await reporter.PostAsJsonAsync(otherPath, new { usedBytes = 1 })).StatusCode);

        var rotated = await browser.PostAsJsonAsync(path + "/token", new { });
        var replacement = (await rotated.Content.ReadFromJsonAsync<DiskReporterToken>())!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await reporter.PostAsJsonAsync(path, new { usedBytes = 1 })).StatusCode);
        reporter.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", replacement.Token);
        Assert.Equal(HttpStatusCode.NoContent, (await reporter.PostAsJsonAsync(path, new { usedBytes = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.DeleteAsync(path + "/token")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await reporter.PostAsJsonAsync(path, new { usedBytes = 1 })).StatusCode);
        var saved = await server.QueryAsync(db => db.GuestDiskUsages.SingleAsync(d => d.ResourceId == guest.Id));
        Assert.Null(saved.TokenHash);
        Assert.Equal(0L, saved.UsedBytes);
        Assert.False(GuestDiskUsageEndpoints.View(guest, saved).Enabled);
    }

    [Fact]
    public async Task Reports_require_https_valid_bounded_json_and_an_active_guest()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAsync();
        var guest = await server.ResourceAsync("home/korriban/krb-app-01");
        var path = $"/api/guests/{guest.Id}/disk-usage";
        using var browser = server.CreateClient();
        await SignInAsync(server, browser);
        var created = await browser.PostAsJsonAsync(path + "/token", new { });
        var token = (await created.Content.ReadFromJsonAsync<DiskReporterToken>())!;
        using var reporter = server.CreateClient();
        reporter.BaseAddress = new Uri("https://localhost:8080");
        reporter.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        Assert.Equal(HttpStatusCode.BadRequest, (await reporter.PostAsJsonAsync("http://localhost:8080" + path, new { usedBytes = 1 })).StatusCode);
        foreach (var invalid in new[] { "{}", "null", "[]", "{\"usedBytes\":-1}", "{\"usedBytes\":1.5}", "{\"usedBytes\":\"2\"}",
                     "{\"usedBytes\":9223372036854775808}", "{\"usedBytes\":1,\"unknown\":2}" })
        {
            using var response = await reporter.PostAsync(path, new StringContent(invalid, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        // StreamContent exercises the bound independently of Content-Length.
        using var oversized = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string(' ', 2048))));
        oversized.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await reporter.PostAsync(path, oversized)).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await reporter.PostAsync(path, new StringContent("{\"usedBytes\":1}"))).StatusCode);
        Assert.Null(await server.QueryAsync(db => db.GuestDiskUsages.Where(d => d.ResourceId == guest.Id).Select(d => d.UsedBytes).SingleAsync()));
        await server.WithDbAsync(async db =>
        {
            var resource = await db.Resources.SingleAsync(r => r.Id == guest.Id);
            resource.RetiredAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await reporter.PostAsJsonAsync(path, new { usedBytes = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsJsonAsync(path + "/token", new { })).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"hardware\":null}")]
    [InlineData("{\"hardware\":{\"disks\":[]}}")]
    [InlineData("{\"hardware\":{\"disks\":[{\"size\":\"wrong\"}]}}")]
    [InlineData("{\"hardware\":{\"disks\":[{\"size\":-1}]}}")]
    [InlineData("{\"hardware\":{\"disks\":[{\"size\":9223372036854775807}]}}")]
    public void Missing_or_invalid_capacity_is_unknown_not_zero(string? definition)
    {
        var guest = new Resource { Id = Guid.NewGuid(), Kind = ResourceKinds.Guest, Address = "home/test/vm", Lab = "home", Definition = definition };
        var view = GuestDiskUsageEndpoints.View(guest, null);
        Assert.Null(view.ConfiguredBytes);
        Assert.Null(view.UsedBytes);
        Assert.Null(view.ReceivedAt);
        Assert.False(view.Enabled);
    }

    private static async Task SignInAsync(ServerHarness server, HttpClient browser)
    {
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await browser.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long password" }))
            .EnsureSuccessStatusCode();
    }
}
