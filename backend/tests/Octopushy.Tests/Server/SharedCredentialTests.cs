using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class SharedCredentialTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string Shared = "proxmox/korriban/shared-token";
    private const string SharedRole = "proxmox.shared-token";

    [Fact]
    public async Task One_token_is_persisted_once_and_used_for_preview_apply_and_verification_without_bypassing_approval()
    {
        using var repo = SharedRepository();
        repo.Write("labs/home/lab.yaml",
            "schemaVersion: 1\nexecution: { agentLabels: [home] }\npolicy: { approval: manual }");
        await using var server = await ServerHarness.StartAsync(postgres, repo, withSecrets: false);
        await server.SetSecretAsync(Shared, "shared-v1");
        await server.SetSecretAsync("opentofu/pg", "fixture-backend");
        using var http = await LoginAsync(server);
        var worker = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);
        var preview = await worker.NextAsync("preview");
        Assert.Equal("shared-v1", preview.Secrets["proxmox.token"]);
        Assert.DoesNotContain(SharedRole, preview.Secrets.Keys);
        Assert.DoesNotContain(SecretNames.ProxmoxReadOnlyToken, preview.Secrets.Keys);
        Assert.DoesNotContain(SecretNames.ProxmoxPrivilegedToken, preview.Secrets.Keys);
        await worker.ReportAsync(preview, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        var context = RunContext.Parse(run.Context);
        Assert.Equal(Shared, context.Secrets[SharedRole]);
        Assert.Equal(1, context.SecretVersions[SharedRole]);
        Assert.Single(context.Secrets, s => s.Key.StartsWith("proxmox.", StringComparison.Ordinal));
        Assert.Single(context.SecretVersions, s => s.Key.StartsWith("proxmox.", StringComparison.Ordinal));
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Empty(worker.TakeAssignments());
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        (await http.PostAsJsonAsync($"/api/runs/{run.Id}/steps/{approval.Id}/approve",
            new { planSha256 = approval.PlanSha256 })).EnsureSuccessStatusCode();
        var apply = await worker.NextAsync("apply");
        Assert.Equal("shared-v1", apply.Secrets["proxmox.token"]);
        await worker.ReportAsync(apply, Outcome.Succeeded);
        var verify = await worker.NextAsync("verify-guest");
        Assert.Equal("shared-v1", Assert.Single(verify.Secrets).Value);
    }

    [Theory]
    [InlineData("secrets", SecretNames.ProxmoxReadOnlyToken)]
    [InlineData("secretVersions", SecretNames.ProxmoxPrivilegedToken)]
    [InlineData("requestedSecretVersions", SecretNames.ProxmoxReadOnlyToken)]
    [InlineData("secrets", SecretNames.ProxmoxToken)]
    [InlineData("secretVersions", SecretNames.ProxmoxToken)]
    [InlineData("requestedSecretVersions", SecretNames.ProxmoxToken)]
    [InlineData("secrets", SecretNames.ProxmoxCa)]
    [InlineData("secrets", "backend.PG_CONN_STR")]
    public async Task Invalid_persisted_roles_and_shared_key_aliases_block_before_release(string map, string role)
    {
        using var repo = SharedRepository();
        await using var server = await ServerHarness.StartAsync(postgres, repo, withSecrets: false);
        await server.SetSecretAsync(Shared, "shared-v1");
        await server.SetSecretAsync("opentofu/pg", "fixture-backend");
        using var http = await LoginAsync(server);
        await server.SyncAndTrustAsync(http);
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        await server.WithDbAsync(async db =>
        {
            var row = await db.Runs.SingleAsync(r => r.Id == run.Id);
            var context = JsonNode.Parse(row.Context)!;
            context[map]![role] = map == "secrets" ? JsonValue.Create(Shared) : JsonValue.Create(1);
            row.Context = context.ToJsonString();
            await db.SaveChangesAsync();
        });
        var worker = await server.ConnectAgentAsync("home");
        await server.TickAsync();
        Assert.DoesNotContain(worker.TakeAssignments(), a => a.Action == "preview");
        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        if (role == SecretNames.ProxmoxToken)
            Assert.Contains("inspect and replan", run.Detail);
        var attempts = run.Steps.SelectMany(s => s.Attempts).Select(a => a.Id).ToArray();
        Assert.False(await server.QueryAsync(db => db.SecretResolutions.AnyAsync(r => attempts.Contains(r.AttemptId))));
    }

    [Fact]
    public async Task Observe_pins_one_shared_role_while_guest_binding_waits_atomically_for_backend()
    {
        using var repo = SharedRepository();
        await using var server = await ServerHarness.StartAsync(postgres, repo, withSecrets: false);
        await server.SetSecretAsync(Shared, "shared-v1");
        var worker = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var observe = await worker.NextAsync("observe");
        Assert.Equal("shared-v1", Assert.Single(observe.Secrets).Value);
        var observed = RunContext.Parse((await server.LatestRunAsync("home/korriban", Operations.Observe)).Context);
        Assert.Equal(Shared, Assert.Single(observed.Secrets).Value);
        Assert.Equal(SharedRole, Assert.Single(observed.Secrets).Key);
        Assert.Equal(1, Assert.Single(observed.SecretVersions).Value);
        Assert.Equal(SharedRole, Assert.Single(observed.SecretVersions).Key);
        var guest = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Blocked, guest.Status);
        var blocked = RunContext.Parse(guest.Context);
        Assert.False(blocked.SecretBindingsPinned);
        Assert.Empty(blocked.SecretVersions);
        await server.SetSecretAsync("opentofu/pg", "fixture-backend");
        var preview = await worker.NextAsync("preview");
        Assert.Equal("shared-v1", preview.Secrets["proxmox.token"]);
        var ready = RunContext.Parse((await server.LatestRunAsync(Guest, Operations.Provision)).Context);
        Assert.True(ready.SecretBindingsPinned);
        Assert.Equal(2, ready.SecretVersions.Count);
        Assert.Equal(1, ready.SecretVersions[SharedRole]);
        Assert.Equal(1, ready.SecretVersions["backend.PG_CONN_STR"]);
    }

    [Theory]
    [InlineData(StepNames.Apply, "pending")]
    [InlineData(StepNames.Unprotect, "pending")]
    [InlineData(StepNames.GrowRoot, "pending")]
    [InlineData(StepNames.VerifyRoot, "pending")]
    [InlineData(StepNames.Apply, "denied")]
    [InlineData(StepNames.VerifyRoot, "stale")]
    [InlineData(StepNames.GrowRoot, "status")]
    [InlineData(StepNames.Unprotect, "missing-artifact")]
    [InlineData(StepNames.Apply, "corrupt-artifact")]
    [InlineData(StepNames.VerifyRoot, "corrupt-artifact")]
    [InlineData(StepNames.GrowRoot, "plan-failed")]
    public async Task Shared_credentials_never_bypass_approval_or_saved_plan_checks_in_builder_or_dispatch(string name, string invalid)
    {
        using var repo = SharedRepository(rootFilesystem: true);
        repo.Write("labs/home/lab.yaml",
            "schemaVersion: 1\nexecution: { agentLabels: [home] }\npolicy: { approval: manual }");
        await using var server = await ServerHarness.StartAsync(postgres, repo, withSecrets: false);
        await server.SetSecretAsync(Shared, "shared-v1");
        await server.SetSecretAsync("opentofu/pg", "fixture-backend");
        using var http = await LoginAsync(server);
        var worker = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);
        await worker.ReportAsync(await worker.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(update: 1),
            artifacts: invalid == "missing-artifact" ? [] : [FakeAgent.PlanArtifact()]);
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
        var approval = await db.Steps.SingleAsync(s => s.RunId == run.Id && s.Name == StepNames.Approval);
        approval.Decision = invalid == "pending" ? null : invalid == "denied" ? "rejected" : "approved";
        approval.Status = invalid == "status" ? StepStatuses.AwaitingApproval : StepStatuses.Succeeded;
        if (invalid == "stale")
            approval.PlanSha256 = new string('0', 64);
        if (invalid == "plan-failed")
            (await db.Steps.SingleAsync(s => s.RunId == run.Id && s.Name == StepNames.Plan)).Status = StepStatuses.Failed;
        if (invalid == "corrupt-artifact")
        {
            var planAttempt = run.Steps.Single(s => s.Name == StepNames.Plan).Attempts.Single();
            await scope.ServiceProvider.GetRequiredService<ArtifactStore>()
                .SaveAsync(planAttempt.Id, AssignmentBuilder.PlanArtifact, [1, 2, 3], CancellationToken.None);
        }
        var step = await db.Steps.SingleOrDefaultAsync(s => s.RunId == run.Id && s.Name == name);
        if (step is null)
        {
            step = new Step { Id = Guid.CreateVersion7(), RunId = run.Id, Name = name, Kind = StepKinds.Agent, Ordinal = 9 };
            db.Steps.Add(step);
        }
        step.Status = StepStatuses.Queued;
        var action = AssignmentBuilder.ActionFor(name);
        var attempt = new Attempt
        {
            Id = Guid.CreateVersion7(),
            RunId = run.Id,
            StepId = step.Id,
            Number = 1,
            Executor = action.Executor,
            Action = action.Action,
            Mutating = action.Mutating,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Attempts.Add(attempt);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<AssignmentException>(() => scope.ServiceProvider.GetRequiredService<AssignmentBuilder>()
            .BuildAsync(attempt, step, run, RunContext.Parse(run.Context), worker.Id, CancellationToken.None));
        Assert.Equal(0, await server.QueryAsync(d => d.SecretResolutions.CountAsync(r => r.AttemptId == attempt.Id)));
        await server.Engine.DispatchAsync(CancellationToken.None);
        Assert.DoesNotContain(worker.TakeAssignments(), a => a.AttemptId == attempt.Id.ToString());
        Assert.Equal(AttemptStatuses.Failed,
            await server.QueryAsync(d => d.Attempts.Where(a => a.Id == attempt.Id).Select(a => a.Status).SingleAsync()));
        Assert.Equal(0, await server.QueryAsync(d => d.SecretResolutions.CountAsync(r => r.AttemptId == attempt.Id)));
    }

    [Fact]
    public async Task One_exact_version_survives_promotion_restart_and_retry_then_revocation_blocks_without_fallback()
    {
        using var repo = SharedRepository(rootFilesystem: true);
        const string installation = "labs/home/proxmox/korriban/installation.yaml";
        repo.Write(installation, File.ReadAllText(Path.Combine(repo.Root, installation))
            .Replace($"store: {Shared} }}", $"store: {Shared}, version: 2 }}"));
        await using var server = await ServerHarness.StartAsync(postgres, repo, withSecrets: false);
        await server.SetSecretAsync(Shared, "shared-v1");
        await server.SetSecretAsync("opentofu/pg", "fixture-backend");
        using var http = await LoginAsync(server);
        (await http.PostAsJsonAsync("/api/variables/candidates", new { key = Shared, value = "shared-v2" })).EnsureSuccessStatusCode();
        var worker = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);
        await server.TickAsync();
        Assert.Empty(worker.TakeAssignments());
        var blocked = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Contains("candidate not promoted", blocked.Detail);
        Assert.False(RunContext.Parse(blocked.Context).SecretBindingsPinned);
        Assert.Empty(RunContext.Parse(blocked.Context).SecretVersions);
        (await http.PostAsJsonAsync("/api/variables/promote", new
        { key = Shared, version = 2, expectedCurrentVersion = 1, verificationNote = "fixture shared access verified" })).EnsureSuccessStatusCode();
        var preview = await worker.NextAsync("preview");
        Assert.Equal("shared-v2", preview.Secrets["proxmox.token"]);
        var observed = RunContext.Parse((await server.LatestRunAsync("home/korriban", Operations.Observe)).Context);
        Assert.Equal(2, Assert.Single(observed.RequestedSecretVersions).Value);
        Assert.Equal(SharedRole, Assert.Single(observed.RequestedSecretVersions).Key);
        await StageAndPromoteAsync(http, 3, 2, "shared-v3");
        await worker.ReportAsync(preview, Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: FakeAgent.PlanArtifact());
        await server.RestartFixtureAsync();
        using var resumed = server.CreateClient();
        (await resumed.PostAsJsonAsync("/api/auth/login", new { username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        worker = await server.ConnectAgentAsync("home");
        var apply = await worker.NextAsync("apply");
        Assert.Equal("shared-v2", apply.Secrets["proxmox.token"]);
        await worker.ReportAsync(apply, Outcome.Succeeded);
        var verify = await worker.NextAsync("verify-guest");
        Assert.Equal("shared-v2", Assert.Single(verify.Secrets).Value);
        await worker.ReportAsync(verify, Outcome.Succeeded);
        var grow = await worker.NextAsync("grow");
        Assert.Equal("shared-v2", Assert.Single(grow.Secrets).Value);
        await worker.ReportAsync(grow, Outcome.Failed, error: "fixture guest unit not ready");
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        var context = RunContext.Parse(run.Context);
        Assert.Equal(2, context.SecretVersions[SharedRole]);
        Assert.Equal(2, Assert.Single(context.RequestedSecretVersions).Value);
        Assert.Equal(SharedRole, Assert.Single(context.RequestedSecretVersions).Key);
        (await resumed.PostAsync($"/api/runs/{run.Id}/retry-filesystem", null)).EnsureSuccessStatusCode();
        var retry = await worker.NextAsync("grow");
        Assert.Equal("shared-v2", Assert.Single(retry.Secrets).Value);
        await worker.ReportAsync(retry, Outcome.Failed, error: "still not ready");
        await server.TickAsync();
        (await resumed.PostAsJsonAsync("/api/variables/revoke", new
        { key = Shared, version = 2, reason = "fixture revoke pinned shared token" })).EnsureSuccessStatusCode();
        var releases = await server.QueryAsync(db => db.SecretResolutions.CountAsync(r => r.Key == Shared));
        (await resumed.PostAsync($"/api/runs/{run.Id}/retry-filesystem", null)).EnsureSuccessStatusCode();
        await server.TickAsync();
        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("v2: revoked", run.Detail);
        Assert.Equal(2, RunContext.Parse(run.Context).SecretVersions[SharedRole]);
        Assert.DoesNotContain(worker.TakeAssignments(), a => a.Action == "grow");
        Assert.Equal(releases, await server.QueryAsync(db => db.SecretResolutions.CountAsync(r => r.Key == Shared)));
        Assert.False(await server.QueryAsync(db => db.SecretResolutions.AnyAsync(r => r.Key == Shared && r.Version != 2)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Noop_shared_rootfs_still_requires_manual_approval_and_a_saved_plan(bool artifact)
    {
        using var repo = SharedRepository(rootFilesystem: true);
        repo.Write("labs/home/lab.yaml",
            "schemaVersion: 1\nexecution: { agentLabels: [home] }\npolicy: { approval: manual }");
        await using var server = await ServerHarness.StartAsync(postgres, repo, withSecrets: false);
        await server.SetSecretAsync(Shared, "shared-v1");
        await server.SetSecretAsync("opentofu/pg", "fixture-backend");
        using var http = await LoginAsync(server);
        var worker = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);
        var preview = await worker.NextAsync("preview");
        Assert.True(JsonNode.Parse(preview.InputJson)!["retainNoChangesPlan"]!.GetValue<bool>());
        await worker.ReportAsync(preview, Outcome.Succeeded, FakeAgent.Plan(), artifacts: artifact ? [FakeAgent.PlanArtifact()] : []);
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(StepStatuses.Skipped, run.Steps.Single(s => s.Name == StepNames.Apply).Status);
        if (!artifact)
        {
            Assert.Equal(RunStatuses.Failed, run.Status);
            Assert.Contains("saved plan", run.Detail);
            Assert.DoesNotContain(worker.TakeAssignments(), a => a.Executor == "nixos-rootfs");
            return;
        }
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Empty(worker.TakeAssignments());
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        Assert.True(approval.ApprovalRequired);
        (await http.PostAsJsonAsync($"/api/runs/{run.Id}/steps/{approval.Id}/approve",
            new { planSha256 = approval.PlanSha256 })).EnsureSuccessStatusCode();
        await worker.ReportAsync(await worker.NextAsync("verify-guest"), Outcome.Succeeded);
        var grow = await worker.NextAsync("grow");
        Assert.Equal("shared-v1", Assert.Single(grow.Secrets).Value);
        await worker.ReportAsync(grow, Outcome.Succeeded);
        var verify = await worker.NextAsync("verify");
        Assert.Equal("shared-v1", Assert.Single(verify.Secrets).Value);
        Assert.Equal("nixos-rootfs", verify.Executor);
        Assert.False(AssignmentBuilder.ActionFor(StepNames.VerifyRoot).Mutating);
        Assert.Equal(SecretNames.ProxmoxPrivilegedToken, AssignmentBuilder.TokenFor(StepNames.VerifyRoot));
        await worker.ReportAsync(verify, Outcome.Succeeded);
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
    }

    [Theory]
    [InlineData("secrets")]
    [InlineData("secretVersions")]
    [InlineData("requestedSecretVersions")]
    public async Task Persisted_split_context_cannot_be_converted_by_adding_a_shared_role(string map)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAndTrustAsync();
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        await server.WithDbAsync(async db =>
        {
            var row = await db.Runs.SingleAsync(r => r.Id == run.Id);
            var context = JsonNode.Parse(row.Context)!;
            context[map]![SharedRole] = map == "secrets"
                ? JsonValue.Create("proxmox/korriban/read-only-token") : JsonValue.Create(1);
            row.Context = context.ToJsonString();
            await db.SaveChangesAsync();
        });
        await server.RestartFixtureAsync();
        var worker = await server.ConnectAgentAsync("home");
        await server.TickAsync();
        Assert.DoesNotContain(worker.TakeAssignments(), a => a.Action == "preview");
        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("cannot be mixed", run.Detail);
        var attempts = run.Steps.SelectMany(s => s.Attempts).Select(a => a.Id).ToArray();
        Assert.False(await server.QueryAsync(db => db.SecretResolutions.AnyAsync(r => attempts.Contains(r.AttemptId))));
    }

    [Fact]
    public async Task Shared_retire_uses_the_same_token_for_approved_unprotect_apply_and_absence_verification()
    {
        using var repo = SharedRepository();
        const string guest = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
        repo.Write(guest, File.ReadAllText(Path.Combine(repo.Root, guest)) + "\nlifecycle: retired");
        repo.Write("labs/home/lab.yaml",
            "schemaVersion: 1\nexecution: { agentLabels: [home] }\npolicy: { approval: manual }");
        await using var server = await ServerHarness.StartAsync(postgres, repo, withSecrets: false);
        await server.SetSecretAsync(Shared, "shared-v1");
        await server.SetSecretAsync("opentofu/pg", "fixture-backend");
        using var http = await LoginAsync(server);
        var worker = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);
        var preview = await worker.NextAsync("preview");
        Assert.True(JsonNode.Parse(preview.InputJson)!["destroy"]!.GetValue<bool>());
        Assert.Equal("shared-v1", preview.Secrets["proxmox.token"]);
        await worker.ReportAsync(preview, Outcome.Succeeded, FakeAgent.Plan(delete: 1), artifacts: FakeAgent.PlanArtifact("destroy"));
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Retire);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Empty(worker.TakeAssignments());
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        (await http.PostAsJsonAsync($"/api/runs/{run.Id}/steps/{approval.Id}/approve",
            new { planSha256 = approval.PlanSha256 })).EnsureSuccessStatusCode();
        var unprotect = await worker.NextAsync("set-protection");
        Assert.Equal("shared-v1", Assert.Single(unprotect.Secrets).Value);
        await worker.ReportAsync(unprotect, Outcome.Succeeded);
        var apply = await worker.NextAsync("apply");
        Assert.Equal("shared-v1", apply.Secrets["proxmox.token"]);
        await worker.ReportAsync(apply, Outcome.Succeeded);
        var absent = await worker.NextAsync("verify-absent");
        Assert.Equal("shared-v1", Assert.Single(absent.Secrets).Value);
        await worker.ReportAsync(absent, Outcome.Succeeded, new JsonObject { ["present"] = false });
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Retire)).Status);
    }

    private static async Task StageAndPromoteAsync(HttpClient http, int version, int current, string value)
    {
        var staged = await http.PostAsJsonAsync("/api/variables/candidates", new { key = Shared, value });
        staged.EnsureSuccessStatusCode();
        Assert.Equal(version, (await staged.Content.ReadFromJsonAsync<JsonObject>())!["version"]!.GetValue<int>());
        (await http.PostAsJsonAsync("/api/variables/promote", new
        { key = Shared, version, expectedCurrentVersion = current, verificationNote = "fixture shared access verified" })).EnsureSuccessStatusCode();
    }

    private static TestRepository SharedRepository(bool rootFilesystem = false)
    {
        var repo = rootFilesystem ? Definitions.GuestFilesystemTests.Repository() : TestRepository.Minimal();
        const string installation = "labs/home/proxmox/korriban/installation.yaml";
        repo.Write(installation, File.ReadAllText(Path.Combine(repo.Root, installation))
            .Replace("readOnlyToken: { store: proxmox/korriban/read-only-token }\n  privilegedToken: { store: proxmox/korriban/privileged-token }",
                $"token: {{ store: {Shared} }}"));
        return repo;
    }

    private static async Task<HttpClient> LoginAsync(ServerHarness server)
    {
        var http = server.CreateClient();
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await http.PostAsJsonAsync("/api/auth/setup", new
        { setupCode = code, username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        return http;
    }
}
