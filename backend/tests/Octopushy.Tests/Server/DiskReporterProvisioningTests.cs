using Microsoft.Extensions.Configuration;
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

    [Theory]
    [InlineData(false, 3600)]
    [InlineData(true, 1800)]
    public async Task Unsupported_native_reporting_is_local_to_one_guest_and_can_be_corrected(bool qualified, int interval)
    {
        using var repository = Definitions.ManagedWorkerDefinitionTests.Repository();
        using var ordinary = TestRepository.Minimal();
        var original = File.ReadAllText(Path.Combine(repository.Root, GuestFile));
        repository.Write(GuestFile, original + $"\nsetup: {{ diskReporter: {{ enabled: true, intervalSeconds: {interval} }} }}\n");
        repository.Write(GuestFile.Replace("krb-app-01", "unrelated"),
            File.ReadAllText(Path.Combine(ordinary.Root, GuestFile)).Replace("vmid: 310", "vmid: 311"));
        var settings = Settings;
        if (qualified)
            settings["Octopushy:DiskReporter:ManagedNixosImageSha256"] = new string('a', 64);
        await using var server = await ServerHarness.StartAsync(postgres, repository, settings: settings);
        using var client = await InstallationTrustTests.AuthenticateAsync(server);

        await server.SyncAsync();
        var inventory = (await client.GetFromJsonAsync<JsonObject>("/api/inventory"))!;
        var guests = inventory["labs"]![0]!["installations"]![0]!["guests"]!.AsArray();
        var worker = guests.Single(g => g!["address"]!.GetValue<string>() == Guest)!;
        Assert.False(worker["valid"]!.GetValue<bool>());
        var peer = guests.Single(g => g!["address"]!.GetValue<string>() == "home/korriban/unrelated")!;
        Assert.True(peer["valid"]!.GetValue<bool>());
        Assert.Equal("provision", peer["activeRun"]!["operation"]!.GetValue<string>());
        var workerId = worker["id"]!.GetValue<Guid>();
        var detail = (await client.GetFromJsonAsync<JsonObject>($"/api/guests/{workerId}"))!;
        Assert.Contains(detail["issues"]!.AsArray(), i =>
            i!["severity"]!.GetValue<string>() == "error"
            && i["message"]!.GetValue<string>().Contains("reporting", StringComparison.OrdinalIgnoreCase));
        Assert.Null(worker["activeRun"]);
        Assert.Null(worker["lastRun"]);

        repository.Write(GuestFile, original + "\nsetup: { diskReporter: { enabled: false } }\n");
        server.Commit("Correct unsupported reporting");
        await server.SyncAsync();
        detail = (await client.GetFromJsonAsync<JsonObject>($"/api/guests/{workerId}"))!;
        Assert.True(detail["guest"]!["valid"]!.GetValue<bool>());
        Assert.Equal("provision", detail["guest"]!["activeRun"]!["operation"]!.GetValue<string>());
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Untrusted_installations_never_create_or_release_reporter_credentials(bool native)
    {
        using var repository = native ? Definitions.ManagedWorkerDefinitionTests.Repository() : TestRepository.Minimal();
        var settings = Settings;
        settings["Octopushy:DiskReporter:ManagedNixosImageSha256"] = new string('a', 64);
        await using var server = await ServerHarness.StartAsync(postgres, repository, settings: settings);
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
        Assert.Equal("octopushy@pve!writer=writer-token", install.Secrets["proxmox.token"]);
        Assert.DoesNotContain(plan.Secrets["proxmox.token"], install.Secrets.Values);
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
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(), artifacts: FakeAgent.PlanArtifact());
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
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(), artifacts: FakeAgent.PlanArtifact());
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

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Setup_only_runs_require_manual_approval_and_a_saved_plan_before_creating_credentials(bool shared, bool artifact)
    {
        using var repository = TestRepository.Minimal();
        repository.Write("labs/home/lab.yaml", "schemaVersion: 1\nexecution: { agentLabels: [home] }\npolicy: { approval: manual }\n");
        if (shared)
        {
            const string installation = "labs/home/proxmox/korriban/installation.yaml";
            repository.Write(installation, File.ReadAllText(Path.Combine(repository.Root, installation))
                .Replace("readOnlyToken: { store: proxmox/korriban/read-only-token }\n  privilegedToken: { store: proxmox/korriban/privileged-token }",
                    "token: { store: proxmox/korriban/shared-token }"));
        }
        await using var server = await ServerHarness.StartAsync(postgres, repository, settings: Settings);
        if (shared)
            await server.SetSecretAsync("proxmox/korriban/shared-token", "shared-token");
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var preview = await agent.NextAsync("preview");
        Assert.True(JsonNode.Parse(preview.InputJson)!["retainNoChangesPlan"]!.GetValue<bool>());
        await agent.ReportAsync(preview, Outcome.Succeeded, FakeAgent.Plan(),
            artifacts: artifact ? [FakeAgent.PlanArtifact()] : []);
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(0, await server.QueryAsync(db => db.GuestDiskUsages.CountAsync()));
        Assert.Equal(0, await server.QueryAsync(db => db.SecretResolutions.CountAsync(r => r.Key == "proxmox/korriban/privileged-token")));
        Assert.Empty(agent.TakeAssignments());
        if (!artifact)
        {
            Assert.Equal(RunStatuses.Failed, run.Status);
            Assert.Contains("no saved plan file", run.Detail);
            return;
        }
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        Assert.Equal(FakeAgent.PlanArtifact().Sha256, approval.PlanSha256);
        Assert.Contains("inside the VM", approval.Detail);
        await Assert.ThrowsAsync<RunException>(() => server.Engine.ApproveAsync(run.Id, approval.Id, new string('0', 64), "ops", null, CancellationToken.None));
        await server.Engine.ApproveAsync(run.Id, approval.Id, approval.PlanSha256!, "ops", null, CancellationToken.None);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        var install = await agent.NextAsync("install-disk-reporter");
        Assert.Equal(shared ? "shared-token" : "octopushy@pve!writer=writer-token", install.Secrets["proxmox.token"]);
        Assert.Contains(SecretNames.DiskReporterToken, install.Secrets.Keys);
    }

    [Fact]
    public async Task Failed_verification_never_creates_a_token_or_executes_setup()
    {
        await using var server = await ServerHarness.StartAsync(postgres, settings: Settings);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(), artifacts: FakeAgent.PlanArtifact());
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
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(), artifacts: FakeAgent.PlanArtifact());
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

/// <summary>Public run-construction seam; no database or operational state.</summary>
public class NativeDiskReporterRunTests
{
    private static (Run Run, OctopushyOptions Options) Create(bool qualified, bool enabled = true, bool optedOut = false, int interval = 3600)
    {
        using var repo = Definitions.ManagedWorkerDefinitionTests.Repository();
        if (optedOut || interval != 3600)
            repo.Write(Definitions.ManagedWorkerDefinitionTests.GuestPath,
                File.ReadAllText(Path.Combine(repo.Root, Definitions.ManagedWorkerDefinitionTests.GuestPath))
                + $"\nsetup: {{ diskReporter: {{ enabled: {(!optedOut).ToString().ToLowerInvariant()}, intervalSeconds: {interval} }} }}\n");
        var options = new OctopushyOptions
        {
            AgentChannel = { PublicUrl = "https://agent.test:8443" },
            DiskReporter = { Enabled = enabled, PublicUrl = "https://web.test", BinaryUrl = "https://releases.test/reporter", BinarySha256 = new string('b', 64) },
        };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DiskReporter:ManagedNixosImageSha256"] = qualified ? new string('a', 64) : null,
            }).Build();
        Microsoft.Extensions.Configuration.ConfigurationBinder.Bind(config, options);
        var repository = Octopushy.Definitions.LabRepositoryLoader.Load(repo.Root);
        var guest = repository.Guests.Single();
        var run = new RunFactory(Microsoft.Extensions.Options.Options.Create(options), TimeProvider.System).Guest(repository, guest,
            new Resource { Id = Guid.NewGuid(), Kind = ResourceKinds.Guest, Address = guest.Address, Lab = guest.Lab },
            new DefinitionRevision { Commit = "fixture" }, "test");
        return (run, options);
    }

    [Fact]
    public void Qualified_managed_image_freezes_native_mode_image_provenance_and_ownership_not_download_hash()
    {
        var (run, options) = Create(true);
        var context = JsonNode.Parse(run.Context)!;
        Assert.Equal("managed-nixos-v1", context["diskReporter"]!["installationMode"]!.GetValue<string>());
        Assert.Equal(new string('a', 64), context["diskReporter"]!["imageSha256"]!.GetValue<string>());
        Assert.Null(context["diskReporter"]!["binarySha256"]);
        Assert.Null(context["diskReporter"]!["binaryUrl"]);
        Assert.Equal("https://web.test/api/guests/" + run.ResourceId + "/disk-usage", context["diskReporter"]!["endpoint"]!.GetValue<string>());
        Assert.Equal(Definitions.ManagedWorkerDefinitionTests.Generation, context["managedWorker"]!["generation"]!.GetValue<string>());
        Assert.DoesNotContain("disk-reporter.token", run.Context);
        Assert.DoesNotContain(new string('b', 64), run.Steps.Single(s => s.Name == StepNames.InstallDiskReporter).Detail);
        Assert.Equal("manual", RunContext.Parse(run.Context).Policy!.Approval);
        options.DiskReporter.PublicUrl = "https://changed.test";
        Assert.Contains("https://web.test", run.Context);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Old_images_global_disable_and_explicit_opt_out_never_select_fhs_for_a_managed_worker(bool qualified, bool enabled, bool optedOut)
    {
        var (run, _) = Create(qualified, enabled, optedOut);
        Assert.Null(RunContext.Parse(run.Context).DiskReporter);
        Assert.DoesNotContain(run.Steps, s => s.Name == StepNames.InstallDiskReporter);
    }

    [Fact]
    public void Native_image_cannot_record_an_interval_other_than_the_image_supported_hour() =>
        Assert.Throws<RunException>(() => Create(true, interval: 1800));
}
