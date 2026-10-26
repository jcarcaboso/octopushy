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
public class SplitCredentialTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string Reader = "proxmox/korriban/read-only-token";
    private const string Writer = "proxmox/korriban/privileged-token";

    [Fact]
    public async Task Manual_run_releases_only_reader_before_approval_and_only_writer_for_apply()
    {
        using var repo = TestRepository.Minimal();
        repo.Write("labs/home/lab.yaml", """
            schemaVersion: 1
            execution: { agentLabels: [home] }
            policy: { approval: manual }
            """);
        await using var server = await ServerHarness.StartAsync(postgres, repo);
        using var http = await LoginAsync(server);
        var worker = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);
        var preview = await worker.NextAsync("preview");
        Assert.Equal("octopushy@pve!agent=secret-token", preview.Secrets["proxmox.token"]);
        Assert.DoesNotContain("octopushy@pve!writer=writer-token", preview.Secrets.Values);
        Assert.DoesNotContain(SecretNames.ProxmoxReadOnlyToken, preview.Secrets.Keys);
        Assert.DoesNotContain(SecretNames.ProxmoxPrivilegedToken, preview.Secrets.Keys);
        await worker.ReportAsync(preview, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Empty(worker.TakeAssignments());
        Assert.Equal(0, await server.QueryAsync(db => db.SecretResolutions.CountAsync(r => r.Key == Writer)));
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        (await http.PostAsJsonAsync($"/api/runs/{run.Id}/steps/{approval.Id}/approve",
            new { planSha256 = approval.PlanSha256 })).EnsureSuccessStatusCode();
        var apply = await worker.NextAsync("apply");
        Assert.Equal("octopushy@pve!writer=writer-token", apply.Secrets["proxmox.token"]);
        Assert.DoesNotContain("octopushy@pve!agent=secret-token", apply.Secrets.Values);
        await worker.ReportAsync(apply, Outcome.Succeeded);
        var verify = await worker.NextAsync("verify-guest");
        Assert.Equal(preview.Secrets["proxmox.token"], verify.Secrets["proxmox.token"]);
        Assert.Single(verify.Secrets);
    }

    [Theory]
    [InlineData(StepNames.Apply, "pending")]
    [InlineData(StepNames.Unprotect, "pending")]
    [InlineData(StepNames.GrowRoot, "pending")]
    [InlineData(StepNames.VerifyRoot, "pending")]
    [InlineData(StepNames.InstallDiskReporter, "pending")]
    [InlineData(StepNames.InstallDiskReporter, "stale")]
    [InlineData(StepNames.InstallDiskReporter, "missing-artifact")]
    [InlineData(StepNames.InstallDiskReporter, "corrupt-artifact")]
    [InlineData(StepNames.Apply, "denied")]
    [InlineData(StepNames.VerifyRoot, "stale")]
    [InlineData(StepNames.GrowRoot, "status")]
    [InlineData(StepNames.Apply, "missing-artifact")]
    [InlineData(StepNames.Apply, "corrupt-artifact")]
    [InlineData(StepNames.VerifyRoot, "corrupt-artifact")]
    [InlineData(StepNames.GrowRoot, "plan-failed")]
    public async Task Direct_builder_refuses_privileged_release_without_exact_successful_approval(string name, string invalid)
    {
        using var repo = Definitions.GuestFilesystemTests.Repository();
        repo.Write("labs/home/lab.yaml", "schemaVersion: 1\nexecution: { agentLabels: [home] }\npolicy: { approval: manual }");
        await using var server = await ServerHarness.StartAsync(postgres, repo);
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
        Assert.Equal(0, await server.QueryAsync(d => d.SecretResolutions.CountAsync(r => r.Key == Writer)));
        Assert.Equal(0, await server.QueryAsync(d => d.SecretResolutions.CountAsync(r => r.AttemptId == attempt.Id)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Noop_rootfs_preserves_manual_approval_and_fails_closed_without_retained_plan(bool artifact)
    {
        using var repo = Definitions.GuestFilesystemTests.Repository();
        repo.Write("labs/home/lab.yaml", "schemaVersion: 1\nexecution: { agentLabels: [home] }\npolicy: { approval: manual }");
        await using var server = await ServerHarness.StartAsync(postgres, repo);
        using var http = await LoginAsync(server);
        var worker = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);
        var preview = await worker.NextAsync("preview");
        Assert.True(JsonNode.Parse(preview.InputJson)!["retainNoChangesPlan"]!.GetValue<bool>());
        await worker.ReportAsync(preview, Outcome.Succeeded, FakeAgent.Plan(), artifacts: artifact ? [FakeAgent.PlanArtifact()] : []);
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(StepStatuses.Skipped, run.Steps.Single(s => s.Name == StepNames.Apply).Status);
        Assert.Equal(0, await server.QueryAsync(db => db.SecretResolutions.CountAsync(r => r.Key == Writer)));
        if (!artifact)
        {
            Assert.Equal(RunStatuses.Failed, run.Status);
            Assert.Contains("saved plan", run.Detail);
            Assert.DoesNotContain(worker.TakeAssignments(), a => a.Executor == "nixos-rootfs");
            return;
        }
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        Assert.True(approval.ApprovalRequired);
        (await http.PostAsJsonAsync($"/api/runs/{run.Id}/steps/{approval.Id}/approve",
            new { planSha256 = approval.PlanSha256 })).EnsureSuccessStatusCode();
        await worker.ReportAsync(await worker.NextAsync("verify-guest"), Outcome.Succeeded);
        var grow = await worker.NextAsync("grow");
        Assert.Equal("octopushy@pve!writer=writer-token", grow.Secrets["proxmox.token"]);
        await worker.ReportAsync(grow, Outcome.Succeeded);
        var verify = await worker.NextAsync("verify");
        Assert.Equal(grow.Secrets["proxmox.token"], verify.Secrets["proxmox.token"]);
        Assert.False(AssignmentBuilder.ActionFor(StepNames.VerifyRoot).Mutating);
    }

    [Theory]
    [InlineData("legacy", false)]
    [InlineData("legacy", true)]
    [InlineData("writer-alias", false)]
    [InlineData("provider-override", false)]
    public async Task Trusted_queued_context_cannot_bypass_roles_or_upgrade_legacy_bindings(string attack, bool restart)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var http = await LoginAsync(server);
        await server.SyncAndTrustAsync(http);
        await server.TickAsync(); // Pin metadata and queue, without a worker.
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        await server.WithDbAsync(async db =>
        {
            var row = await db.Runs.SingleAsync(r => r.Id == run.Id);
            var context = JsonNode.Parse(row.Context)!;
            if (attack == "legacy")
            {
                var secrets = context["secrets"]!.AsObject();
                secrets.Remove(SecretNames.ProxmoxReadOnlyToken);
                secrets.Remove(SecretNames.ProxmoxPrivilegedToken);
                secrets[SecretNames.ProxmoxToken] = Reader;
            }
            if (attack == "writer-alias")
            {
                context["secrets"]![SecretNames.Backend("PG_CONN_STR")] = Writer;
            }
            if (attack == "provider-override")
            {
                context["openTofu"]!["env"]!["PROXMOX_VE_API_TOKEN"] = "injected";
            }
            row.Context = context.ToJsonString();
            await db.SaveChangesAsync();
        });
        if (restart)
            await server.RestartFixtureAsync();
        var worker = await server.ConnectAgentAsync("home");
        await server.TickAsync();
        Assert.DoesNotContain(worker.TakeAssignments(), a => a.Action == "preview");
        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains(attack == "legacy" ? "replan" : attack == "writer-alias" ? "privileged" : "reserved", run.Detail);
        var attemptIds = run.Steps.SelectMany(s => s.Attempts).Select(a => a.Id).ToArray();
        Assert.False(await server.QueryAsync(db => db.SecretResolutions.AnyAsync(r => attemptIds.Contains(r.AttemptId))));
    }

    [Fact]
    public async Task Provider_denial_of_read_only_plan_never_falls_back_to_writer()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var http = await LoginAsync(server);
        var worker = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);
        var plan = await worker.NextAsync("preview");
        Assert.Equal("octopushy@pve!agent=secret-token", plan.Secrets["proxmox.token"]);
        await worker.ReportAsync(plan, Outcome.Failed, error: "403 VM.Config.Disk required for storage content refresh");
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Single(run.Steps.Single(s => s.Name == StepNames.Plan).Attempts);
        Assert.DoesNotContain(worker.TakeAssignments(), a => a.Action is "preview" or "apply");
        Assert.Equal(0, await server.QueryAsync(db => db.SecretResolutions.CountAsync(r => r.Key == Writer)));
    }

    [Fact]
    public async Task Observe_needs_only_reader_but_guest_metadata_pinning_is_all_or_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        await server.SetSecretAsync(Reader, "reader-only");
        await server.SetSecretAsync("opentofu/pg", "fixture-backend");
        var worker = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var observe = await worker.NextAsync("observe");
        Assert.Equal("reader-only", Assert.Single(observe.Secrets).Value);
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.False(RunContext.Parse(run.Context).SecretBindingsPinned);
        Assert.Empty(RunContext.Parse(run.Context).SecretVersions);
    }

    [Fact]
    public async Task Dispatch_cannot_skip_manual_approval_by_queuing_privileged_work_directly()
    {
        using var repo = TestRepository.Minimal();
        repo.Write("labs/home/lab.yaml", "schemaVersion: 1\nexecution: { agentLabels: [home] }\npolicy: { approval: manual }");
        await using var server = await ServerHarness.StartAsync(postgres, repo);
        using var http = await LoginAsync(server);
        var worker = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);
        await worker.ReportAsync(await worker.NextAsync("preview"), Outcome.Succeeded,
            FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        await server.WithDbAsync(async db =>
        {
            var step = await db.Steps.SingleAsync(s => s.RunId == run.Id && s.Name == StepNames.Apply);
            step.Status = StepStatuses.Queued;
            db.Attempts.Add(new Attempt
            {
                Id = Guid.CreateVersion7(),
                RunId = run.Id,
                StepId = step.Id,
                Number = 1,
                Executor = "opentofu",
                Action = "apply",
                Mutating = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        await server.Engine.DispatchAsync(CancellationToken.None);
        Assert.DoesNotContain(worker.TakeAssignments(), a => a.Action == "apply");
        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(StepStatuses.Failed, run.Steps.Single(s => s.Name == StepNames.Apply).Status);
        Assert.Equal(0, await server.QueryAsync(db => db.SecretResolutions.CountAsync(r => r.Key == Writer)));
    }

    [Theory]
    [InlineData(Outcome.Succeeded)]
    [InlineData(Outcome.Uncertain)]
    public async Task Legacy_inflight_outcomes_reconcile_but_never_release_next_work(Outcome outcome)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var worker = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await worker.ReportAsync(await worker.NextAsync("preview"), Outcome.Succeeded,
            FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        var apply = await worker.NextAsync("apply");
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        await server.WithDbAsync(async db =>
        {
            var row = await db.Runs.SingleAsync(r => r.Id == run.Id);
            var json = JsonNode.Parse(row.Context)!;
            var secrets = json["secrets"]!.AsObject();
            secrets.Remove(SecretNames.ProxmoxReadOnlyToken);
            secrets.Remove(SecretNames.ProxmoxPrivilegedToken);
            secrets[SecretNames.ProxmoxToken] = Writer;
            row.Context = json.ToJsonString();
            await db.SaveChangesAsync();
        });
        Assert.True(await worker.ReportAsync(apply, outcome, error: outcome == Outcome.Uncertain ? "outcome unknown" : null));
        await server.TickAsync();
        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(outcome == Outcome.Uncertain ? RunStatuses.Uncertain : RunStatuses.Blocked, run.Status);
        Assert.Equal(outcome == Outcome.Uncertain ? StepStatuses.Uncertain : StepStatuses.Succeeded,
            run.Steps.Single(s => s.Name == StepNames.Apply).Status);
        Assert.DoesNotContain(worker.TakeAssignments(), a => a.Action == "verify-guest");
    }

    [Fact]
    public async Task Distinct_exact_versions_survive_promotion_restart_retry_and_revoke_without_rebinding()
    {
        using var repo = Definitions.GuestFilesystemTests.Repository();
        const string installation = "labs/home/proxmox/korriban/installation.yaml";
        repo.Write(installation, File.ReadAllText(Path.Combine(repo.Root, installation))
            .Replace($"store: {Reader} }}", $"store: {Reader}, version: 2 }}")
            .Replace($"store: {Writer} }}", $"store: {Writer}, version: 3 }}"));
        await using var server = await ServerHarness.StartAsync(postgres, repo);
        using var http = await LoginAsync(server);
        await StageAndPromoteAsync(http, Reader, 2, 1, "reader-v2");
        (await http.PostAsJsonAsync("/api/variables/candidates", new { key = Writer, value = "writer-v2-unused" })).EnsureSuccessStatusCode();
        await StageAndPromoteAsync(http, Writer, 3, 1, "writer-v3");
        var worker = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);
        var plan = await worker.NextAsync("preview");
        Assert.Equal("reader-v2", plan.Secrets["proxmox.token"]);
        await StageAndPromoteAsync(http, Reader, 3, 2, "reader-v3");
        await StageAndPromoteAsync(http, Writer, 4, 3, "writer-v4");
        await worker.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: FakeAgent.PlanArtifact());
        await server.RestartFixtureAsync();
        using var resumed = server.CreateClient();
        (await resumed.PostAsJsonAsync("/api/auth/login", new { username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        worker = await server.ConnectAgentAsync("home");
        var apply = await worker.NextAsync("apply");
        Assert.Equal("writer-v3", apply.Secrets["proxmox.token"]);
        await worker.ReportAsync(apply, Outcome.Succeeded);
        var verify = await worker.NextAsync("verify-guest");
        Assert.Equal("reader-v2", verify.Secrets["proxmox.token"]);
        await worker.ReportAsync(verify, Outcome.Succeeded);
        var grow = await worker.NextAsync("grow");
        Assert.Equal("writer-v3", grow.Secrets["proxmox.token"]);
        await worker.ReportAsync(grow, Outcome.Failed, error: "fixture guest unit not ready");
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        var context = RunContext.Parse(run.Context);
        Assert.Equal(2, context.SecretVersions[SecretNames.ProxmoxReadOnlyToken]);
        Assert.Equal(3, context.SecretVersions[SecretNames.ProxmoxPrivilegedToken]);
        (await resumed.PostAsync($"/api/runs/{run.Id}/retry-filesystem", null)).EnsureSuccessStatusCode();
        var retry = await worker.NextAsync("grow");
        Assert.Equal("writer-v3", retry.Secrets["proxmox.token"]);
        await worker.ReportAsync(retry, Outcome.Failed, error: "still not ready");
        await server.TickAsync();
        (await resumed.PostAsJsonAsync("/api/variables/revoke", new { key = Writer, version = 3, reason = "fixture revoke pinned writer" })).EnsureSuccessStatusCode();
        var releases = await server.QueryAsync(db => db.SecretResolutions.CountAsync(r => r.Key == Writer));
        (await resumed.PostAsync($"/api/runs/{run.Id}/retry-filesystem", null)).EnsureSuccessStatusCode();
        await server.TickAsync();
        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("v3: revoked", run.Detail);
        Assert.DoesNotContain(worker.TakeAssignments(), a => a.Action == "grow");
        Assert.Equal(releases, await server.QueryAsync(db => db.SecretResolutions.CountAsync(r => r.Key == Writer)));
    }

    private static async Task StageAndPromoteAsync(HttpClient http, string key, int version, int current, string value)
    {
        var staged = await http.PostAsJsonAsync("/api/variables/candidates", new { key, value });
        staged.EnsureSuccessStatusCode();
        Assert.Equal(version, (await staged.Content.ReadFromJsonAsync<JsonObject>())!["version"]!.GetValue<int>());
        (await http.PostAsJsonAsync("/api/variables/promote", new
        { key, version, expectedCurrentVersion = current, verificationNote = "fixture intended role verified" })).EnsureSuccessStatusCode();
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
