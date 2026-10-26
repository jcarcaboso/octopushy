using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
        await server.Engine.ApproveAsync(run.Id, approval.Id, approval.PlanSha256!, "ops", null, CancellationToken.None);
    }
    private static JsonObject Csr(ECDsa key) => new()
    {
        ["protocolVersion"] = 1,
        ["generation"] = Definitions.ManagedWorkerDefinitionTests.Generation,
        ["csrDer"] = Convert.ToBase64String(new CertificateRequest("CN=worker", key, HashAlgorithmName.SHA256).CreateSigningRequest()),
        ["agentVersion"] = "test",
    };

    [Fact]
    public async Task A_noop_plan_needs_approval_and_readiness_requires_the_correlated_authenticated_worker()
    {
        using var repo = Definitions.ManagedWorkerDefinitionTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repo);
        var control = await server.ConnectAgentAsync("home");
        await TrustAsync(server);
        var preview = await control.NextAsync("preview");
        Assert.True(JsonNode.Parse(preview.InputJson)!["retainNoChangesPlan"]!.GetValue<bool>());
        await control.ReportAsync(preview, Outcome.Succeeded, FakeAgent.Plan(), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Empty(control.TakeAssignments());
        await ApproveAsync(server);
        await control.ReportAsync(await control.NextAsync("verify-guest"), Outcome.Succeeded);
        await control.ReportAsync(await control.NextAsync("grow"), Outcome.Succeeded);
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
        var connections = server.Services.GetRequiredService<AgentConnections>();
        var session = new AgentSession(agentId, "worker", ["opentofu", "proxmox"]);
        connections.Add(session);
        await server.TickAsync();
        Assert.Equal(RunStatuses.Running, (await server.LatestRunAsync(Guest)).Status); // connection alone is not Hello.
        var hello = new Hello { AgentVersion = "test" }; hello.Executors.AddRange(new[] { "opentofu", "proxmox" });
        await server.Engine.OnHelloAsync(agentId, hello, CancellationToken.None);
        await server.TickAsync();
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
        connections.Remove(session);
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

    [Fact]
    public async Task Unapproved_or_corrupted_plan_never_resolves_a_bootstrap_credential()
    {
        using var repo = Definitions.ManagedWorkerDefinitionTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repo);
        var control = await server.ConnectAgentAsync("home");
        await TrustAsync(server);
        await control.ReportAsync(await control.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();
        async Task RefusedAsync()
        {
            await server.Engine.ExecuteAsync(async (db, services) =>
            {
                var run = await db.Runs.Include(r => r.Steps).SingleAsync(r => r.Operation == Operations.Provision);
                var step = run.Steps.Single(s => s.Name == StepNames.PrepareWorker);
                var attempt = new Attempt { Id = Guid.NewGuid(), RunId = run.Id, StepId = step.Id, Executor = "managed-worker", Action = "prepare", Mutating = true };
                await Assert.ThrowsAsync<AssignmentException>(() => services.GetRequiredService<AssignmentBuilder>()
                    .BuildAsync(attempt, step, run, RunContext.Parse(run.Context), control.Id, CancellationToken.None));
                Assert.False(await db.SecretResolutions.AnyAsync(r => r.AttemptId == attempt.Id));
                return true;
            }, CancellationToken.None);
        }
        await RefusedAsync();
        await ApproveAsync(server);
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
