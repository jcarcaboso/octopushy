using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Server.Data;
using Services = Octopushy.Tests.Definitions.ServiceDefinitionTests;

namespace Octopushy.Tests.Server;

/// <summary>
/// The icon the Services page shows for a service: a picture uploaded for it, a file of the icons directory chosen for it, or
/// the file named after it. See docs/service-icons.md.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ServiceIconTests(PostgresFixture postgres)
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F'];

    private static readonly byte[] Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1 1\"/>"u8.ToArray();

    private static readonly DateTimeOffset Start = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private sealed record Lab(ServerHarness Server, HttpClient Client, TestClock Clock, string Icons, Guid Service) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Server.DisposeAsync();

        public void Write(string name, byte[]? content = null) => File.WriteAllBytes(Path.Combine(Icons, name), content ?? Svg);

        /// <summary>Lets the cached listing of the icons directory go stale.</summary>
        public void Later() => Clock.Now = Clock.Now!.Value.AddSeconds(31);

        public async Task<JsonNode?> IconAsync(string name = "web")
        {
            var inventory = (await Client.GetFromJsonAsync<JsonObject>("/api/inventory"))!;
            return inventory["labs"]![0]!["installations"]![0]!["guests"]![0]!["services"]!.AsArray()
                .Single(s => s!["name"]!.GetValue<string>() == name)!["icon"];
        }

        public Task<HttpResponseMessage> UploadAsync(byte[] content, string type, Guid? service = null)
        {
            var body = new ByteArrayContent(content);
            body.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
            return Client.PutAsync($"/api/services/{service ?? Service}/icon/picture", body);
        }

        public Task<HttpResponseMessage> ChooseAsync(string name, Guid? service = null) =>
            Client.PutAsJsonAsync($"/api/services/{service ?? Service}/icon/predefined", new { name });
    }

    private async Task<Lab> StartAsync(Action<IServiceCollection>? services = null, Action<TestRepository>? repository = null)
    {
        var clock = new TestClock { Now = Start };
        var test = Services.Repository();
        repository?.Invoke(test);
        var server = await ServerHarness.StartAsync(postgres, test, clock: clock, services: services);
        await server.SyncAndTrustAsync();
        var icons = Path.Combine(server.DataDirectory, "icons");
        Directory.CreateDirectory(icons);
        return new Lab(server, await server.SignedInClientAsync(), clock, icons, (await server.ResourceAsync(Services.Address)).Id);
    }

    [Fact]
    public async Task A_picture_comes_first_then_the_chosen_file_then_the_file_named_after_the_service()
    {
        await using var lab = await StartAsync();
        Assert.Null(await lab.IconAsync());

        lab.Write("web.png", Png);
        lab.Write("web.svg");
        lab.Write("other.svg");
        lab.Later();
        var named = (await lab.IconAsync())!;
        Assert.Equal(("/api/icons/web.svg", "name"), (named["url"]!.GetValue<string>(), named["source"]!.GetValue<string>()));

        Assert.Equal(HttpStatusCode.NoContent, (await lab.ChooseAsync("other.svg")).StatusCode);
        var chosen = (await lab.IconAsync())!;
        Assert.Equal(("/api/icons/other.svg", "chosen"), (chosen["url"]!.GetValue<string>(), chosen["source"]!.GetValue<string>()));

        Assert.Equal(HttpStatusCode.NoContent, (await lab.UploadAsync(Png, "image/png")).StatusCode);
        var picture = (await lab.IconAsync())!;
        Assert.Equal("picture", picture["source"]!.GetValue<string>());
        var url = picture["url"]!.GetValue<string>();
        Assert.Equal($"/api/services/{lab.Service}/icon?v={lab.Clock.Now!.Value.ToUnixTimeMilliseconds()}", url);

        // A new picture is a new URL; choosing a file again drops the picture.
        lab.Clock.Now = lab.Clock.Now.Value.AddSeconds(1);
        Assert.Equal(HttpStatusCode.NoContent, (await lab.UploadAsync(Jpeg, "image/jpeg")).StatusCode);
        Assert.NotEqual(url, (await lab.IconAsync())!["url"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, (await lab.ChooseAsync("other.svg")).StatusCode);
        Assert.Equal("chosen", (await lab.IconAsync())!["source"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await lab.Client.GetAsync($"/api/services/{lab.Service}/icon")).StatusCode);

        // A chosen file that is gone is skipped; the next of the service's own names is used.
        File.Delete(Path.Combine(lab.Icons, "other.svg"));
        File.Delete(Path.Combine(lab.Icons, "web.svg"));
        lab.Later();
        Assert.Equal("/api/icons/web.png", (await lab.IconAsync())!["url"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.NoContent, (await lab.Client.DeleteAsync($"/api/services/{lab.Service}/icon")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await lab.Client.DeleteAsync($"/api/services/{lab.Service}/icon")).StatusCode);
        Assert.Equal("name", (await lab.IconAsync())!["source"]!.GetValue<string>());
        Assert.Equal(0, await lab.Server.QueryAsync(db => db.ServiceIcons.CountAsync()));

        File.Delete(Path.Combine(lab.Icons, "web.png"));
        lab.Later();
        Assert.Null(await lab.IconAsync());
    }

    [Fact]
    public async Task A_picture_and_a_file_are_served_from_this_origin_as_documents_that_run_nothing()
    {
        await using var lab = await StartAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await lab.UploadAsync(Svg, "image/svg+xml; charset=utf-8")).StatusCode);
        var picture = await lab.Client.GetAsync((await lab.IconAsync())!["url"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
        Assert.Equal("image/svg+xml", picture.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Svg, await picture.Content.ReadAsByteArrayAsync());
        Assert.Equal("default-src 'none'; style-src 'unsafe-inline'; sandbox", picture.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", picture.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(picture.Headers.CacheControl!.Private);
        Assert.Equal(TimeSpan.FromDays(365), picture.Headers.CacheControl.MaxAge);

        lab.Write("web.png", Png);
        lab.Write("a-b_c.1.jpeg", Jpeg);
        lab.Write("notes.txt");
        lab.Write(".hidden.svg");
        lab.Write("empty.svg", []);
        Directory.CreateDirectory(Path.Combine(lab.Icons, "folder.svg"));
        File.CreateSymbolicLink(Path.Combine(lab.Icons, "link.svg"), Path.Combine(lab.Icons, "web.png"));
        lab.Later();
        var names = (await lab.Client.GetFromJsonAsync<JsonObject>("/api/icons"))!["names"]!.AsArray().Select(n => n!.GetValue<string>());
        Assert.Equal(["a-b_c.1.jpeg", "web.png"], names);

        var file = await lab.Client.GetAsync("/api/icons/web.png");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("image/png", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Png, await file.Content.ReadAsByteArrayAsync());
        Assert.Equal("default-src 'none'; style-src 'unsafe-inline'; sandbox", file.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(file.Headers.CacheControl!.Private);
        Assert.Equal(TimeSpan.FromHours(1), file.Headers.CacheControl.MaxAge);
        Assert.Equal("image/jpeg", (await lab.Client.GetAsync("/api/icons/a-b_c.1.jpeg")).Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task A_picture_that_is_not_what_it_says_too_large_or_for_no_service_is_refused()
    {
        await using var lab = await StartAsync();
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await lab.UploadAsync(Png, "image/gif")).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await lab.UploadAsync(Png, "application/octet-stream")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await lab.UploadAsync(Jpeg, "image/png")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await lab.UploadAsync(Png, "image/webp")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await lab.UploadAsync(Png, "image/svg+xml")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await lab.UploadAsync("<html><script>alert(1)</script></html>"u8.ToArray(), "image/svg+xml")).StatusCode);
        var withDtd = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY x \"y\">]><svg xmlns=\"http://www.w3.org/2000/svg\">&x;</svg>");
        Assert.Equal(HttpStatusCode.BadRequest, (await lab.UploadAsync(withDtd, "image/svg+xml")).StatusCode);
        var large = new byte[1024 * 1024 + 1];
        Png.CopyTo(large, 0);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await lab.UploadAsync(large, "image/png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await lab.UploadAsync(Png, "image/png", Guid.NewGuid())).StatusCode);
        Assert.Equal(0, await lab.Server.QueryAsync(db => db.ServiceIcons.CountAsync()));

        var webp = "RIFF\0\0\0\0WEBPVP8 "u8.ToArray();
        Assert.Equal(HttpStatusCode.NoContent, (await lab.UploadAsync(webp, "image/webp")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await lab.UploadAsync(new byte[1024 * 1024].Select((_, i) => i < Png.Length ? Png[i] : (byte)0).ToArray(), "image/png")).StatusCode);
    }

    [Fact]
    public async Task Only_a_file_of_the_icons_directory_can_be_chosen_or_served()
    {
        await using var lab = await StartAsync();
        lab.Write("x.svg");
        lab.Write("x.txt");
        File.WriteAllBytes(Path.Combine(lab.Server.DataDirectory, "outside.svg"), Svg);
        Directory.CreateDirectory(Path.Combine(lab.Icons, "a"));
        File.WriteAllBytes(Path.Combine(lab.Icons, "a", "b.svg"), Svg);
        lab.Later();

        Assert.Equal(HttpStatusCode.OK, (await lab.Client.GetAsync("/api/icons/x.svg")).StatusCode);
        foreach (var path in new[] { "/api/icons/x.txt", "/api/icons/missing.svg", "/api/icons/..%2Foutside.svg", "/api/icons/a%2Fb.svg",
                     "/api/icons/a%2fb.svg", "/api/icons/%2E%2E%2Foutside.svg", "/api/icons/../icons/a/b.svg", "/api/icons/.x.svg" })
            Assert.Equal(HttpStatusCode.NotFound, (await lab.Client.GetAsync(path)).StatusCode);

        foreach (var name in new[] { "missing.svg", "../outside.svg", "a/b.svg", "x.txt", "a" })
            Assert.Equal(HttpStatusCode.BadRequest, (await lab.ChooseAsync(name)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await lab.ChooseAsync("x.svg", Guid.NewGuid())).StatusCode);
        Assert.Equal(0, await lab.Server.QueryAsync(db => db.ServiceIcons.CountAsync()));

        // Swapped for a link while the listing still names it: the link is not followed out of the directory.
        File.Delete(Path.Combine(lab.Icons, "x.svg"));
        File.CreateSymbolicLink(Path.Combine(lab.Icons, "x.svg"), Path.Combine(lab.Server.DataDirectory, "outside.svg"));
        Assert.Equal(HttpStatusCode.NotFound, (await lab.Client.GetAsync("/api/icons/x.svg")).StatusCode);
    }

    [Fact]
    public async Task The_inventory_reads_the_icons_of_a_guests_services_at_once_without_their_pictures()
    {
        var commands = new Commands();
        await using var lab = await StartAsync(
            services => services.ConfigureDbContext<OctopushyDb>(options => options.AddInterceptors(commands)),
            test => test.Write(Services.ServiceFile.Replace("/web/", "/api/"), Services.Service));
        var other = (await lab.Server.ResourceAsync(Services.Guest + "/api")).Id;
        Assert.Equal(HttpStatusCode.NoContent, (await lab.UploadAsync(Png, "image/png")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await lab.UploadAsync(Png, "image/png", other)).StatusCode);

        commands.Texts.Clear();
        Assert.Equal("picture", (await lab.IconAsync("api"))!["source"]!.GetValue<string>());
        var reads = commands.Texts.Where(t => t.Contains("service_icons")).ToList();
        Assert.Single(reads);
        Assert.DoesNotContain(".picture", reads[0]);
    }

    private sealed class Commands : DbCommandInterceptor
    {
        public List<string> Texts { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            lock (Texts)
                Texts.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
