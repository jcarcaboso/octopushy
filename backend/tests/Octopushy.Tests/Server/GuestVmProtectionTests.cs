using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>A provision plan that removes the guest's VM has to get past Proxmox's protection flag, and nothing else may touch it.</summary>
[Collection(PostgresCollection.Name)]
public class GuestVmProtectionTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    private const int Vmid = 310;
    private const string Tainted = "replace_because_tainted";
    private const string CannotUpdate = "replace_because_cannot_update";
    private const string BridgeMissing = "bridge 'lab' does not exist";
    private const string Protected = "error deleting VM: can't remove VM 310 - protection mode enabled";
    private const string StoppedByPolicy = "policy: { destructive: manual }";
    private static readonly string[] Replace = ["delete", "create"];

    private static JsonObject Lifted => new() { ["absent"] = false, ["previous"] = true, ["protection"] = false };

    private static JsonObject Restored => new() { ["absent"] = false, ["previous"] = false, ["protection"] = true };

    [Fact]
    public async Task A_replace_after_a_failed_first_apply_recovers_on_its_own()
    {
        await using var server = await StartAsync(StoppedByPolicy);
        var agent = await server.ConnectAgentAsync("home");
        await FailFirstApplyAsync(server, agent);

        await PlanAgainAsync(server);
        var artifact = FakeAgent.PlanArtifact("replace");
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, VmPlan(Replace, Tainted), artifacts: artifact);

        var unprotect = await agent.NextAsync("set-protection");
        var input = JsonNode.Parse(unprotect.InputJson)!;
        Assert.Equal((Vmid, "korriban", false),
            (input["vmid"]!.GetValue<int>(), input["node"]!.GetValue<string>(), input["protection"]!.GetValue<bool>()));
        Assert.Equal("octopushy@pve!writer=writer-token", Assert.Single(unprotect.Secrets).Value);
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        Assert.Equal(("approved", "policy", false), (approval.Decision, approval.DecidedBy, approval.ApprovalRequired));
        Assert.Contains("first apply did not finish", approval.DecisionNote);

        await agent.ReportAsync(unprotect, Outcome.Succeeded, Lifted);
        var apply = await agent.NextAsync("apply");
        Assert.Equal(artifact.Sha256, Assert.Single(apply.Artifacts, a => a.Name == artifact.Name).Sha256);
        await agent.ReportAsync(apply, Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject { ["vm_id"] = Vmid } });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());

        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.DoesNotContain(run.Steps, s => s.Name == StepNames.Reprotect);
        Assert.Equal(Vmid, (await server.ResourceAsync(Guest)).BoundVmid);
    }

    [Fact]
    public async Task A_plain_update_leaves_protection_alone()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();

        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, VmPlan(["update"]), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        var unprotect = run.Steps.Single(s => s.Name == StepNames.Unprotect);
        Assert.Equal(StepStatuses.Skipped, unprotect.Status);
        Assert.Contains("protection stays", unprotect.Detail);
        Assert.Equal(0, await SetProtectionAttemptsAsync(server));
    }

    [Fact]
    public async Task A_failed_apply_of_a_plain_update_touches_no_protection()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();

        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, VmPlan(["update"]), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Failed, error: BridgeMissing);
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.DoesNotContain(run.Steps, s => s.Name == StepNames.Reprotect);
        Assert.Equal(0, await SetProtectionAttemptsAsync(server));
    }

    [Fact]
    public async Task A_preview_of_a_replacement_changes_nothing()
    {
        await using var server = await StartAsync("policy: { approval: manual }");
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();

        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, VmPlan(Replace, Tainted), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Empty(agent.TakeAssignments());
        Assert.Equal(0, await SetProtectionAttemptsAsync(server));

        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        await server.Engine.RejectAsync(run.Id, approval.Id, "ops", null, CancellationToken.None);
        await server.TickAsync();
        Assert.Empty(agent.TakeAssignments());
        Assert.Equal(0, await SetProtectionAttemptsAsync(server));
    }

    [Fact]
    public async Task An_apply_that_fails_after_protection_was_lifted_restores_it_and_a_rerun_still_works()
    {
        await using var server = await StartAsync(StoppedByPolicy);
        var agent = await server.ConnectAgentAsync("home");
        await FailFirstApplyAsync(server, agent);
        await LiftAndFailApplyAsync(server, agent);

        var reprotect = await agent.NextAsync("set-protection");
        var input = JsonNode.Parse(reprotect.InputJson)!;
        Assert.Equal((Vmid, "korriban", true),
            (input["vmid"]!.GetValue<int>(), input["node"]!.GetValue<string>(), input["protection"]!.GetValue<bool>()));
        Assert.Equal("octopushy@pve!writer=writer-token", Assert.Single(reprotect.Secrets).Value);

        // The rerun waits: it must not race the restore.
        var failing = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Running, failing.Status);
        Assert.Contains("Restoring the protection of VM 310", failing.Detail);
        Assert.Equal(StepStatuses.Failed, failing.Steps.Single(s => s.Name == StepNames.Apply).Status);

        await agent.ReportAsync(reprotect, Outcome.Succeeded, Restored);
        await server.TickAsync();
        failing = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, failing.Status);
        Assert.Contains(BridgeMissing, failing.Detail);
        Assert.Contains("Protection of VM 310 is back on. Plan again to retry.", failing.Detail);
        Assert.Equal(StepStatuses.Skipped, failing.Steps.Single(s => s.Name == StepNames.Verify).Status);

        await ReplaceToSuccessAsync(server, agent);
    }

    [Fact]
    public async Task A_vm_the_failed_apply_already_deleted_needs_no_restoring()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await FailFirstApplyAsync(server, agent);
        await LiftAndFailApplyAsync(server, agent);

        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded,
            new JsonObject { ["absent"] = true, ["previous"] = null, ["protection"] = null });
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("VM 310 no longer exists", run.Detail);
    }

    [Fact]
    public async Task A_restore_that_fails_says_so_and_a_rerun_still_works()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await FailFirstApplyAsync(server, agent);
        await LiftAndFailApplyAsync(server, agent);

        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Failed, error: "connection refused");
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("may still be off", run.Detail);
        Assert.Equal(StepStatuses.Failed, run.Steps.Single(s => s.Name == StepNames.Reprotect).Status);

        await ReplaceToSuccessAsync(server, agent, previous: false);
    }

    [Fact]
    public async Task Protection_that_was_already_off_is_not_turned_on()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await FailFirstApplyAsync(server, agent);
        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, VmPlan(Replace, Tainted), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded,
            new JsonObject { ["absent"] = false, ["previous"] = false, ["protection"] = false });
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Failed, error: BridgeMissing);
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.DoesNotContain(run.Steps, s => s.Name == StepNames.Reprotect);
    }

    [Fact]
    public async Task A_run_cancelled_between_lifting_and_applying_restores_protection()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await FailFirstApplyAsync(server, agent);
        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, VmPlan(Replace, Tainted), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded, Lifted);

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.False(RunEngine.CanSupersede(run));
        await server.Engine.CancelAsync(run.Id, "Not now.", CancellationToken.None);
        var reprotect = await agent.NextAsync("set-protection");
        Assert.True(JsonNode.Parse(reprotect.InputJson)!["protection"]!.GetValue<bool>());
        await agent.ReportAsync(reprotect, Outcome.Succeeded, Restored);
        await server.TickAsync();

        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Cancelled, run.Status);
        Assert.Contains("Not now.", run.Detail);
        Assert.Contains("back on", run.Detail);
        Assert.Equal(StepStatuses.Cancelled, run.Steps.Single(s => s.Name == StepNames.Apply).Status);
    }

    [Fact]
    public async Task Cancelling_a_restore_that_has_not_started_says_protection_may_be_off()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await FailFirstApplyAsync(server, agent);
        await LiftAndFailApplyAsync(server, agent);

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(StepStatuses.Queued, run.Steps.Single(s => s.Name == StepNames.Reprotect).Status);
        await server.Engine.CancelAsync(run.Id, "Not now.", CancellationToken.None);

        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Cancelled, run.Status);
        Assert.Contains("may still be off", run.Detail);
        Assert.Equal(StepStatuses.Cancelled, run.Steps.Single(s => s.Name == StepNames.Reprotect).Status);
    }

    [Fact]
    public async Task An_uncertain_apply_restores_protection_only_once_it_is_closed()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await FailFirstApplyAsync(server, agent);
        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, VmPlan(Replace, Tainted), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded, Lifted);
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Uncertain, error: "The agent cannot tell.");
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Uncertain, run.Status);
        Assert.Empty(agent.TakeAssignments());

        await server.Engine.CloseUncertainAsync(run.Id, "ops", CancellationToken.None);
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded, Restored);
        await server.TickAsync();

        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("Closed by ops", run.Detail);
        Assert.Contains("back on", run.Detail);
    }

    [Fact]
    public async Task A_lift_with_no_clear_answer_restores_protection_once_it_is_closed()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await FailFirstApplyAsync(server, agent);
        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, VmPlan(Replace, Tainted), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Uncertain, error: "the change could not be confirmed");
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Uncertain, run.Status);
        Assert.Equal(StepStatuses.Pending, run.Steps.Single(s => s.Name == StepNames.Apply).Status);
        Assert.Empty(agent.TakeAssignments());

        await server.Engine.CloseUncertainAsync(run.Id, "ops", CancellationToken.None);
        var reprotect = await agent.NextAsync("set-protection");
        Assert.True(JsonNode.Parse(reprotect.InputJson)!["protection"]!.GetValue<bool>());
        await agent.ReportAsync(reprotect, Outcome.Succeeded, Restored);
        await server.TickAsync();

        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("back on", run.Detail);
        Assert.Empty(run.Steps.Single(s => s.Name == StepNames.Apply).Attempts);
        await ReplaceToSuccessAsync(server, agent);
    }

    [Fact]
    public async Task A_lost_restore_stays_uncertain_until_it_is_closed()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await FailFirstApplyAsync(server, agent);
        await LiftAndFailApplyAsync(server, agent);
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Uncertain, error: "Proxmox may have changed it");
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Uncertain, run.Status);

        await server.Engine.CloseUncertainAsync(run.Id, "ops", CancellationToken.None);
        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("may still be off", run.Detail);
        Assert.Single(run.Steps.Single(s => s.Name == StepNames.Reprotect).Attempts);
    }

    [Theory]
    [InlineData(StoppedByPolicy, "it is manual, set by home")]
    [InlineData("", "it is automatic, set by default, following approval")]
    public async Task Replacing_an_established_vm_waits_for_a_person(string policy, string reason)
    {
        await using var server = await StartAsync(policy);
        var agent = await server.ConnectAgentAsync("home");
        await EstablishAsync(server, agent);

        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, VmPlan(Replace, CannotUpdate), artifacts: FakeAgent.PlanArtifact("replace"));
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Contains("destroys a VM and its disks", run.Detail);
        Assert.Contains(reason, run.Detail);
        Assert.Equal(0, await SetProtectionAttemptsAsync(server));

        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        await server.Engine.ApproveAsync(run.Id, approval.Id, approval.PlanSha256!, "ops", "wipe it", CancellationToken.None);
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded, Lifted);
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());

        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal("ops", run.Steps.Single(s => s.Name == StepNames.Approval).DecidedBy);
    }

    [Fact]
    public async Task A_tainted_vm_that_was_established_before_still_waits()
    {
        await using var server = await StartAsync(StoppedByPolicy);
        var agent = await server.ConnectAgentAsync("home");
        await EstablishAsync(server, agent);

        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, VmPlan(Replace, Tainted), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        Assert.Equal(RunStatuses.AwaitingApproval, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
    }

    [Fact]
    public async Task A_never_verified_vm_replaced_by_an_edit_waits()
    {
        await using var server = await StartAsync(StoppedByPolicy);
        var agent = await server.ConnectAgentAsync("home");
        await FailFirstApplyAsync(server, agent);

        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, VmPlan(Replace, CannotUpdate), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        Assert.Equal(RunStatuses.AwaitingApproval, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
    }

    [Fact]
    public async Task Explicitly_automatic_destructive_replaces_an_established_vm_without_a_person()
    {
        await using var server = await StartAsync("policy: { destructive: automatic }");
        var agent = await server.ConnectAgentAsync("home");
        await EstablishAsync(server, agent);

        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, VmPlan(Replace, CannotUpdate), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded, Lifted);

        var approval = (await server.LatestRunAsync(Guest, Operations.Provision)).Steps.Single(s => s.Name == StepNames.Approval);
        Assert.Equal("policy", approval.DecidedBy);
        Assert.Contains("Approval and destructive are automatic", approval.DecisionNote);
        Assert.Contains("destroys a VM and its disks", approval.DecisionNote);
    }

    [Fact]
    public async Task A_provision_plan_that_deletes_the_vm_is_treated_like_replacing_it()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await EstablishAsync(server, agent);

        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, VmPlan(["delete"]), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        await server.Engine.ApproveAsync(run.Id, approval.Id, approval.PlanSha256!, "ops", null, CancellationToken.None);
        Assert.False(JsonNode.Parse((await agent.NextAsync("set-protection")).InputJson)!["protection"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(311, "korriban", "VMID 311 on node korriban")]
    [InlineData(Vmid, "dromund", "VMID 310 on node dromund")]
    [InlineData(null, null, "an unknown VMID on an unknown node")]
    [InlineData(Vmid, null, "VMID 310 on an unknown node")]
    public async Task A_vm_the_plan_does_not_identify_as_the_guests_is_refused(int? vmid, string? node, string named)
    {
        // Explicitly automatic destructive: no policy lets this through.
        await using var server = await StartAsync("policy: { approval: automatic, destructive: automatic }");
        var agent = await server.ConnectAgentAsync("home");
        await FailFirstApplyAsync(server, agent);

        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, VmPlan(Replace, Tainted, vmid, node), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains(named, run.Detail);
        Assert.Contains("refuses to remove a machine it does not manage", run.Detail);
        Assert.Equal(StepStatuses.Failed, run.Steps.Single(s => s.Name == StepNames.Plan).Status);
        // No approval was offered, and nothing changed.
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        Assert.Equal((StepStatuses.Skipped, null), (approval.Status, approval.Decision));
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action is "apply" or "set-protection");
        Assert.Equal(0, await SetProtectionAttemptsAsync(server));
        Assert.Equal(0, await server.QueryAsync(db => db.Attempts.CountAsync(a => a.RunId == run.Id && a.Action == "apply")));
    }

    [Fact]
    public async Task Recovering_the_guests_vm_does_not_excuse_replacing_another()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await FailFirstApplyAsync(server, agent);

        await PlanAgainAsync(server);
        var plan = VmPlan(Replace, Tainted);
        plan["changes"]!.AsArray().Add(VmChange(Replace, CannotUpdate, 311, "korriban", "sidecar"));
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, plan, artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("proxmox_virtual_environment_vm.sidecar replaces VMID 311 on node korriban", run.Detail);
        Assert.DoesNotContain("VMID 310 on", run.Detail);
        Assert.Equal(0, await SetProtectionAttemptsAsync(server));
    }

    [Fact]
    public async Task Replacing_something_other_than_a_vm_still_follows_approval()
    {
        await using var server = await StartAsync(StoppedByPolicy);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();

        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(replace: 1), artifacts: FakeAgent.PlanArtifact());

        Assert.Equal("apply", (await agent.NextAsync("apply")).Action);
        Assert.Equal(0, await SetProtectionAttemptsAsync(server));
    }

    [Fact]
    public async Task The_builder_refuses_to_lift_protection_for_a_plan_that_keeps_the_vm()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var plan = await agent.NextAsync("preview");
        await agent.ReportAsync(plan, Outcome.Succeeded, VmPlan(["update"]), artifacts: FakeAgent.PlanArtifact());
        var run = await server.LatestRunAsync(Guest, Operations.Provision);

        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
        var step = await db.Steps.SingleAsync(s => s.RunId == run.Id && s.Name == StepNames.Unprotect);
        var attempt = new Attempt
        {
            Id = Guid.CreateVersion7(),
            RunId = run.Id,
            StepId = step.Id,
            Number = 1,
            Executor = "proxmox",
            Action = "set-protection",
            Mutating = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Attempts.Add(attempt);
        await db.SaveChangesAsync();

        var refused = await Assert.ThrowsAsync<AssignmentException>(() => scope.ServiceProvider.GetRequiredService<AssignmentBuilder>()
            .BuildAsync(attempt, step, run, RunContext.Parse(run.Context), agent.Id, CancellationToken.None));
        Assert.Contains("Protection is lifted only", refused.Message);
    }

    [Fact]
    public async Task A_retire_run_that_fails_keeps_its_behaviour()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await EstablishAsync(server, agent);

        var path = Path.Combine(server.Repository.Root, "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml");
        server.Repository.Write("labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml", File.ReadAllText(path) + "lifecycle: retired\n");
        server.Commit("Retire");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, VmPlan(["delete"]), artifacts: FakeAgent.PlanArtifact("destroy"));
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded, Lifted);
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Failed, error: "boom");
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Retire);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.DoesNotContain(run.Steps, s => s.Name == StepNames.Reprotect);
    }

    private async Task<ServerHarness> StartAsync(string policy = "")
    {
        var repository = TestRepository.Minimal();
        repository.Write("labs/home/lab.yaml", $"schemaVersion: 1\nexecution: {{ agentLabels: [home] }}\n{policy}\n");
        return await ServerHarness.StartAsync(postgres, repository);
    }

    /// <summary>Run #1 of the incident: creating the VM works, starting it does not.</summary>
    private static async Task FailFirstApplyAsync(ServerHarness server, FakeAgent agent)
    {
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 2), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Failed, error: BridgeMissing);
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Equal(0, await SetProtectionAttemptsAsync(server));
    }

    private static async Task EstablishAsync(ServerHarness server, FakeAgent agent)
    {
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        Assert.NotNull((await server.ResourceAsync(Guest)).VerifiedAt);
    }

    private static async Task LiftAndFailApplyAsync(ServerHarness server, FakeAgent agent)
    {
        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, VmPlan(Replace, Tainted), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded, Lifted);
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Failed, error: BridgeMissing);
    }

    private static async Task ReplaceToSuccessAsync(ServerHarness server, FakeAgent agent, bool previous = true)
    {
        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, VmPlan(Replace, Tainted), artifacts: FakeAgent.PlanArtifact("again"));
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded,
            new JsonObject { ["absent"] = false, ["previous"] = previous, ["protection"] = false });
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
    }

    private static async Task PlanAgainAsync(ServerHarness server)
    {
        var resource = await server.ResourceAsync(Guest);
        await server.Services.GetRequiredService<Planner>().PlanGuestAsync(resource.Id, CancellationToken.None);
    }

    private static Task<int> SetProtectionAttemptsAsync(ServerHarness server) =>
        server.QueryAsync(db => db.Attempts.CountAsync(a => a.Action == "set-protection"));

    /// <summary>A plan with one change to a Proxmox VM, as the agent summarizes it.</summary>
    private static JsonObject VmPlan(string[] actions, string? reason = null) => VmPlan(actions, reason, Vmid, "korriban");

    private static JsonObject VmPlan(string[] actions, string? reason, int? vmid, string? node)
    {
        var replacing = actions.Length == 2;
        return new JsonObject
        {
            ["hasChanges"] = true,
            ["changes"] = new JsonArray(VmChange(actions, reason, vmid, node)),
            ["counts"] = new JsonObject
            {
                ["create"] = 0,
                ["update"] = actions is ["update"] ? 1 : 0,
                ["delete"] = actions is ["delete"] ? 1 : 0,
                ["replace"] = replacing ? 1 : 0,
            },
            ["toolVersion"] = "1.13.1",
        };
    }

    private static JsonObject VmChange(string[] actions, string? reason, int? vmid, string? node, string name = "guest")
    {
        var change = new JsonObject
        {
            ["address"] = $"{GuestVmPlan.VmResourceType}.{name}",
            ["type"] = GuestVmPlan.VmResourceType,
            ["name"] = name,
            ["actions"] = new JsonArray(actions.Select(a => (JsonNode)a).ToArray()),
        };
        if (reason is not null) change["actionReason"] = reason;
        if (vmid is not null) change["vmId"] = vmid;
        if (node is not null) change["node"] = node;
        return change;
    }
}
