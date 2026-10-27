using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class ManagedWorkerLifecycleTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    private static async Task TrustAsync(ServerHarness server)
    {
        using var http = server.CreateClient();
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await http.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        await server.SyncAndTrustAsync(http);
    }
    private static async Task ApproveAsync(ServerHarness server)
    {
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        await server.Engine.ApproveAsync(run.Id, approval.Id, approval.PlanSha256!, "ops", null, CancellationToken.None,
            RunContext.Parse(run.Context).WorkerUpdate?.ArchiveSha256);
    }
    private static JsonObject Csr(ECDsa key) => new()
    {
        ["protocolVersion"] = 1,
        ["generation"] = Definitions.ManagedWorkerDefinitionTests.Generation,
        ["csrDer"] = Convert.ToBase64String(new CertificateRequest("CN=worker", key, HashAlgorithmName.SHA256).CreateSigningRequest()),
        ["agentVersion"] = "test",
    };

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_noop_plan_needs_approval_and_readiness_requires_the_correlated_authenticated_worker(bool diskReporter, bool update)
    {
        using var repo = Definitions.ManagedWorkerDefinitionTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repo, settings: diskReporter ? new()
        {
            ["Octopushy:DiskReporter:Enabled"] = "true",
            ["Octopushy:DiskReporter:PublicUrl"] = "https://octopushy.test",
            ["Octopushy:DiskReporter:BinaryUrl"] = "https://releases.test/disk-reporter",
            ["Octopushy:DiskReporter:BinarySha256"] = new string('b', 64),
            ["Octopushy:DiskReporter:ManagedNixosImageSha256"] = new string('a', 64),
        } : null);
        var control = await server.ConnectAgentAsync("home");
        await TrustAsync(server);
        var preview = await control.NextAsync("preview");
        Assert.True(JsonNode.Parse(preview.InputJson)!["retainNoChangesPlan"]!.GetValue<bool>());
        await control.ReportAsync(preview, Outcome.Succeeded, FakeAgent.Plan(), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Empty(control.TakeAssignments());
        Assert.Equal(0, await server.QueryAsync(db => db.GuestDiskUsages.CountAsync()));
        await ApproveAsync(server);
        await control.ReportAsync(await control.NextAsync("verify-guest"), Outcome.Succeeded);
        await control.ReportAsync(await control.NextAsync("grow"), Outcome.Succeeded);
        Assert.Equal(0, await server.QueryAsync(db => db.GuestDiskUsages.CountAsync()));
        await control.ReportAsync(await control.NextAsync("verify"), Outcome.Succeeded);
        var prepare = await control.NextAsync("prepare");
        Assert.Equal("managed-worker", prepare.Executor);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await control.ReportAsync(prepare, Outcome.Succeeded, Csr(key));
        var install = await control.NextAsync("install");
        var publicIdentity = JsonNode.Parse(install.InputJson)!["identity"]!;
        Assert.DoesNotContain("PRIVATE KEY", install.InputJson);
        Assert.DoesNotContain("opy1.", install.InputJson);
        Assert.Single(install.Secrets);
        var agentId = Guid.Parse(publicIdentity["agentId"]!.GetValue<string>());
        await control.ReportAsync(install, Outcome.Succeeded, new JsonObject
        {
            ["protocolVersion"] = 1,
            ["generation"] = Definitions.ManagedWorkerDefinitionTests.Generation,
            ["agentId"] = agentId.ToString(),
            ["installed"] = true,
        });
        await server.TickAsync();
        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Running, run.Status); // VM existence / QGA success is not ready.
        Assert.Equal(StepStatuses.Running, run.Steps.Single(s => s.Name == StepNames.WaitWorker).Status);
        Assert.Equal(0, await server.QueryAsync(db => db.GuestDiskUsages.CountAsync()));
        var connections = server.Services.GetRequiredService<AgentConnections>();
        var session = new AgentSession(agentId, "worker", ["opentofu", "proxmox"]);
        connections.Add(session);
        await server.TickAsync();
        Assert.Equal(RunStatuses.Running, (await server.LatestRunAsync(Guest)).Status); // connection alone is not Hello.
        var hello = new Hello { AgentVersion = "test" }; hello.Executors.AddRange(new[] { "opentofu", "proxmox" });
        await server.Engine.OnHelloAsync(agentId, hello, CancellationToken.None);
        await server.TickAsync();
        if (diskReporter)
        {
            Assert.Equal(RunStatuses.Running, (await server.LatestRunAsync(Guest)).Status);
            var reporter = await control.NextAsync("install-disk-reporter");
            Assert.Equal("guest", reporter.Executor);
            Assert.Equal(install.Secrets["proxmox.token"], reporter.Secrets["proxmox.token"]);
            Assert.Contains("disk-reporter.token", reporter.Secrets.Keys);
            var token = reporter.Secrets[SecretNames.DiskReporterToken];
            var reporterInput = JsonNode.Parse(reporter.InputJson)!;
            Assert.Equal("managed-nixos-v1", reporterInput["reporter"]!["installationMode"]!.GetValue<string>());
            Assert.Equal(new string('a', 64), reporterInput["reporter"]!["imageSha256"]!.GetValue<string>());
            Assert.Null(reporterInput["reporter"]!["binarySha256"]);
            Assert.Equal(Definitions.ManagedWorkerDefinitionTests.Generation, reporterInput["generation"]!.GetValue<string>());
            Assert.DoesNotContain(token, reporter.InputJson + run.Context + install.InputJson);
            Assert.DoesNotContain(SecretNames.DiskReporterToken, install.Secrets.Keys);
            using var http = server.CreateClient();
            http.BaseAddress = new Uri("https://localhost:8080");
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(System.Net.HttpStatusCode.NoContent,
                (await http.PostAsJsonAsync($"/api/guests/{run.ResourceId}/disk-usage", new { usedBytes = 987654321L })).StatusCode);
            Assert.Equal(987654321L, await server.QueryAsync(db => db.GuestDiskUsages.Select(d => d.UsedBytes).SingleAsync()));
            await control.ReportAsync(reporter, Outcome.Succeeded, new JsonObject { ["installed"] = true, ["firstReportSent"] = true });
            await server.TickAsync();
        }
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest)).Status);
        await server.WithDbAsync(async db =>
        {
            var binding = await db.ManagedWorkers.SingleAsync();
            Assert.Equal(agentId, binding.AgentId);
            Assert.NotNull(binding.ReadyAt);
            var agent = await db.Agents.SingleAsync(a => a.Id == agentId);
            Assert.Equal(new[] { "home" }, agent.Labels);
            Assert.Equal(1, agent.MaxConcurrency);
        });
        if (update)
        {
            await ExerciseOsUpdateAsync(server, control, agentId, hello, diskReporter);
            connections.Remove(session);
            return;
        }
        if (diskReporter)
        {
            // A Ready generation can configure/report again under fresh approval,
            // without replacing its correlated public identity or running enrollment.
            var original = await server.QueryAsync(db => db.ManagedWorkers.Select(w => new { w.AgentId, w.CertificatePem, w.ReadyAt }).SingleAsync());
            var planner = server.Services.GetRequiredService<Octopushy.Server.Inventory.Planner>();
            await planner.PlanGuestAsync(run.ResourceId, CancellationToken.None);
            await control.ReportAsync(await control.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(), artifacts: FakeAgent.PlanArtifact());
            await server.TickAsync();
            var again = await server.LatestRunAsync(Guest);
            Assert.Equal(RunStatuses.AwaitingApproval, again.Status);
            Assert.Empty(control.TakeAssignments());
            Assert.All(again.Steps.Where(s => s.Name is StepNames.PrepareWorker or StepNames.InstallWorker or StepNames.WaitWorker),
                s => Assert.Equal(StepStatuses.Skipped, s.Status));
            await ApproveAsync(server);
            await control.ReportAsync(await control.NextAsync("verify-guest"), Outcome.Succeeded);
            await control.ReportAsync(await control.NextAsync("grow"), Outcome.Succeeded);
            await control.ReportAsync(await control.NextAsync("verify"), Outcome.Succeeded);
            var setup = await control.NextAsync("install-disk-reporter");
            Assert.Equal("managed-nixos-v1", JsonNode.Parse(setup.InputJson)!["reporter"]!["installationMode"]!.GetValue<string>());
            await control.ReportAsync(setup, Outcome.Succeeded, new JsonObject { ["installed"] = true, ["firstReportSent"] = true });
            await server.TickAsync();
            Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest)).Status);
            Assert.Equal(original, await server.QueryAsync(db => db.ManagedWorkers.Select(w => new { w.AgentId, w.CertificatePem, w.ReadyAt }).SingleAsync()));

            await planner.PlanGuestAsync(run.ResourceId, CancellationToken.None);
            await control.ReportAsync(await control.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(), artifacts: FakeAgent.PlanArtifact());
            await server.TickAsync();
            await ApproveAsync(server);
            await control.ReportAsync(await control.NextAsync("verify-guest"), Outcome.Succeeded);
            await control.ReportAsync(await control.NextAsync("grow"), Outcome.Succeeded);
            await control.ReportAsync(await control.NextAsync("verify"), Outcome.Succeeded);
            var released = await server.QueryAsync(db => db.SecretResolutions.CountAsync(r => r.Key.StartsWith("system/disk-reporter/")));
            var pinned = RunContext.Parse((await server.LatestRunAsync(Guest)).Context);
            await using (var scope = server.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<Octopushy.Server.Store.VariableStore>()
                    .RevokeAsync(pinned.Secrets[SecretNames.ProxmoxPrivilegedToken], pinned.SecretVersions[SecretNames.ProxmoxPrivilegedToken],
                        "synthetic reporter refusal", "fixture", CancellationToken.None);
            await server.TickAsync();
            Assert.Empty(control.TakeAssignments());
            Assert.Equal(RunStatuses.Blocked, (await server.LatestRunAsync(Guest)).Status);
            Assert.Equal(released, await server.QueryAsync(db => db.SecretResolutions.CountAsync(r => r.Key.StartsWith("system/disk-reporter/"))));
        }
        connections.Remove(session);
    }

    private static async Task ExerciseOsUpdateAsync(ServerHarness server, FakeAgent control, Guid agentId, Hello hello, bool reporting)
    {
        var options = server.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Octopushy.Server.Options.OctopushyOptions>>().Value;
        const string system = "/nix/store/00000000000000000000000000000000-nixos-system-worker-test";
        var archive = new string('c', 64);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WorkerUpdates:Enabled"] = "true",
            ["WorkerUpdates:ArchiveUrl"] = "https://releases.test/worker.nar.xz",
            ["WorkerUpdates:ArchiveSha256"] = archive,
            ["WorkerUpdates:SystemPath"] = system,
            ["WorkerUpdates:ImageSha256"] = new string('e', 64),
        }).Build();
        ConfigurationBinder.Bind(config, options);
        options.DiskReporter.ManagedNixosImageSha256 = new string('e', 64);
        var original = await server.QueryAsync(db => db.ManagedWorkers.Select(w => new { w.AgentId, w.CertificatePem, w.ReadyAt }).SingleAsync());
        var resource = await server.ResourceAsync(Guest);
        using var http = server.CreateClient();
        (await http.PostAsJsonAsync("/api/auth/login", new { username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        var path = $"/api/managed-worker-preparation/{resource.Id}/update";
        async Task<JsonNode> ReleaseEvidenceAsync()
        {
            var data = (await http.GetFromJsonAsync<JsonObject>("/api/managed-worker-preparation"))!;
            return data["existingWorkers"]!.AsArray().Single(w => w!["resourceId"]!.GetValue<Guid>() == resource.Id)!;
        }
        var request = new { generation = Definitions.ManagedWorkerDefinitionTests.Generation, archiveSha256 = archive };
        var release = await http.GetAsync(path);
        Assert.Equal(System.Net.HttpStatusCode.OK, release.StatusCode);
        Assert.Equal(archive, (await release.Content.ReadFromJsonAsync<JsonObject>())!["archiveSha256"]!.GetValue<string>());
        Assert.Equal(System.Net.HttpStatusCode.Conflict,
            (await http.PostAsJsonAsync(path, new { generation = Guid.NewGuid(), archiveSha256 = archive })).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Conflict,
            (await http.PostAsJsonAsync(path, new { generation = Definitions.ManagedWorkerDefinitionTests.Generation, archiveSha256 = new string('d', 64) })).StatusCode);
        Assert.False(await server.QueryAsync(db => db.Agents.Where(a => a.Id == agentId).Select(a => a.Draining).SingleAsync()));
        var started = await http.PostAsJsonAsync(path, request);
        Assert.Equal(System.Net.HttpStatusCode.OK, started.StatusCode);
        var id = (await started.Content.ReadFromJsonAsync<JsonObject>())!["runId"]!.GetValue<Guid>();
        Assert.Equal(id, (await (await http.PostAsJsonAsync(path, request)).Content.ReadFromJsonAsync<JsonObject>())!["runId"]!.GetValue<Guid>());
        var agents = (await http.GetFromJsonAsync<JsonArray>("/api/agents"))!;
        Assert.True(agents.Single(a => a!["id"]!.GetValue<Guid>() == agentId)!["draining"]!.GetValue<bool>());
        Assert.Equal(System.Net.HttpStatusCode.Conflict,
            (await http.PatchAsJsonAsync($"/api/agents/{agentId}", new { draining = false })).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Conflict,
            (await http.DeleteAsync($"/api/agents/{agentId}")).StatusCode);
        await control.ReportAsync(await control.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();
        Assert.Equal(RunStatuses.AwaitingApproval, (await server.LatestRunAsync(Guest)).Status);
        Assert.Empty(control.TakeAssignments());
        var awaiting = await server.LatestRunAsync(Guest);
        var approval = awaiting.Steps.Single(s => s.Name == StepNames.Approval);
        Assert.Equal(System.Net.HttpStatusCode.Conflict,
            (await http.PostAsJsonAsync($"/api/runs/{awaiting.Id}/steps/{approval.Id}/approve",
                new { planSha256 = approval.PlanSha256 })).StatusCode);
        // Even a bypass of the decision API cannot release privileged credentials
        // without independent approval of this exact OS archive.
        await server.Engine.ExecuteAsync(async (db, services) =>
        {
            var current = await db.Runs.Include(r => r.Steps).SingleAsync(r => r.Id == id);
            var decision = current.Steps.Single(s => s.Name == StepNames.Approval);
            decision.Decision = "approved";
            decision.Status = StepStatuses.Succeeded;
            await db.SaveChangesAsync();
            var step = current.Steps.Single(s => s.Name == StepNames.UpdateWorkerOs);
            var attempt = new Attempt
            {
                Id = Guid.NewGuid(),
                RunId = id,
                StepId = step.Id,
                Executor = "managed-worker",
                Action = "update-os",
                Mutating = true,
            };
            var before = await db.SecretResolutions.CountAsync();
            await Assert.ThrowsAsync<AssignmentException>(() => services.GetRequiredService<AssignmentBuilder>()
                .BuildAsync(attempt, step, current, RunContext.Parse(current.Context), control.Id, CancellationToken.None));
            Assert.Equal(before, await db.SecretResolutions.CountAsync());
            decision.Decision = null;
            decision.Status = StepStatuses.AwaitingApproval;
            await db.SaveChangesAsync();
            return true;
        }, CancellationToken.None);
        await ApproveAsync(server);
        await control.ReportAsync(await control.NextAsync("verify-guest"), Outcome.Succeeded);
        var assignment = await control.NextAsync("update-os");
        Assert.Equal("managed-worker", assignment.Executor);
        var input = JsonNode.Parse(assignment.InputJson)!;
        Assert.Equal(archive, input["update"]!["archiveSha256"]!.GetValue<string>());
        Assert.Equal(system, input["update"]!["systemPath"]!.GetValue<string>());
        Assert.DoesNotContain(SecretNames.DiskReporterToken, assignment.Secrets.Keys);
        JsonObject evidence() => new()
        {
            ["protocolVersion"] = 1,
            ["generation"] = Definitions.ManagedWorkerDefinitionTests.Generation,
            ["systemPath"] = system,
            ["archiveSha256"] = archive,
            ["identityPreserved"] = true,
            ["stateFilesystemPreserved"] = true,
        };
        // A reconnect while the archive is importing is not post-switch readiness.
        await server.Engine.OnHelloAsync(agentId, hello, CancellationToken.None);
        await control.ReportAsync(assignment, Outcome.Succeeded, evidence());
        await control.ReportAsync(await control.NextAsync("verify-os"), Outcome.Succeeded, evidence());
        await control.ReportAsync(await control.NextAsync("grow"), Outcome.Succeeded);
        await control.ReportAsync(await control.NextAsync("verify"), Outcome.Succeeded);
        await server.TickAsync();
        Assert.Equal(RunStatuses.Running, (await server.LatestRunAsync(Guest)).Status);
        Assert.Empty(control.TakeAssignments());
        Assert.Null((await ReleaseEvidenceAsync())["verifiedOsImageSha256"]);
        var connections = server.Services.GetRequiredService<AgentConnections>();
        connections.Add(new AgentSession(agentId, "worker", ["opentofu", "proxmox"]));
        await server.Engine.OnHelloAsync(agentId, hello, CancellationToken.None);
        await server.TickAsync();
        if (reporting)
            await control.ReportAsync(await control.NextAsync("install-disk-reporter"), Outcome.Succeeded,
                new JsonObject { ["installed"] = true, ["firstReportSent"] = true });
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest)).Status);
        Assert.Equal(new string('e', 64), (await ReleaseEvidenceAsync())["verifiedOsImageSha256"]!.GetValue<string>());
        Assert.Equal(id, (await ReleaseEvidenceAsync())["verifiedOsUpdateRunId"]!.GetValue<Guid>());
        Assert.Equal(original, await server.QueryAsync(db => db.ManagedWorkers.Select(w => new { w.AgentId, w.CertificatePem, w.ReadyAt }).SingleAsync()));
        Assert.Equal(System.Net.HttpStatusCode.NoContent,
            (await http.PatchAsJsonAsync($"/api/agents/{agentId}", new { draining = false })).StatusCode);
        if (reporting)
        {
            // The import/base image is still the old revision. A later normal
            // provision must configure the reporter from the verified OS release.
            (await http.PostAsync($"/api/guests/{resource.Id}/plan", null)).EnsureSuccessStatusCode();
            var reprovision = await server.LatestRunAsync(Guest);
            Assert.NotNull(RunContext.Parse(reprovision.Context).DiskReporter);
            Assert.Equal(new string('e', 64), RunContext.Parse(reprovision.Context).DiskReporter!.ImageSha256);
            await control.ReportAsync(await control.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(), artifacts: FakeAgent.PlanArtifact());
            await server.TickAsync();
            await ApproveAsync(server);
            await control.ReportAsync(await control.NextAsync("verify-guest"), Outcome.Succeeded);
            await control.ReportAsync(await control.NextAsync("grow"), Outcome.Succeeded);
            await control.ReportAsync(await control.NextAsync("verify"), Outcome.Succeeded);
            await control.ReportAsync(await control.NextAsync("install-disk-reporter"), Outcome.Succeeded,
                new JsonObject { ["installed"] = true, ["firstReportSent"] = true });
            await server.TickAsync();
            Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest)).Status);
        }

        // A second release loses its outcome or reports invalid preservation
        // evidence. Neither path resumes, re-enrolls or automatically replays it.
        archive = new string('d', 64);
        options.WorkerUpdates.ArchiveSha256 = archive;
        (await http.PostAsJsonAsync(path, new { generation = Definitions.ManagedWorkerDefinitionTests.Generation, archiveSha256 = archive }))
            .EnsureSuccessStatusCode();
        await control.ReportAsync(await control.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();
        var recreate = await server.LatestRunAsync(Guest);
        Assert.Equal(RunStatuses.Failed, recreate.Status); // An OS update must never recreate a missing VM.
        Assert.Empty(control.TakeAssignments());
        await server.Engine.ExecuteAsync(async (db, services) =>
        {
            var current = await db.Runs.Include(r => r.Steps).SingleAsync(r => r.Id == recreate.Id);
            var decision = current.Steps.Single(s => s.Name == StepNames.Approval);
            decision.Decision = "approved";
            decision.Status = StepStatuses.Succeeded;
            decision.PlanSha256 = FakeAgent.PlanArtifact().Sha256;
            decision.WorkerUpdateSha256 = archive;
            await db.SaveChangesAsync();
            var step = current.Steps.Single(s => s.Name == StepNames.UpdateWorkerOs);
            var attempt = new Attempt { Id = Guid.NewGuid(), RunId = current.Id, StepId = step.Id, Executor = "managed-worker", Action = "update-os", Mutating = true };
            var before = await db.SecretResolutions.CountAsync();
            await Assert.ThrowsAsync<AssignmentException>(() => services.GetRequiredService<AssignmentBuilder>()
                .BuildAsync(attempt, step, current, RunContext.Parse(current.Context), control.Id, CancellationToken.None));
            Assert.Equal(before, await db.SecretResolutions.CountAsync());
            return true;
        }, CancellationToken.None);
        (await http.PostAsJsonAsync(path, new { generation = Definitions.ManagedWorkerDefinitionTests.Generation, archiveSha256 = archive }))
            .EnsureSuccessStatusCode();
        await control.ReportAsync(await control.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();
        await ApproveAsync(server);
        await control.ReportAsync(await control.NextAsync("verify-guest"), Outcome.Succeeded);
        var unknown = await control.NextAsync("update-os");
        var invalid = evidence();
        invalid["identityPreserved"] = false;
        await control.ReportAsync(unknown, reporting ? Outcome.Uncertain : Outcome.Succeeded, reporting ? null : invalid,
            error: reporting ? "synthetic lost update outcome" : null);
        await server.TickAsync();
        var uncertain = await server.LatestRunAsync(Guest);
        Assert.Equal(RunStatuses.Uncertain, uncertain.Status);
        Assert.Single(uncertain.Steps.Single(s => s.Name == StepNames.UpdateWorkerOs).Attempts);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, (await http.PatchAsJsonAsync($"/api/agents/{agentId}", new { draining = false })).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, (await http.DeleteAsync($"/api/agents/{agentId}")).StatusCode);
        await Assert.ThrowsAsync<RunException>(() => server.Engine.RetryFilesystemAsync(uncertain.Id, CancellationToken.None));
        var resolutions = await server.QueryAsync(db => db.SecretResolutions.CountAsync());
        await server.Engine.OnHelloAsync(agentId, hello, CancellationToken.None);
        await server.TickAsync();
        Assert.Empty(control.TakeAssignments());
        Assert.Equal(resolutions, await server.QueryAsync(db => db.SecretResolutions.CountAsync()));
        Assert.Equal(id, (await ReleaseEvidenceAsync())["verifiedOsUpdateRunId"]!.GetValue<Guid>());
        Assert.Equal(original, await server.QueryAsync(db => db.ManagedWorkers.Select(w => new { w.AgentId, w.CertificatePem, w.ReadyAt }).SingleAsync()));
        connections.Disconnect(agentId);
    }
    private static async Task<Assignment> PrepareAssignmentAsync(ServerHarness server, FakeAgent control)
    {
        await control.ReportAsync(await control.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();
        await ApproveAsync(server);
        await control.ReportAsync(await control.NextAsync("verify-guest"), Outcome.Succeeded);
        await control.ReportAsync(await control.NextAsync("grow"), Outcome.Succeeded);
        await control.ReportAsync(await control.NextAsync("verify"), Outcome.Succeeded);
        return await control.NextAsync("prepare");
    }

    [Theory]
    [InlineData(StepNames.PrepareWorker)]
    [InlineData(StepNames.InstallDiskReporter)]
    public async Task Unapproved_or_corrupted_plan_never_resolves_a_bootstrap_credential(string targetStep)
    {
        using var repo = Definitions.ManagedWorkerDefinitionTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repo, settings: new()
        {
            ["Octopushy:DiskReporter:Enabled"] = "true",
            ["Octopushy:DiskReporter:PublicUrl"] = "https://web.test",
            ["Octopushy:DiskReporter:BinaryUrl"] = "https://releases.test/reporter",
            ["Octopushy:DiskReporter:BinarySha256"] = new string('b', 64),
            ["Octopushy:DiskReporter:ManagedNixosImageSha256"] = new string('a', 64),
        });
        var control = await server.ConnectAgentAsync("home");
        await TrustAsync(server);
        await control.ReportAsync(await control.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();
        async Task RefusedAsync()
        {
            await server.Engine.ExecuteAsync(async (db, services) =>
            {
                var run = await db.Runs.Include(r => r.Steps).SingleAsync(r => r.Operation == Operations.Provision);
                var step = run.Steps.Single(s => s.Name == targetStep);
                var attempt = new Attempt { Id = Guid.NewGuid(), RunId = run.Id, StepId = step.Id, Executor = "managed-worker", Action = "prepare", Mutating = true };
                await Assert.ThrowsAsync<AssignmentException>(() => services.GetRequiredService<AssignmentBuilder>()
                    .BuildAsync(attempt, step, run, RunContext.Parse(run.Context), control.Id, CancellationToken.None));
                Assert.False(await db.SecretResolutions.AnyAsync(r => r.AttemptId == attempt.Id));
                return true;
            }, CancellationToken.None);
        }
        await RefusedAsync();
        await ApproveAsync(server);
        if (targetStep == StepNames.InstallDiskReporter) await RefusedAsync(); // root/readiness absent, still no plaintext
        await server.WithDbAsync(db => db.Artifacts.ExecuteUpdateAsync(u => u.SetProperty(a => a.Sha256, "corrupted")));
        await RefusedAsync();
    }

    [Fact]
    public async Task A_lost_prepare_lease_is_uncertain_and_is_not_replayed()
    {
        using var repo = Definitions.ManagedWorkerDefinitionTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repo);
        var control = await server.ConnectAgentAsync("home");
        await TrustAsync(server);
        var prepare = await PrepareAssignmentAsync(server, control);
        await server.Engine.OnStartedAsync(control.Id, Guid.Parse(prepare.AttemptId), CancellationToken.None);
        await server.WithDbAsync(db => db.Attempts.Where(a => a.Id == Guid.Parse(prepare.AttemptId))
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest);
        Assert.Equal(RunStatuses.Uncertain, run.Status);
        Assert.Single(run.Steps.Single(s => s.Name == StepNames.PrepareWorker).Attempts);
        Assert.Empty(run.Steps.Single(s => s.Name == StepNames.InstallWorker).Attempts);
        Assert.Empty(control.TakeAssignments());
        await Assert.ThrowsAsync<RunException>(() => server.Engine.RetryFilesystemAsync(run.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Resume_reuses_the_certificate_and_a_new_key_for_the_generation_is_refused()
    {
        using var repo = Definitions.ManagedWorkerDefinitionTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repo);
        var control = await server.ConnectAgentAsync("home");
        await TrustAsync(server);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await control.ReportAsync(await PrepareAssignmentAsync(server, control), Outcome.Succeeded, Csr(key));
        var install = await control.NextAsync("install");
        var issued = JsonNode.Parse(install.InputJson)!["identity"]!.ToJsonString();
        await control.ReportAsync(install, Outcome.Failed, error: "synthetic known failure");
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest);
        await server.Engine.RetryFilesystemAsync(run.Id, CancellationToken.None);
        var retry = await control.NextAsync("install");
        Assert.Equal(issued, JsonNode.Parse(retry.InputJson)!["identity"]!.ToJsonString());
        await control.ReportAsync(retry, Outcome.Failed, error: "synthetic known failure");
        await server.TickAsync();
        var resourceId = run.ResourceId;
        var planner = server.Services.GetRequiredService<Octopushy.Server.Inventory.Planner>();
        await planner.PlanGuestAsync(resourceId, CancellationToken.None);
        await control.ReportAsync(await PrepareAssignmentAsync(server, control), Outcome.Succeeded, Csr(key));
        var resumed = await control.NextAsync("install");
        Assert.Equal(issued, JsonNode.Parse(resumed.InputJson)!["identity"]!.ToJsonString());
        await control.ReportAsync(resumed, Outcome.Failed, error: "synthetic known failure");
        await server.TickAsync();
        await planner.PlanGuestAsync(resourceId, CancellationToken.None);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await control.ReportAsync(await PrepareAssignmentAsync(server, control), Outcome.Succeeded, Csr(otherKey));
        await server.TickAsync();
        run = await server.LatestRunAsync(Guest);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("different public key", run.Detail);
        await server.WithDbAsync(async db => Assert.Equal(2, await db.Agents.CountAsync())); // control + one correlated identity
    }

    [Fact]
    public async Task Binding_blocks_retirement_role_generation_and_placement_bypasses_and_self_dispatch()
    {
        using var repo = Definitions.ManagedWorkerDefinitionTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repo);
        var control = await server.ConnectAgentAsync("home");
        await TrustAsync(server);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await control.ReportAsync(await PrepareAssignmentAsync(server, control), Outcome.Succeeded, Csr(key));
        var install = await control.NextAsync("install");
        var identity = JsonNode.Parse(install.InputJson)!["identity"]!;
        var agentId = Guid.Parse(identity["agentId"]!.GetValue<string>());
        await control.ReportAsync(install, Outcome.Succeeded, new JsonObject
        {
            ["protocolVersion"] = 1,
            ["generation"] = Definitions.ManagedWorkerDefinitionTests.Generation,
            ["agentId"] = agentId.ToString(),
            ["installed"] = true
        });
        var connections = server.Services.GetRequiredService<AgentConnections>();
        var session = new AgentSession(agentId, "worker", ["opentofu", "proxmox"]);
        connections.Add(session);
        var hello = new Hello { AgentVersion = "test" }; hello.Executors.AddRange(new[] { "opentofu", "proxmox" });
        await server.Engine.OnHelloAsync(agentId, hello, CancellationToken.None);
        await server.TickAsync();
        var previous = await server.LatestRunAsync(Guest);
        var planner = server.Services.GetRequiredService<Octopushy.Server.Inventory.Planner>();
        connections.Disconnect(control.Id);
        await planner.PlanGuestAsync(previous.ResourceId, CancellationToken.None);
        await server.TickAsync();
        Assert.False(session.Outbound.TryRead(out _)); // dispatch exclusion, including preview
        await server.Engine.ExecuteAsync(async (db, services) =>
        {
            var run = await db.Runs.Include(r => r.Steps).ThenInclude(s => s.Attempts).OrderByDescending(r => r.CreatedAt)
                .FirstAsync(r => r.ResourceId == previous.ResourceId);
            var step = run.Steps.Single(s => s.Name == StepNames.Plan);
            var attempt = Assert.Single(step.Attempts);
            var context = RunContext.Parse(run.Context);
            await Assert.ThrowsAsync<AssignmentException>(() => services.GetRequiredService<AssignmentBuilder>()
                .BuildAsync(attempt, step, run, context, agentId, CancellationToken.None));
            foreach (var changed in new[] {context with {ManagedWorker = null},
                context with {ManagedWorker = context.ManagedWorker! with {Generation = Guid.NewGuid()}},
                context with {Proxmox = context.Proxmox with {Node = "other"}},
                context with {Proxmox = context.Proxmox with {Vmid = 999}}})
            {
                await Assert.ThrowsAsync<AssignmentException>(() => services.GetRequiredService<AssignmentBuilder>()
                    .BuildAsync(attempt, step, run, changed, control.Id, CancellationToken.None));
            }
            run.Operation = Operations.Retire;
            await Assert.ThrowsAsync<AssignmentException>(() => services.GetRequiredService<AssignmentBuilder>()
                .BuildAsync(attempt, step, run, context, control.Id, CancellationToken.None));
            return true;
        }, CancellationToken.None);
        connections.Remove(session);
    }

    [Fact]
    public async Task Duplicate_generations_and_mismatched_prepare_evidence_fail_closed()
    {
        using var repo = Definitions.ManagedWorkerDefinitionTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repo);
        var control = await server.ConnectAgentAsync("home");
        await TrustAsync(server);
        var prepare = await PrepareAssignmentAsync(server, control);
        await server.Engine.ExecuteAsync(async (db, _) =>
        {
            var run = await db.Runs.SingleAsync(r => r.Operation == Operations.Provision);
            var duplicate = new Run
            {
                Id = Guid.NewGuid(),
                ResourceId = Guid.NewGuid(),
                Operation = run.Operation,
                RevisionId = run.RevisionId,
                Commit = run.Commit,
                Reason = "test",
                Context = run.Context,
                InputsHash = run.InputsHash
            };
            await Assert.ThrowsAsync<RunException>(() => server.Engine.AddRunsAsync(db, [duplicate], CancellationToken.None));
            return true;
        }, CancellationToken.None);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var report = Csr(key); report["generation"] = Guid.NewGuid().ToString();
        await control.ReportAsync(prepare, Outcome.Succeeded, report);
        await server.TickAsync();
        Assert.Equal(RunStatuses.Failed, (await server.LatestRunAsync(Guest)).Status);
        await server.WithDbAsync(async db =>
        {
            Assert.Single(await db.Agents.ToListAsync());
            Assert.Null((await db.ManagedWorkers.SingleAsync()).CertificatePem);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Managed_delete_or_replace_plans_never_release_privileged_credentials_even_if_approval_is_bypassed(bool actionsOnly)
    {
        using var repo = Definitions.ManagedWorkerDefinitionTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repo);
        var control = await server.ConnectAgentAsync("home");
        await TrustAsync(server);
        var plan = FakeAgent.Plan(replace: 1);
        if (actionsOnly) plan["counts"] = new JsonObject { ["create"] = 0, ["update"] = 1, ["delete"] = 0, ["replace"] = 0 };
        await control.ReportAsync(await control.NextAsync("preview"), Outcome.Succeeded, plan, artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Empty(control.TakeAssignments());
        await server.Engine.ExecuteAsync(async (db, services) =>
        {
            var stored = await db.Runs.Include(r => r.Steps).SingleAsync(r => r.Id == run.Id);
            var approval = stored.Steps.Single(s => s.Name == StepNames.Approval);
            approval.Status = StepStatuses.Succeeded;
            approval.Decision = "approved";
            approval.PlanSha256 = (await db.Artifacts.SingleAsync()).Sha256;
            await db.SaveChangesAsync();
            var step = stored.Steps.Single(s => s.Name == StepNames.PrepareWorker);
            var attempt = new Attempt { Id = Guid.NewGuid(), RunId = stored.Id, StepId = step.Id, Executor = "managed-worker", Action = "prepare", Mutating = true };
            await Assert.ThrowsAsync<AssignmentException>(() => services.GetRequiredService<AssignmentBuilder>()
                .BuildAsync(attempt, step, stored, RunContext.Parse(stored.Context), control.Id, CancellationToken.None));
            Assert.False(await db.SecretResolutions.AnyAsync(r => r.AttemptId == attempt.Id));
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_prepare_result_from_a_previous_source_never_issues_new_authentication_authority()
    {
        using var repo = Definitions.ManagedWorkerDefinitionTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repo);
        var control = await server.ConnectAgentAsync("home");
        await TrustAsync(server);
        var prepare = await PrepareAssignmentAsync(server, control);
        await server.WithDbAsync(db => db.RepositorySettings.ExecuteUpdateAsync(u => u.SetProperty(r => r.SourceId, Guid.NewGuid())));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await control.ReportAsync(prepare, Outcome.Succeeded, Csr(key));
        await server.WithDbAsync(async db =>
        {
            Assert.Null((await db.ManagedWorkers.SingleAsync()).CertificatePem);
            Assert.Single(await db.Agents.ToListAsync());
        });
    }

}
