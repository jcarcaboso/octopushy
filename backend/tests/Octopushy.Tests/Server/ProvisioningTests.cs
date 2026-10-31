using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class ProvisioningTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string Installation = "home/korriban";

    [Fact]
    public async Task A_new_guest_is_planned_applied_and_verified()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();

        var plan = await agent.NextAsync("preview");
        Assert.Equal("opentofu", plan.Executor);
        var input = JsonNode.Parse(plan.InputJson)!;
        Assert.Equal("home.korriban.krb-app-01", input["stack"]!.GetValue<string>());
        Assert.Equal("modules/opentofu/test-vm", input["root"]!.GetValue<string>());
        Assert.Equal("home.korriban.krb-app-01", input["backend"]!["workspace"]!.GetValue<string>());
        Assert.Equal("backend.PG_CONN_STR", input["secretEnv"]!["PG_CONN_STR"]!.GetValue<string>());
        Assert.Equal("proxmox.token", input["secretEnv"]!["PROXMOX_VE_API_TOKEN"]!.GetValue<string>());
        Assert.Equal("true", input["env"]!["PROXMOX_VE_INSECURE"]!.GetValue<string>());
        Assert.False(input["destroy"]!.GetValue<bool>());
        Assert.Equal("postgres://state", plan.Secrets["backend.PG_CONN_STR"]);
        Assert.Equal("octopushy@pve!agent=secret-token", plan.Secrets["proxmox.token"]);
        Assert.DoesNotContain("secret-token", plan.InputJson);
        Assert.Contains("modules/opentofu/test-vm/main.tf", BundleEntries(plan.FilesTarGz.ToByteArray()));

        var planArtifact = FakeAgent.PlanArtifact();
        Assert.True(await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: planArtifact));

        var apply = await agent.NextAsync("apply");
        Assert.Equal(planArtifact.Sha256, Assert.Single(apply.Artifacts).Sha256);
        await agent.ReportAsync(apply, Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject { ["vm_id"] = 310 }, ["sensitiveOutputs"] = new JsonArray() });

        var verify = await agent.NextAsync("verify-guest");
        var verifyInput = JsonNode.Parse(verify.InputJson)!;
        Assert.Equal(310, verifyInput["vmid"]!.GetValue<int>());
        Assert.Equal("10.1.0.31", verifyInput["expectIpv4"]!.GetValue<string>());
        Assert.Equal("korriban", verifyInput["node"]!.GetValue<string>());
        await agent.ReportAsync(verify, Outcome.Succeeded, new JsonObject { ["present"] = true });
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        Assert.Equal(("approved", "policy"), (approval.Decision, approval.DecidedBy));
        var resource = await server.ResourceAsync(Guest);
        Assert.Equal(("home/korriban", 310), (resource.BoundInstallation, resource.BoundVmid));
        Assert.Equal(resource.DesiredInputsHash, resource.SyncedInputsHash);
        Assert.NotNull(resource.VerifiedAt);
        Assert.Contains("310", resource.Outputs);
    }

    [Fact]
    public async Task A_plan_without_changes_skips_the_apply_but_still_verifies()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan());
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal("The plan has no changes.", run.Detail);
        Assert.Equal(StepStatuses.Skipped, run.Steps.Single(s => s.Name == StepNames.Apply).Status);
        var resource = await server.ResourceAsync(Guest);
        Assert.Null(resource.AppliedRevisionId);
        Assert.Equal(resource.DesiredInputsHash, resource.SyncedInputsHash);
    }

    [Fact]
    public async Task Manual_approval_waits_and_approves_only_the_plan_that_was_shown()
    {
        using var repository = TestRepository.Minimal();
        repository.Write("labs/home/lab.yaml", "schemaVersion: 1\nexecution: { agentLabels: [home] }\npolicy: { approval: manual }\n");
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var artifact = FakeAgent.PlanArtifact();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: artifact);
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Contains("Approval is manual (set by home)", run.Detail);
        Assert.Empty(agent.TakeAssignments());

        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        await Assert.ThrowsAsync<RunException>(() => server.Engine.ApproveAsync(run.Id, approval.Id, new string('0', 64), "ops", null, CancellationToken.None));
        await server.Engine.ApproveAsync(run.Id, approval.Id, artifact.Sha256, "ops", "looks right", CancellationToken.None);

        var apply = await agent.NextAsync("apply");
        await agent.ReportAsync(apply, Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(("approved", "ops"), (run.Steps.Single(s => s.Name == StepNames.Approval).Decision, run.Steps.Single(s => s.Name == StepNames.Approval).DecidedBy));
    }

    [Fact]
    public async Task A_replacement_follows_approval_and_a_newer_commit_waits_for_the_apply()
    {
        using var repository = TestRepository.Minimal();
        repository.Write("labs/home/lab.yaml", "schemaVersion: 1\nexecution: { agentLabels: [home] }\npolicy: { destructive: manual }\n");
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(replace: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.NextAsync("apply");

        repository.Write("labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml",
            File.ReadAllText(Path.Combine(repository.Root, "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml")).Replace("cores: 2", "cores: 4"));
        server.Commit("More cores");
        await server.SyncAndTrustAsync();
        var second = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Queued, second.Status);
    }

    [Fact]
    public async Task A_plan_that_deletes_waits_for_approval_when_destructive_is_manual()
    {
        using var repository = TestRepository.Minimal();
        repository.Write("labs/home/lab.yaml", "schemaVersion: 1\nexecution: { agentLabels: [home] }\npolicy: { destructive: manual }\n");
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1, delete: 1), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Contains("deletes 1 resource", run.Detail);
    }

    [Fact]
    public async Task An_output_only_plan_requires_manual_approval_before_privileged_apply()
    {
        using var repository = TestRepository.Minimal();
        repository.Write("labs/home/lab.yaml", "schemaVersion: 1\nexecution: { agentLabels: [home] }\npolicy: { approval: manual }\n");
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(hasChanges: true), artifacts: FakeAgent.PlanArtifact());

        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.True(approval.ApprovalRequired);
        Assert.Empty(agent.TakeAssignments());
        await server.Engine.ApproveAsync(run.Id, approval.Id, approval.PlanSha256!, "ops", null, CancellationToken.None);
        Assert.Equal("apply", (await agent.NextAsync("apply")).Action);
    }

    [Fact]
    public async Task A_missing_secret_blocks_the_step_until_it_is_set()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("proxmox/korriban/read-only-token", run.Detail);
        Assert.Contains("opentofu/pg", run.Detail);

        await server.SetSecretAsync("proxmox/korriban/read-only-token", "t");
        await server.SetSecretAsync("proxmox/korriban/privileged-token", "writer");
        await server.SetSecretAsync("opentofu/pg", "c");
        Assert.Equal("opentofu", (await agent.NextAsync("preview")).Executor);
        var audit = await server.QueryAsync(db => db.SecretResolutions.AsNoTracking().Select(r => r.Key).ToListAsync());
        Assert.Contains("opentofu/pg", audit);
    }

    [Fact]
    public async Task Without_an_agent_with_the_labels_the_attempt_waits_and_says_why()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.ConnectAgentAsync("office");
        await server.SyncAndTrustAsync();
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        var attempt = Assert.Single(run.Steps.Single(s => s.Name == StepNames.Plan).Attempts);
        Assert.Equal(AttemptStatuses.Queued, attempt.Status);
        Assert.Equal("Waiting for a connected agent with the labels home.", attempt.WaitingReason);
    }

    [Fact]
    public async Task A_new_commit_supersedes_a_run_that_has_not_applied()
    {
        using var repository = TestRepository.Minimal();
        repository.Write("labs/home/lab.yaml", "schemaVersion: 1\nexecution: { agentLabels: [home] }\npolicy: { approval: manual }\n");
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();
        var first = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.AwaitingApproval, first.Status);

        repository.Write("labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml",
            File.ReadAllText(Path.Combine(repository.Root, "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml")).Replace("memory: 2048", "memory: 4096"));
        server.Commit("More memory");
        await server.SyncAndTrustAsync();

        var runs = await server.QueryAsync(db => db.Runs.AsNoTracking().Where(r => r.Operation == Operations.Provision).OrderBy(r => r.CreatedAt).ToListAsync());
        Assert.Equal(2, runs.Count);
        Assert.Equal(RunStatuses.Cancelled, runs[0].Status);
        Assert.StartsWith("Superseded by", runs[0].Detail);
        Assert.Equal("preview", (await agent.NextAsync("preview")).Action);
    }

    [Fact]
    public async Task A_lost_lease_during_apply_is_uncertain_until_the_result_arrives()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        var apply = await agent.NextAsync("apply");
        await server.Engine.OnStartedAsync(agent.Id, Guid.Parse(apply.AttemptId), CancellationToken.None);

        await server.WithDbAsync(db => db.Attempts.Where(a => a.Id == Guid.Parse(apply.AttemptId))
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Uncertain, run.Status);
        await Assert.ThrowsAsync<RunException>(() => server.Engine.CancelAsync(run.Id, "no", CancellationToken.None));

        // The agent comes back and reports what happened.
        Assert.True(await agent.ReportAsync(apply, Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() }));
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.True(run.Steps.Single(s => s.Name == StepNames.Apply).Attempts.Single().LateResult);
    }

    [Fact]
    public async Task A_read_only_attempt_that_loses_its_agent_is_retried()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var plan = await agent.NextAsync("preview");
        await server.WithDbAsync(db => db.Attempts.Where(a => a.Id == Guid.Parse(plan.AttemptId))
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        var retry = await agent.NextAsync("preview");
        Assert.NotEqual(plan.AttemptId, retry.AttemptId);

        // A late result of the first attempt is recorded but does not decide the step.
        await agent.ReportAsync(plan, Outcome.Failed, error: "late");
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(StepStatuses.Running, run.Steps.Single(s => s.Name == StepNames.Plan).Status);
    }

    [Fact]
    public async Task A_failed_step_fails_the_run_and_the_guest()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Failed, error: "Error: Unsupported argument");
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("Unsupported argument", run.Detail);
        Assert.All(run.Steps.Where(s => s.Name != StepNames.Plan), s => Assert.Equal(StepStatuses.Skipped, s.Status));
    }

    [Fact]
    public async Task Retiring_a_guest_destroys_it_after_lifting_protection()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());

        var path = Path.Combine(server.Repository.Root, "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml");
        server.Repository.Write("labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml", File.ReadAllText(path) + "lifecycle: retired\n");
        server.Commit("Retire");
        await server.SyncAndTrustAsync();

        var plan = await agent.NextAsync("preview");
        Assert.True(JsonNode.Parse(plan.InputJson)!["destroy"]!.GetValue<bool>());
        Assert.Equal("octopushy@pve!agent=secret-token", plan.Secrets["proxmox.token"]);
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(delete: 1), artifacts: FakeAgent.PlanArtifact("destroy"));
        var unprotect = await agent.NextAsync("set-protection");
        Assert.False(JsonNode.Parse(unprotect.InputJson)!["protection"]!.GetValue<bool>());
        Assert.Equal("octopushy@pve!writer=writer-token", Assert.Single(unprotect.Secrets).Value);
        await agent.ReportAsync(unprotect, Outcome.Succeeded, new JsonObject { ["previous"] = true, ["protection"] = false, ["absent"] = false });
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        var absent = await agent.NextAsync("verify-absent");
        Assert.Equal("octopushy@pve!agent=secret-token", Assert.Single(absent.Secrets).Value);
        await agent.ReportAsync(absent, Outcome.Succeeded, new JsonObject { ["present"] = false });

        var resource = await server.ResourceAsync(Guest);
        Assert.NotNull(resource.RetiredAt);
        Assert.Null(resource.BoundVmid);

        // Later revisions still recognize the retired guest and propose nothing.
        server.Commit("Unrelated");
        await server.SyncAndTrustAsync();
        var runs = await server.QueryAsync(db => db.Runs.CountAsync());
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Changing_the_vmid_of_a_bound_guest_is_refused()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());

        var path = Path.Combine(server.Repository.Root, "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml");
        server.Repository.Write("labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml", File.ReadAllText(path).Replace("vmid: 310", "vmid: 311"));
        server.Commit("Other VMID");
        await server.SyncAndTrustAsync();

        var resource = await server.ResourceAsync(Guest);
        Assert.False(resource.Valid);
        Assert.Contains("bound to VMID 310", resource.Issues);
        Assert.Equal(1, await server.QueryAsync(db => db.Runs.CountAsync(r => r.Operation == Operations.Provision)));
    }

    [Fact]
    public async Task A_deleted_folder_marks_the_guest_missing_and_replaces_provisioning_with_its_removal()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAndTrustAsync();
        server.Repository.Delete("labs/home/proxmox/korriban/guests/krb-app-01");
        server.Commit("Remove the folder");
        await server.SyncAndTrustAsync();

        var resource = await server.ResourceAsync(Guest);
        Assert.NotNull(resource.MissingSince);
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Cancelled, run.Status);
        Assert.Contains("disappeared", run.Detail);
        Assert.NotNull(RunContext.Parse((await server.LatestRunAsync(Guest, Operations.Retire)).Context).DefinitionRemovedIn);
    }

    [Fact]
    public async Task Observing_an_installation_records_what_proxmox_reports()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var observe = await agent.NextAsync("observe");
        Assert.Equal("proxmox", observe.Executor);
        Assert.Equal("https://10.1.0.10:8006/", JsonNode.Parse(observe.InputJson)!["connection"]!["endpoint"]!.GetValue<string>());
        await agent.ReportAsync(observe, Outcome.Succeeded, new JsonObject
        {
            ["version"] = "8.4.1",
            ["hosts"] = new JsonArray(new JsonObject { ["name"] = "korriban", ["online"] = true }),
            ["guests"] = new JsonArray(new JsonObject { ["vmid"] = 999, ["name"] = "by-hand", ["host"] = "korriban", ["status"] = "running" }),
        });
        var observations = await server.QueryAsync(db => db.Observations.CountAsync());
        Assert.Equal(1, observations);

        var installation = await server.ResourceAsync(Installation);
        Assert.NotNull(installation.ObserveRequestedAt);
        Assert.Null(installation.ObserveError);
        Assert.Equal(0, await server.QueryAsync(db => db.Runs.CountAsync(r => r.ResourceId == installation.Id)));
    }

    [Fact]
    public async Task A_failed_observation_is_recorded_on_the_installation_until_one_succeeds()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("observe"), Outcome.Failed, error: "connection refused");

        var installation = await server.ResourceAsync(Installation);
        Assert.Equal("connection refused", installation.ObserveError);
        Assert.NotNull(installation.ObserveErrorAt);
        Assert.Equal(0, await server.QueryAsync(db => db.Runs.CountAsync(r => r.ResourceId == installation.Id)));

        await server.Services.GetRequiredService<Planner>().ObserveAsync(installation.Id, CancellationToken.None);
        await agent.ReportAsync(await agent.NextAsync("observe"), Outcome.Succeeded, new JsonObject { ["hosts"] = new JsonArray(), ["guests"] = new JsonArray() });
        installation = await server.ResourceAsync(Installation);
        Assert.Null(installation.ObserveError);
        Assert.Equal(1, await server.QueryAsync(db => db.Observations.CountAsync()));
    }

    [Fact]
    public async Task Observing_again_while_an_observation_is_under_way_does_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAndTrustAsync();
        var installation = await server.ResourceAsync(Installation);
        await server.Services.GetRequiredService<Planner>().ObserveAsync(installation.Id, CancellationToken.None);
        await server.Services.GetRequiredService<Planner>().ScheduleObservesAsync(CancellationToken.None);

        Assert.Equal(1, await server.QueryAsync(db => db.Attempts.CountAsync(a => a.ResourceId == installation.Id)));
    }

    [Fact]
    public async Task An_observation_waiting_for_an_agent_takes_the_latest_definition()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAndTrustAsync();
        var path = Path.Combine(server.Repository.Root, "labs/home/proxmox/korriban/installation.yaml");
        server.Repository.Write("labs/home/proxmox/korriban/installation.yaml", File.ReadAllText(path).Replace("10.1.0.10", "10.1.0.11"));
        server.Commit("Move the endpoint");
        await server.SyncAndTrustAsync();

        var agent = await server.ConnectAgentAsync("home");
        var observe = await agent.NextAsync("observe");
        Assert.Equal("https://10.1.0.11:8006/", JsonNode.Parse(observe.InputJson)!["connection"]!["endpoint"]!.GetValue<string>());
        var installation = await server.ResourceAsync(Installation);
        Assert.Equal(1, await server.QueryAsync(db => db.Attempts.CountAsync(a => a.ResourceId == installation.Id)));
    }

    [Fact]
    public async Task A_missing_secret_is_the_observation_error_and_sends_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        await server.SyncAndTrustAsync();

        var installation = await server.ResourceAsync(Installation);
        Assert.StartsWith("Blocked credentials:", installation.ObserveError);
        Assert.Contains("proxmox/korriban/read-only-token: missing", installation.ObserveError);
        Assert.Null(installation.ObserveRequestedAt);
        Assert.Equal(0, await server.QueryAsync(db => db.Attempts.CountAsync(a => a.ResourceId == installation.Id)));

        // It stays due: once the secret exists, the next scheduled check sends the observation.
        await server.SetSecretAsync("proxmox/korriban/read-only-token", "reader");
        var agent = await server.ConnectAgentAsync("home");
        await server.Services.GetRequiredService<Planner>().ScheduleObservesAsync(CancellationToken.None);
        Assert.Equal("reader", Assert.Single((await agent.NextAsync("observe")).Secrets).Value);
    }

    [Fact]
    public async Task A_corrupted_plan_artifact_fails_the_plan()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var corrupted = FakeAgent.PlanArtifact();
        corrupted.Sha256 = new string('a', 64);
        Assert.True(await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: corrupted));
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("did not match its checksum", run.Detail);
        Assert.Equal(0, await server.QueryAsync(db => db.Artifacts.CountAsync()));
    }

    [Fact]
    public async Task Secret_values_are_masked_in_logs_and_errors_by_the_server_too()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var plan = await agent.NextAsync("preview");
        var attemptId = Guid.Parse(plan.AttemptId);
        var logs = server.Services.GetRequiredService<Octopushy.Server.Agents.AttemptLogStore>();
        await logs.AppendAsync(attemptId, 0, [(0, "stdout", "token is octopushy@pve!agent=secret-token")], CancellationToken.None);
        await agent.ReportAsync(plan, Outcome.Failed, error: "auth failed for postgres://state");

        var lines = await logs.ReadAsync(attemptId, -1, 10, CancellationToken.None);
        Assert.Equal("token is ***", Assert.Single(lines).Text);
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal("auth failed for ***", run.Steps.Single(s => s.Name == StepNames.Plan).Attempts.Single().Error);
    }

    private static List<string> BundleEntries(byte[] bundle)
    {
        using var gzip = new GZipStream(new MemoryStream(bundle), CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var names = new List<string>();
        while (reader.GetNextEntry() is { } entry)
        {
            names.Add(entry.Name);
        }

        return names;
    }
}
