using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Options;
using Octopushy.Server.Store;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class DiskReporterProvisioningTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    private static Dictionary<string, string> Settings => new()
    {
        ["Octopushy:DiskReporter:Enabled"] = "true",
        ["Octopushy:DiskReporter:PublicUrl"] = "https://octopushy.test",
        ["Octopushy:DiskReporter:BinaryUrl"] = "https://releases.test/disk-reporter",
        ["Octopushy:DiskReporter:BinarySha256"] = new string('a', 64),
    };

    [Fact]
    public void Enabled_configuration_requires_safe_urls_a_pinned_release_and_a_bounded_interval()
    {
        new DiskReporterOptions().Validate(); // Existing deployments remain opt-in.
        var config = new DiskReporterOptions
        {
            Enabled = true,
            PublicUrl = "https://octopushy.test",
            BinaryUrl = "https://releases.test/reporter",
            BinarySha256 = new string('a', 64),
        };
        config.Validate();
        foreach (var url in new[] { "http://octopushy.test", "https://user:pass@octopushy.test", "https://octopushy.test/subpath", "https://octopushy.test?query" })
        {
            config.PublicUrl = url;
            Assert.Throws<InvalidOperationException>(config.Validate);
        }
        config.PublicUrl = "https://octopushy.test";
        config.BinarySha256 = "not-a-checksum";
        Assert.Throws<InvalidOperationException>(config.Validate);
        config.BinarySha256 = new string('a', 64);
        config.IntervalSeconds = 59;
        Assert.Throws<InvalidOperationException>(config.Validate);
        config.IntervalSeconds = 3600;
        config.CaCertificatePem = "-----BEGIN PRIVATE KEY-----";
        Assert.Throws<InvalidOperationException>(config.Validate);
    }

    [Fact]
    public async Task Untrusted_revisions_never_create_or_release_reporter_credentials()
    {
        await using var server = await ServerHarness.StartAsync(postgres, settings: Settings);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAsync();
        await server.TickAsync();
        Assert.Empty(agent.TakeAssignments());
        Assert.Equal(0, await server.QueryAsync(db => db.GuestDiskUsages.CountAsync()));
        Assert.Equal(0, await server.QueryAsync(db => db.SecretResolutions.CountAsync()));
        await server.SyncAndTrustAsync();
        await agent.NextAsync("preview");
    }

    [Fact]
    public async Task Provision_installs_only_after_verification_and_the_guest_can_report()
    {
        using var repository = TestRepository.Minimal();
        repository.Write(GuestFile, File.ReadAllText(Path.Combine(repository.Root, GuestFile))
            + "\nsetup: { diskReporter: { intervalSeconds: 1800 } }\n");
        // Even a recipe without verification must establish guest readiness for setup.
        repository.Write("recipes/test-vm/recipe.yaml",
            "schemaVersion: 1\noperation: provision\nexecutor: opentofu\nroot: ../../modules/opentofu/test-vm\nverify: none\n");
        await using var server = await ServerHarness.StartAsync(postgres, repository, settings: Settings);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var plan = await agent.NextAsync("preview");
        Assert.DoesNotContain("disk-reporter.token", plan.Secrets.Keys);
        Assert.Null(JsonNode.Parse(plan.InputJson)!["variables"]!["guest"]!["setup"]);
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        var apply = await agent.NextAsync("apply");
        Assert.DoesNotContain("disk-reporter.token", apply.Secrets.Keys);
        await agent.ReportAsync(apply, Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        var verify = await agent.NextAsync("verify-guest");
        Assert.DoesNotContain("disk-reporter.token", verify.Secrets.Keys);
        Assert.Equal(0, await server.QueryAsync(db => db.GuestDiskUsages.CountAsync()));
        await agent.ReportAsync(verify, Outcome.Succeeded, new JsonObject());

        var install = await agent.NextAsync("install-disk-reporter");
        Assert.Equal("guest", install.Executor);
        Assert.True(await server.QueryAsync(db => db.Attempts.Where(a => a.Id == Guid.Parse(install.AttemptId)).Select(a => a.Mutating).SingleAsync()));
        Assert.Equal(2, install.Secrets.Count);
        Assert.Contains("proxmox.token", install.Secrets.Keys);
        var token = install.Secrets["disk-reporter.token"];
        Assert.Equal(64, token.Length);
        var resource = await server.ResourceAsync(Guest);
        var input = JsonNode.Parse(install.InputJson)!;
        Assert.Equal($"https://octopushy.test/api/guests/{resource.Id}/disk-usage", input["reporter"]!["endpoint"]!.GetValue<string>());
        Assert.Equal(1800, input["reporter"]!["intervalSeconds"]!.GetValue<int>());
        Assert.Equal(new string('a', 64), input["reporter"]!["binarySha256"]!.GetValue<string>());
        Assert.DoesNotContain(token, install.InputJson);
        var releasedVersion = await server.QueryAsync(db => db.SecretResolutions
            .Where(r => r.AttemptId == Guid.Parse(install.AttemptId) && r.Key == GuestReporterCredentials.Key(resource.Id))
            .Select(r => r.Version).SingleAsync());
        Assert.Equal(1, releasedVersion);
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.DoesNotContain(token, run.Context);
        Assert.NotEqual(RunStatuses.Succeeded, run.Status);
        Assert.True(install.FilesTarGz.IsEmpty);
        Assert.Empty(install.Artifacts);

        using var client = server.CreateClient();
        client.BaseAddress = new Uri("https://localhost:8080");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.PostAsJsonAsync($"/api/guests/{resource.Id}/disk-usage", new { usedBytes = 123456L });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(123456, await server.QueryAsync(db => db.GuestDiskUsages.Select(d => d.UsedBytes).SingleAsync()));
        await agent.ReportAsync(install, Outcome.Succeeded, new JsonObject { ["installed"] = true, ["firstReportSent"] = true });
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
    }

    [Fact]
    public async Task Failed_setup_can_be_repaired_with_a_no_change_plan_and_reuses_its_token()
    {
        await using var server = await ServerHarness.StartAsync(postgres, settings: Settings);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan());
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        var install = await agent.NextAsync("install-disk-reporter");
        await agent.ReportAsync(install, Outcome.Failed, error: "guest prerequisites missing");
        await server.TickAsync();
        Assert.Equal(RunStatuses.Failed, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
        var resource = await server.ResourceAsync(Guest);
        await using var scope = server.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<VariableStore>();
        var before = await store.GetAsync(GuestReporterCredentials.Key(resource.Id), CancellationToken.None);
        await server.Services.GetRequiredService<Planner>().PlanGuestAsync(resource.Id, CancellationToken.None);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan());
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        var retry = await agent.NextAsync("install-disk-reporter");
        Assert.Equal(install.Secrets["disk-reporter.token"], retry.Secrets["disk-reporter.token"]);
        Assert.Equal(before, await store.GetAsync(GuestReporterCredentials.Key(resource.Id), CancellationToken.None));
        await agent.ReportAsync(retry, Outcome.Succeeded);
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal(StepStatuses.Skipped, run.Steps.Single(s => s.Name == StepNames.Apply).Status);
        Assert.Contains("setup completed", run.Detail);
    }

    [Fact]
    public async Task Setup_only_runs_still_require_manual_approval_before_creating_credentials()
    {
        using var repository = TestRepository.Minimal();
        repository.Write("labs/home/lab.yaml", "schemaVersion: 1\nexecution: { agentLabels: [home] }\npolicy: { approval: manual }\n");
        await using var server = await ServerHarness.StartAsync(postgres, repository, settings: Settings);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan());
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Equal(0, await server.QueryAsync(db => db.GuestDiskUsages.CountAsync()));
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        Assert.Equal(64, approval.PlanSha256?.Length);
        Assert.Contains("inside the VM", approval.Detail);
        await Assert.ThrowsAsync<RunException>(() => server.Engine.ApproveAsync(run.Id, approval.Id, new string('0', 64), "ops", null, CancellationToken.None));
        await server.Engine.ApproveAsync(run.Id, approval.Id, approval.PlanSha256!, "ops", null, CancellationToken.None);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        await agent.NextAsync("install-disk-reporter");
    }

    [Fact]
    public async Task Failed_verification_never_creates_a_token_or_executes_setup()
    {
        await using var server = await ServerHarness.StartAsync(postgres, settings: Settings);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan());
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Failed, error: "guest agent unavailable");
        await server.TickAsync();
        Assert.Equal(RunStatuses.Failed, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
        Assert.Equal(0, await server.QueryAsync(db => db.GuestDiskUsages.CountAsync()));
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "install-disk-reporter");
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task Disabled_or_retired_guests_have_no_install_step(bool globallyEnabled, bool optedOut, bool retired)
    {
        using var repository = TestRepository.Minimal();
        repository.Write(GuestFile, File.ReadAllText(Path.Combine(repository.Root, GuestFile))
            + (optedOut ? "\nsetup: { diskReporter: { enabled: false } }\n" : "")
            + (retired ? "\nlifecycle: retired\n" : ""));
        var settings = Settings;
        settings["Octopushy:DiskReporter:Enabled"] = globallyEnabled.ToString();
        await using var server = await ServerHarness.StartAsync(postgres, repository, settings: settings);
        await server.SyncAndTrustAsync();
        var run = await server.LatestRunAsync(Guest);
        Assert.DoesNotContain(run.Steps, s => s.Name == StepNames.InstallDiskReporter);
        Assert.Null(RunContext.Parse(run.Context).DiskReporter);
    }

    [Fact]
    public async Task Lost_install_lease_is_uncertain_and_never_automatically_retried()
    {
        await using var server = await ServerHarness.StartAsync(postgres, settings: Settings);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan());
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        var install = await agent.NextAsync("install-disk-reporter");
        await server.WithDbAsync(async db =>
        {
            var attempt = await db.Attempts.SingleAsync(a => a.Id == Guid.Parse(install.AttemptId));
            attempt.LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        });
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Uncertain, run.Status);
        Assert.Single(run.Steps.Single(s => s.Name == StepNames.InstallDiskReporter).Attempts);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "install-disk-reporter");
    }
}
