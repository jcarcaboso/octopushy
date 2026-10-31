using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Server.Api;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Options;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class WorkerDeploymentTests(PostgresFixture postgres)
{
    private const string Path = "/api/managed-worker-preparation";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authenticated_review_publishes_exact_versioned_files_once_without_trust_approval_or_secret_release(bool qualifiedReportingImage)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var remote = new TestRepository();
        var git = server.Services.GetRequiredService<GitCli>();
        await git.RunAsync(["clone", "--bare", "--quiet", server.Repository.Root, remote.Root]);
        await server.WithDbAsync(async db =>
        {
            (await db.RepositorySettings.SingleAsync()).Url = remote.Root;
            await db.SaveChangesAsync();
        });
        await server.SyncAsync();
        Enable(server);
        var reporter = server.Services.GetRequiredService<IOptions<OctopushyOptions>>().Value.DiskReporter;
        reporter.Enabled = true;
        reporter.PublicUrl = "https://web.test";
        reporter.BinaryUrl = "https://releases.test/reporter";
        reporter.BinarySha256 = new string('c', 64);
        reporter.ManagedNixosImageSha256 = new string(qualifiedReportingImage ? 'a' : 'b', 64);
        using var client = await InstallationTrustTests.AuthenticateAsync(server);
        var view = (await client.GetFromJsonAsync<WorkerPreparationDto>(Path))!;
        Assert.Null(view.DeploymentBlockedReason);
        var draft = Request(view);
        using var anonymous = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Path + "/review", draft)).StatusCode);
        var reviewResponse = await client.PostAsJsonAsync(Path + "/review", draft);
        reviewResponse.EnsureSuccessStatusCode();
        var review = (await reviewResponse.Content.ReadFromJsonAsync<WorkerReviewDto>())!;
        Assert.False(review.AlreadyExists);
        Assert.NotEqual(Guid.Empty, review.Generation);
        Assert.Contains("modules/opentofu/octopushy-worker-v1/.terraform.lock.hcl", review.Files.Keys);
        Assert.Contains("templates/octopushy-worker-v1/template.yaml", review.Files.Keys);
        var worker = JsonNode.Parse(review.Files["labs/home/proxmox/korriban/guests/worker-01/guest.yaml"])!;
        Assert.Equal(qualifiedReportingImage, worker["setup"]!["diskReporter"]!["enabled"]!.GetValue<bool>());
        Assert.DoesNotContain(review.Files.Values, f => f.Contains("secret-token") || f.Contains("PRIVATE KEY") || f.Contains("opy1."));
        Assert.Contains(review.Files.Values, f => f.Contains("worker-generation:"));
        var initial = view.HeadCommit!;
        Assert.Equal(initial, (await git.RunAsync(["rev-parse", "main"], remote.Root)).Trim());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path + "/publish",
            new WorkerPublishRequest(draft, review.ReviewSha256, false))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path + "/publish",
            new WorkerPublishRequest(draft, "wrong", true))).StatusCode);
        var request = new WorkerPublishRequest(draft, review.ReviewSha256, true);
        var publish = await client.PostAsJsonAsync(Path + "/publish", request);
        publish.EnsureSuccessStatusCode();
        var result = (await publish.Content.ReadFromJsonAsync<WorkerPublishedDto>())!;
        Assert.False(result.AlreadyPresent);
        Assert.NotEqual(initial, result.Commit);
        var repeated = await client.PostAsJsonAsync(Path + "/publish", request);
        repeated.EnsureSuccessStatusCode();
        Assert.True((await repeated.Content.ReadFromJsonAsync<WorkerPublishedDto>())!.AlreadyPresent);
        Assert.Equal(result.Commit, (await git.RunAsync(["rev-parse", "main"], remote.Root)).Trim());
        await server.SyncAsync();
        var current = (await client.GetFromJsonAsync<WorkerPreparationDto>(Path))!;
        Assert.Single(current.ExistingWorkers);
        var loaded = await server.Services.GetRequiredService<RevisionCache>().GetAsync(result.Commit, default);
        Assert.DoesNotContain(loaded.Issues, i => i.Severity == IssueSeverity.Error);
        Assert.Equal(2, loaded.Guests.Count);
        Assert.Equal(review.Generation, loaded.Guest("home/korriban/worker-01")!.ManagedWorker!.Generation);
        await server.TickAsync();
        await server.WithDbAsync(async db =>
        {
            Assert.Empty(await db.SecretResolutions.ToListAsync());
            Assert.Empty(await db.ManagedWorkers.ToListAsync());
            Assert.Empty(await db.EnrollmentTokens.ToListAsync());
        });
        var existsResponse = await client.PostAsJsonAsync(Path + "/review", Request(current));
        existsResponse.EnsureSuccessStatusCode();
        Assert.True((await existsResponse.Content.ReadFromJsonAsync<WorkerReviewDto>())!.AlreadyExists);
    }

    [Fact]
    public async Task Review_refuses_disabled_image_missing_ingress_stale_revision_and_extra_fields()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        await server.SyncAsync();
        using var client = await InstallationTrustTests.AuthenticateAsync(server);
        var options = (await client.GetFromJsonAsync<WorkerPreparationDto>(Path))!;
        Assert.NotNull(options.DeploymentBlockedReason);
        var draft = Request(options);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path + "/review", draft)).StatusCode);
        Enable(server);
        var config = server.Services.GetRequiredService<IOptions<OctopushyOptions>>().Value;
        config.AgentChannel.PublicUrl = "https://127.0.0.1:8443";
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path + "/review", draft)).StatusCode);
        config.AgentChannel.PublicUrl = "https://controller.example.invalid:8443";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path + "/review",
            new { selection = draft.Selection, stateBackend = draft.StateBackend, endpoint = "https://attacker.invalid" })).StatusCode);
        var bad = draft with { Selection = draft.Selection with { RevisionId = Guid.NewGuid() } };
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path + "/review", bad)).StatusCode);
        config.WorkerDeployment.ImageUrl = "https://host/image?token=forbidden";
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path + "/review", draft)).StatusCode);
    }

    [Fact]
    public async Task A_retired_workers_name_is_reused_with_a_new_generation_but_not_while_removal_is_pending()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAsync();
        Enable(server);
        using var client = await InstallationTrustTests.AuthenticateAsync(server);
        var draft = Request((await client.GetFromJsonAsync<WorkerPreparationDto>(Path))!);
        var original = (await (await client.PostAsJsonAsync(Path + "/review", draft)).Content.ReadFromJsonAsync<WorkerReviewDto>())!;

        var id = Guid.CreateVersion7();
        await server.WithDbAsync(async db =>
        {
            db.Resources.Add(new Resource
            {
                Id = id, Address = original.Address, Kind = ResourceKinds.Guest, ParentAddress = "home/korriban", Lab = "home",
                MissingSince = DateTimeOffset.UtcNow,
                ManagedWorker = new()
                {
                    ResourceId = id, Generation = original.Generation, Installation = "home/korriban", Host = "korriban",
                    Vmid = 901, RevisionId = draft.Selection!.RevisionId, RunId = Guid.NewGuid(), ServerUrl = "https://controller.test",
                },
            });
            await db.SaveChangesAsync();
        });
        var pending = await client.PostAsJsonAsync(Path + "/review", draft);
        Assert.Equal(HttpStatusCode.Conflict, pending.StatusCode);
        Assert.Contains("removal has not finished", await pending.Content.ReadAsStringAsync());

        await server.WithDbAsync(async db =>
        {
            (await db.Resources.SingleAsync(r => r.Id == id)).RetiredAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        });
        var reused = await client.PostAsJsonAsync(Path + "/review", draft);
        reused.EnsureSuccessStatusCode();
        var review = (await reused.Content.ReadFromJsonAsync<WorkerReviewDto>())!;
        Assert.False(review.AlreadyExists);
        Assert.NotEqual(original.Generation, review.Generation);
    }

    private static void Enable(ServerHarness server)
    {
        var config = server.Services.GetRequiredService<IOptions<OctopushyOptions>>().Value;
        config.WorkerDeployment.RepositoryWritesEnabled = true;
        config.WorkerDeployment.ImageUrl = "https://images.example.invalid/worker-v1.qcow2";
        config.WorkerDeployment.ImageSha256 = new string('a', 64);
    }

    [Fact]
    public async Task A_worker_needs_no_state_backend_and_an_unknown_one_is_refused()
    {
        using var repository = TestRepository.BuiltInState();
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        await server.SyncAsync();
        Enable(server);
        using var client = await InstallationTrustTests.AuthenticateAsync(server);
        var view = (await client.GetFromJsonAsync<WorkerPreparationDto>(Path))!;
        Assert.Empty(view.StateBackends);

        var response = await client.PostAsJsonAsync(Path + "/review", new { selection = Request(view).Selection });
        response.EnsureSuccessStatusCode();
        var review = (await response.Content.ReadFromJsonAsync<WorkerReviewDto>())!;
        var worker = JsonNode.Parse(review.Files["labs/home/proxmox/korriban/guests/worker-01/guest.yaml"])!;
        Assert.Equal("octopushy-worker-v1", worker["provision"]!["recipe"]!.GetValue<string>());
        Assert.Null(worker["provision"]!["state"]);

        var unknown = await client.PostAsJsonAsync(Path + "/review", Request(view) with { StateBackend = "nowhere" });
        Assert.False(unknown.IsSuccessStatusCode);
    }

    private static WorkerDeploymentRequest Request(WorkerPreparationDto view) => new(
        new(view.Revision!.Id, "home/korriban", "korriban", "worker-01", 901, 1, 2, 2048, 8, 16, "local-lvm", "vmbr0", null), "postgres");
}
