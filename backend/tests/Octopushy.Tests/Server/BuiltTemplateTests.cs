using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Definitions;
using Octopushy.Server.Api;
using Octopushy.Server.Data;
using Octopushy.Server.Images;
using Octopushy.Server.Orchestration;
using Octopushy.Tests.Definitions;

namespace Octopushy.Tests.Server;

/// <summary>
/// Built templates on the server: a build per key, queued by sync and handed to an agent with <c>nix</c>, its image uploaded
/// and kept, and the provision runs that waited for it. See docs/built-templates.md.
/// </summary>
[Collection(PostgresCollection.Name)]
public class BuiltTemplateTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    private static readonly byte[] ImageBytes = RandomNumberGenerator.GetBytes(ImageStore.ChunkBytes + 1000);
    private static readonly string ImageSha256 = Convert.ToHexStringLower(SHA256.HashData(ImageBytes));

    [Fact]
    public async Task A_sync_queues_one_build_per_key_and_the_guest_waits_for_its_image()
    {
        var repository = BuiltTemplateFixtures.WithBuiltTemplate();
        // A built template no guest uses is not built.
        repository.Write("templates/unused/template.yaml", """
            schemaVersion: 1
            source:
              build: { workflow: test-vm@1, output: other-image }
            """);
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        await server.SyncAndTrustAsync();
        await server.SyncAsync();

        var build = Assert.Single(await server.QueryAsync(db => db.ImageBuilds.AsNoTracking().ToListAsync()));
        var key = LabRepositoryLoader.Load(repository.Root).Templates["nixos-base"].Build!.Key;
        Assert.Equal((key, "nixos-base", ImageBuildStatuses.Queued, 1, "templates/nixos-base/flake.lock"),
            (build.Key, build.TemplateId, build.Status, build.AttemptCount, build.LockPath));
        var attempt = Assert.Single(await server.QueryAsync(db => db.Attempts.AsNoTracking().Where(a => a.Executor == "nix").ToListAsync()));
        Assert.Equal((build.AttemptId, "build", false, (Guid?)null, (Guid?)null), (attempt.Id, attempt.Action, attempt.Mutating, attempt.StepId, attempt.ResourceId));

        var guest = await server.ResourceAsync(Guest);
        Assert.Equal("Waiting for the image of template nixos-base.", guest.ProvisionWaiting);
        Assert.Equal((GuestStatuses.Pending, "Waiting for the image of template nixos-base."), Views.GuestStatus(guest, null, null));
        Assert.False(await server.QueryAsync(db => db.Runs.AnyAsync(r => r.ResourceId == guest.Id)));
    }

    [Fact]
    public async Task A_build_goes_only_to_an_agent_with_nix_and_a_free_slot_with_no_labels_or_reachability()
    {
        await using var server = await ServerHarness.StartAsync(postgres, BuiltTemplateFixtures.WithBuiltTemplate());
        await server.SyncAndTrustAsync();
        var tofu = await server.ConnectAgentAsync("home");
        await server.TickAsync();
        Assert.DoesNotContain(tofu.TakeAssignments(), a => a.Executor == "nix");
        Assert.Equal("Waiting for a connected agent advertising executor nix.", (await BuildAttemptAsync(server)).WaitingReason);

        var full = await server.ConnectNixAgentAsync("full", maxConcurrency: 0);
        await server.TickAsync();
        Assert.Empty(full.TakeAssignments());
        Assert.Equal("Waiting for agent capacity.", (await BuildAttemptAsync(server)).WaitingReason);

        // No labels, no capability to report reachability, and no probe: a build connects to nothing in the lab.
        var nix = await server.ConnectNixAgentAsync();
        var assignment = await nix.NextAsync("build");
        Assert.Empty(nix.TakeProbes());
        Assert.Equal("nix", assignment.Executor);
        Assert.Empty(assignment.Secrets);
        var build = await server.QueryAsync(db => db.ImageBuilds.AsNoTracking().SingleAsync());
        Assert.Equal(ImageBuildStatuses.Building, build.Status);
        var input = JsonNode.Parse(assignment.InputJson)!.AsObject();
        Assert.Equal(
            $$"""{"key":"{{build.Key}}","flake":"modules/nixos","output":"base-image","system":"x86_64-linux","lock":"templates/nixos-base/flake.lock"}""",
            input.ToJsonString());
        var files = Entries(assignment.FilesTarGz);
        Assert.Contains("templates/nixos-base/flake.lock", files);
        Assert.Contains("modules/nixos/flake.nix", files);
        Assert.Equal(AttemptStatuses.Assigned, (await BuildAttemptAsync(server)).Status);
    }

    [Fact]
    public async Task An_upload_is_taken_only_from_the_building_agent_whole_and_matching_its_digest()
    {
        await using var server = await ServerHarness.StartAsync(postgres, BuiltTemplateFixtures.WithBuiltTemplate());
        await server.SyncAndTrustAsync();
        var nix = await server.ConnectNixAgentAsync();
        var other = await server.ConnectNixAgentAsync("other");
        var assignment = await nix.NextAsync("build");
        var attemptId = Guid.Parse(assignment.AttemptId);
        await server.Engine.OnStartedAsync(nix.Id, attemptId, CancellationToken.None);

        await Assert.ThrowsAsync<ImageAccessException>(() =>
            server.Engine.AuthorizeImageUploadAsync(other.Id, attemptId, "nixos.qcow2", ImageBytes.Length, CancellationToken.None));
        await Assert.ThrowsAsync<ImageAccessException>(() =>
            server.Engine.AuthorizeImageUploadAsync(nix.Id, Guid.NewGuid(), "nixos.qcow2", ImageBytes.Length, CancellationToken.None));
        await Assert.ThrowsAsync<ImageUploadException>(() =>
            server.Engine.AuthorizeImageUploadAsync(nix.Id, attemptId, "../nixos.qcow2", ImageBytes.Length, CancellationToken.None));

        var store = server.Services.GetRequiredService<ImageStore>();
        var wrongDigest = await Assert.ThrowsAsync<ImageUploadException>(() => UploadAsync(server, nix, attemptId, ImageBytes, sha256: new string('0', 64)));
        Assert.Contains("SHA-256", wrongDigest.Message);
        var wrongSize = await Assert.ThrowsAsync<ImageUploadException>(() => UploadAsync(server, nix, attemptId, ImageBytes, size: ImageBytes.Length + 1));
        Assert.Contains("declared", wrongSize.Message);
        var tooLarge = await Assert.ThrowsAsync<ImageUploadException>(() => UploadAsync(server, nix, attemptId, ImageBytes, size: ImageBytes.Length - 1));
        Assert.Contains("larger than", tooLarge.Message);
        Assert.False(store.Has(ImageSha256));
        Assert.Null((await server.QueryAsync(db => db.ImageBuilds.AsNoTracking().SingleAsync())).Sha256);

        await UploadAsync(server, nix, attemptId, ImageBytes);
        Assert.True(store.Has(ImageSha256));
        Assert.Equal(ImageBytes, await File.ReadAllBytesAsync(store.PathOf(ImageSha256)));
        var build = await server.QueryAsync(db => db.ImageBuilds.AsNoTracking().SingleAsync());
        Assert.Equal((ImageSha256, (long?)ImageBytes.Length, "nixos.qcow2", (Guid?)attemptId, ImageBuildStatuses.Building),
            (build.Sha256, build.Size, build.FileName, build.UploadAttemptId, build.Status));
    }

    [Fact]
    public async Task A_built_image_proposes_the_waiting_provision_run_whose_plan_may_fetch_exactly_that_image()
    {
        await using var server = await ServerHarness.StartAsync(postgres, BuiltTemplateFixtures.WithBuiltTemplate());
        await server.SyncAndTrustAsync();
        var nix = await server.ConnectNixAgentAsync();
        var assignment = await nix.NextAsync("build");
        var attemptId = Guid.Parse(assignment.AttemptId);
        await server.Engine.OnStartedAsync(nix.Id, attemptId, CancellationToken.None);
        await UploadAsync(server, nix, attemptId, ImageBytes);
        Assert.True(await nix.ReportAsync(assignment, Outcome.Succeeded, Outputs()));

        var build = await server.QueryAsync(db => db.ImageBuilds.AsNoTracking().SingleAsync());
        Assert.Equal((ImageBuildStatuses.Built, (string?)null), (build.Status, build.Error));
        Assert.NotNull(build.BuiltAt);
        var guest = await server.ResourceAsync(Guest);
        Assert.Null(guest.ProvisionWaiting);
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        var image = TemplateImage.Of(ImageSha256, ImageBytes.Length, "nixos.qcow2");
        Assert.Equal(image, RunContext.Parse(run.Context).OpenTofu!.TemplateImage);
        Assert.Equal($"{ImageSha256[..12]}-nixos.qcow2", image.FileName);

        var tofu = await server.ConnectAgentAsync("home");
        var plan = await tofu.NextAsync("preview");
        var input = JsonNode.Parse(plan.InputJson)!;
        Assert.Equal($$"""{"sha256":"{{ImageSha256}}","fileName":"{{image.FileName}}","size":{{ImageBytes.Length}}}""", input["templateImage"]!.ToJsonString());
        var source = input["variables"]!["template"]!["source"]!;
        Assert.Equal(("built", ImageSha256, image.FileName), (source["kind"]!.GetValue<string>(), source["sha256"]!.GetValue<string>(), source["fileName"]!.GetValue<string>()));

        var planAttempt = Guid.Parse(plan.AttemptId);
        Assert.Equal(image, await server.Engine.AuthorizeImageDownloadAsync(tofu.Id, planAttempt, ImageSha256, CancellationToken.None));
        await Assert.ThrowsAsync<ImageAccessException>(() =>
            server.Engine.AuthorizeImageDownloadAsync(tofu.Id, planAttempt, new string('b', 64), CancellationToken.None));
        await Assert.ThrowsAsync<ImageAccessException>(() =>
            server.Engine.AuthorizeImageDownloadAsync(nix.Id, planAttempt, ImageSha256, CancellationToken.None));
        // A build attempt names no image to fetch.
        await Assert.ThrowsAsync<ImageAccessException>(() =>
            server.Engine.AuthorizeImageDownloadAsync(nix.Id, attemptId, ImageSha256, CancellationToken.None));

        // The image a run needs stays, and the next sync builds nothing for the same key.
        await server.SyncAsync();
        Assert.True(server.Services.GetRequiredService<ImageStore>().Has(ImageSha256));
        Assert.Equal(1, await server.QueryAsync(db => db.Attempts.CountAsync(a => a.Executor == "nix")));
    }

    [Fact]
    public async Task A_build_whose_outputs_do_not_match_its_upload_fails_and_waits_for_a_person_to_retry_it()
    {
        await using var server = await ServerHarness.StartAsync(postgres, BuiltTemplateFixtures.WithBuiltTemplate());
        var http = await SignedInAsync(server);
        await server.SyncAndTrustAsync(http);
        var nix = await server.ConnectNixAgentAsync();
        var assignment = await nix.NextAsync("build");
        var attemptId = Guid.Parse(assignment.AttemptId);
        await server.Engine.OnStartedAsync(nix.Id, attemptId, CancellationToken.None);
        await UploadAsync(server, nix, attemptId, ImageBytes);
        var outputs = Outputs();
        outputs["size"] = 1;
        await nix.ReportAsync(assignment, Outcome.Succeeded, outputs);

        var build = await server.QueryAsync(db => db.ImageBuilds.AsNoTracking().SingleAsync());
        Assert.Equal(ImageBuildStatuses.Failed, build.Status);
        Assert.Contains("do not match the image it uploaded", build.Error);
        Assert.Equal(AttemptStatuses.Failed, (await BuildAttemptAsync(server)).Status);
        // What it uploaded was not its image, and nothing else names it.
        Assert.False(server.Services.GetRequiredService<ImageStore>().Has(ImageSha256));

        await server.SyncAsync();
        Assert.Equal(1, await server.QueryAsync(db => db.Attempts.CountAsync(a => a.Executor == "nix")));
        var templates = await http.GetFromJsonAsync<JsonArray>("/api/templates");
        var template = templates!.Single(t => t!["id"]!.GetValue<string>() == "nixos-base")!;
        Assert.Equal(("built", "failed"), (template["kind"]!.GetValue<string>(), template["build"]!["status"]!.GetValue<string>()));

        var retried = await http.PostAsync("/api/templates/nixos-base/build", null);
        retried.EnsureSuccessStatusCode();
        Assert.Equal("queued", (await retried.Content.ReadFromJsonAsync<JsonObject>())!["build"]!["status"]!.GetValue<string>());
        Assert.Equal(2, await server.QueryAsync(db => db.Attempts.CountAsync(a => a.Executor == "nix")));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, (await http.PostAsync("/api/templates/nixos-base/build", null)).StatusCode);
    }

    [Fact]
    public async Task An_image_that_would_not_fit_fails_its_build_with_image_store_full()
    {
        await using var server = await ServerHarness.StartAsync(postgres, BuiltTemplateFixtures.WithBuiltTemplate(),
            settings: new() { ["Octopushy:Images:MaxBytes"] = "1000" });
        await server.SyncAndTrustAsync();
        var nix = await server.ConnectNixAgentAsync();
        var assignment = await nix.NextAsync("build");
        var attemptId = Guid.Parse(assignment.AttemptId);

        var full = await Assert.ThrowsAsync<ImageUploadException>(() =>
            server.Engine.AuthorizeImageUploadAsync(nix.Id, attemptId, "nixos.qcow2", 1001, CancellationToken.None));
        Assert.True(full.Full);
        var build = await server.QueryAsync(db => db.ImageBuilds.AsNoTracking().SingleAsync());
        Assert.Equal((ImageBuildStatuses.Failed, "image store full"), (build.Status, build.Error));
    }

    [Fact]
    public async Task A_lost_build_is_retried_as_a_read_only_step_is_then_fails()
    {
        var clock = new TestClock { Now = DateTimeOffset.UtcNow };
        await using var server = await ServerHarness.StartAsync(postgres, BuiltTemplateFixtures.WithBuiltTemplate(), clock: clock);
        await server.SyncAndTrustAsync();
        var nix = await server.ConnectNixAgentAsync();
        await nix.NextAsync("build");
        for (var expired = 1; expired <= 3; expired++)
        {
            clock.Now += TimeSpan.FromMinutes(5);
            await server.TickAsync();
            Assert.Equal(expired < 3 ? 1 : 0, nix.TakeAssignments().Count(a => a.Action == "build"));
        }

        var build = await server.QueryAsync(db => db.ImageBuilds.AsNoTracking().SingleAsync());
        Assert.Equal((ImageBuildStatuses.Failed, "The agent stopped reporting, 3 times.", 3), (build.Status, build.Error, build.AttemptCount));
    }

    [Fact]
    public async Task The_rpcs_take_an_image_from_its_builder_and_give_it_to_the_plan_that_needs_it_in_chunks()
    {
        await using var server = await ServerHarness.StartAsync(postgres, BuiltTemplateFixtures.WithBuiltTemplate());
        await server.SyncAndTrustAsync();
        var nix = await server.ConnectNixAgentAsync();
        var assignment = await nix.NextAsync("build");
        var attemptId = Guid.Parse(assignment.AttemptId);
        await server.Engine.OnStartedAsync(nix.Id, attemptId, CancellationToken.None);
        var service = ActivatorUtilities.CreateInstance<Octopushy.Server.Agents.AgentChannelService>(server.Services);
        var nixCall = await CallAsAsync(server, nix);

        var chunks = ImageBytes.Chunk(ImageStore.ChunkBytes).Select((data, i) => new ImageChunk
        {
            AttemptId = i == 0 ? assignment.AttemptId : "",
            FileName = i == 0 ? "nixos.qcow2" : "",
            Size = i == 0 ? (ulong)ImageBytes.Length : 0,
            Data = ByteString.CopyFrom(data),
        }).ToList();
        chunks[^1].Sha256 = ImageSha256;
        // Another agent, or no certificate, is refused before a byte is kept.
        var other = await server.ConnectNixAgentAsync("other");
        var otherCall = await CallAsAsync(server, other);
        var refused = await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => service.UploadImage(new Chunks(chunks), otherCall));
        Assert.Equal(Grpc.Core.StatusCode.PermissionDenied, refused.StatusCode);
        var anonymous = await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => service.UploadImage(new Chunks(chunks), new TestCallContext(DefaultHttpContextWith(null))));
        Assert.Equal(Grpc.Core.StatusCode.Unauthenticated, anonymous.StatusCode);
        var corrupt = chunks.Select(c => c.Clone()).ToList();
        corrupt[^1].Sha256 = new string('0', 64);
        Assert.Equal(Grpc.Core.StatusCode.InvalidArgument,
            (await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => service.UploadImage(new Chunks(corrupt), nixCall))).StatusCode);

        var uploaded = await service.UploadImage(new Chunks(chunks), nixCall);
        Assert.Equal((ImageSha256, (ulong)ImageBytes.Length), (uploaded.Sha256, uploaded.Size));
        await nix.ReportAsync(assignment, Outcome.Succeeded, Outputs());

        var tofu = await server.ConnectAgentAsync("home");
        var plan = await tofu.NextAsync("preview");
        var tofuCall = await CallAsAsync(server, tofu);
        var received = new Received();
        await service.DownloadImage(new DownloadImageRequest { AttemptId = plan.AttemptId, Sha256 = ImageSha256 }, received, tofuCall);
        Assert.Equal(2, received.Chunks.Count);
        Assert.Equal(($"{ImageSha256[..12]}-nixos.qcow2", (ulong)ImageBytes.Length, ""), (received.Chunks[0].FileName, received.Chunks[0].Size, received.Chunks[0].Sha256));
        Assert.Equal(ImageSha256, received.Chunks[^1].Sha256);
        Assert.All(received.Chunks, c => Assert.True(c.Data.Length <= ImageStore.ChunkBytes));
        Assert.Equal(ImageBytes, received.Chunks.SelectMany(c => c.Data.ToByteArray()).ToArray());

        var denied = await Assert.ThrowsAsync<Grpc.Core.RpcException>(() =>
            service.DownloadImage(new DownloadImageRequest { AttemptId = plan.AttemptId, Sha256 = ImageSha256 }, new Received(), nixCall));
        Assert.Equal(Grpc.Core.StatusCode.PermissionDenied, denied.StatusCode);
    }

    /// <summary>A call as the agent, with a client certificate the server's CA issued it.</summary>
    private static async Task<Grpc.Core.ServerCallContext> CallAsAsync(ServerHarness server, FakeAgent agent)
    {
        var ca = server.Services.GetRequiredService<Octopushy.Server.Agents.AgentCertificateAuthority>();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=agent", key, HashAlgorithmName.SHA256);
        var (der, serial, _) = ca.IssueAgentCertificate(request.CreateSigningRequest(), agent.Id);
        await server.WithDbAsync(db => db.Agents.Where(a => a.Id == agent.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.CertificateSerial, serial)));
        return new TestCallContext(DefaultHttpContextWith(System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(der)));
    }

    private static Microsoft.AspNetCore.Http.HttpContext DefaultHttpContextWith(System.Security.Cryptography.X509Certificates.X509Certificate2? certificate) =>
        new Microsoft.AspNetCore.Http.DefaultHttpContext { Connection = { ClientCertificate = certificate } };

    private sealed class TestCallContext : Grpc.Core.ServerCallContext
    {
        public TestCallContext(Microsoft.AspNetCore.Http.HttpContext http) => UserState["__HttpContext"] = http;

        protected override string MethodCore => "test";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "test";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Grpc.Core.Metadata RequestHeadersCore => [];
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Grpc.Core.Metadata ResponseTrailersCore => [];
        protected override Grpc.Core.Status StatusCore { get; set; }
        protected override Grpc.Core.WriteOptions? WriteOptionsCore { get; set; }
        protected override Grpc.Core.AuthContext AuthContextCore => new(null, []);
        protected override Grpc.Core.ContextPropagationToken CreatePropagationTokenCore(Grpc.Core.ContextPropagationOptions? options) =>
            throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Grpc.Core.Metadata responseHeaders) => Task.CompletedTask;
    }

    private sealed class Chunks(IReadOnlyList<ImageChunk> chunks) : Grpc.Core.IAsyncStreamReader<ImageChunk>
    {
        private int _next = -1;

        public ImageChunk Current => chunks[_next];

        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromResult(++_next < chunks.Count);
    }

    private sealed class Received : Grpc.Core.IServerStreamWriter<ImageChunk>
    {
        public List<ImageChunk> Chunks { get; } = [];

        public Grpc.Core.WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(ImageChunk message)
        {
            Chunks.Add(message.Clone());
            return Task.CompletedTask;
        }
    }

    private static JsonObject Outputs() => new()
    {
        ["sha256"] = ImageSha256,
        ["size"] = ImageBytes.Length,
        ["fileName"] = "nixos.qcow2",
        ["outPath"] = "/nix/store/0000-base-image",
    };

    /// <summary>Uploads as the agent's RPC does: let in, received in chunks, then recorded; refused uploads are released.</summary>
    private static async Task UploadAsync(ServerHarness server, FakeAgent agent, Guid attemptId, byte[] bytes, string? sha256 = null, long? size = null)
    {
        var store = server.Services.GetRequiredService<ImageStore>();
        var declared = size ?? bytes.Length;
        await server.Engine.AuthorizeImageUploadAsync(agent.Id, attemptId, "nixos.qcow2", declared, CancellationToken.None);
        try
        {
            var chunks = bytes.Chunk(ImageStore.ChunkBytes).Select((data, i) => new ImageChunk
            {
                AttemptId = i == 0 ? attemptId.ToString() : "",
                FileName = i == 0 ? "nixos.qcow2" : "",
                Size = i == 0 ? (ulong)declared : 0,
                Data = ByteString.CopyFrom(data),
            }).ToList();
            chunks[^1].Sha256 = sha256 ?? Convert.ToHexStringLower(SHA256.HashData(bytes));
            var next = 1;
            var digest = await store.ReceiveAsync(attemptId, declared, chunks[0],
                _ => Task.FromResult(next < chunks.Count ? chunks[next++] : null), CancellationToken.None);
            await server.Engine.CompleteImageUploadAsync(agent.Id, attemptId, digest, declared, "nixos.qcow2", CancellationToken.None);
        }
        catch
        {
            store.Release(attemptId);
            throw;
        }
    }

    private static Task<Attempt> BuildAttemptAsync(ServerHarness server) =>
        server.QueryAsync(db => db.Attempts.AsNoTracking().Where(a => a.Executor == "nix").OrderByDescending(a => a.CreatedAt).FirstAsync());

    private static async Task<HttpClient> SignedInAsync(ServerHarness server)
    {
        var http = server.CreateClient();
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await http.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        return http;
    }

    private static List<string> Entries(ByteString tarGz)
    {
        using var gzip = new GZipStream(new MemoryStream(tarGz.ToByteArray()), CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var names = new List<string>();
        while (tar.GetNextEntry() is { } entry)
            names.Add(entry.Name);
        return names;
    }
}
