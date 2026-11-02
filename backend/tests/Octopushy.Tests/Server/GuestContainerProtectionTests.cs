using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>
/// The rule for a guest's own VM, for its container: replacing one wipes its disks too, so a plan
/// that does goes only past a person, or past a first apply that never finished, and Octopushy
/// lifts and restores Proxmox's protection flag through the container API. Mirrors
/// <see cref="GuestVmProtectionTests"/>, whose behaviour is not changed.
/// </summary>
[Collection(PostgresCollection.Name)]
public class GuestContainerProtectionTests(PostgresFixture postgres)
{
    private const string Guest = Definitions.ContainerGuestTests.Guest;
    private const int Vmid = 311;
    private const string Tainted = "replace_because_tainted";
    private const string CannotUpdate = "replace_because_cannot_update";
    private const string BridgeMissing = "bridge 'lab' does not exist";
    private const string StoppedByPolicy = "policy: { destructive: manual }";
    private static readonly string[] Replace = ["delete", "create"];

    private static JsonObject Lifted => new() { ["absent"] = false, ["previous"] = true, ["protection"] = false };

    private static JsonObject Restored => new() { ["absent"] = false, ["previous"] = false, ["protection"] = true };

    [Fact]
    public async Task A_container_replaced_after_a_failed_first_apply_recovers_on_its_own()
    {
        await using var server = await StartAsync(StoppedByPolicy);
        var agent = await server.ConnectAgentAsync("home");
        await FailFirstApplyAsync(server, agent);

        await PlanAgainAsync(server);
        var artifact = FakeAgent.PlanArtifact("replace");
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(Replace, Tainted), artifacts: artifact);

        var unprotect = await agent.NextAsync("set-protection");
        var input = JsonNode.Parse(unprotect.InputJson)!;
        Assert.Equal((Vmid, "korriban", false, "lxc"),
            (input["vmid"]!.GetValue<int>(), input["node"]!.GetValue<string>(), input["protection"]!.GetValue<bool>(), input["guestKind"]!.GetValue<string>()));
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        Assert.Equal(("approved", "policy", false), (approval.Decision, approval.DecidedBy, approval.ApprovalRequired));
        Assert.Contains("recreates container 311, whose first apply did not finish", approval.DecisionNote);

        await agent.ReportAsync(unprotect, Outcome.Succeeded, Lifted);
        var apply = await agent.NextAsync("apply");
        Assert.Equal(artifact.Sha256, Assert.Single(apply.Artifacts).Sha256);
        await agent.ReportAsync(apply, Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject { ["vm_id"] = Vmid } });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());

        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.DoesNotContain(run.Steps, s => s.Name == StepNames.Reprotect);
        Assert.Equal(Vmid, (await server.ResourceAsync(Guest)).BoundVmid);
    }

    [Fact]
    public async Task A_plain_update_of_a_container_leaves_protection_alone()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();

        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(["update"]), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        var unprotect = run.Steps.Single(s => s.Name == StepNames.Unprotect);
        Assert.Equal(StepStatuses.Skipped, unprotect.Status);
        Assert.Contains("this guest's container, so its protection stays", unprotect.Detail);
        Assert.Equal(0, await SetProtectionAttemptsAsync(server));
    }

    [Fact]
    public async Task An_apply_that_fails_after_protection_was_lifted_restores_it_through_the_container_api()
    {
        await using var server = await StartAsync(StoppedByPolicy);
        var agent = await server.ConnectAgentAsync("home");
        await FailFirstApplyAsync(server, agent);
        await LiftAndFailApplyAsync(server, agent);

        var reprotect = await agent.NextAsync("set-protection");
        var input = JsonNode.Parse(reprotect.InputJson)!;
        Assert.Equal((Vmid, "korriban", true, "lxc"),
            (input["vmid"]!.GetValue<int>(), input["node"]!.GetValue<string>(), input["protection"]!.GetValue<bool>(), input["guestKind"]!.GetValue<string>()));

        var failing = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Running, failing.Status);
        Assert.Contains("Restoring the protection of container 311", failing.Detail);

        await agent.ReportAsync(reprotect, Outcome.Succeeded, Restored);
        await server.TickAsync();
        failing = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, failing.Status);
        Assert.Contains(BridgeMissing, failing.Detail);
        Assert.Contains("Protection of container 311 is back on. Plan again to retry.", failing.Detail);

        await ReplaceToSuccessAsync(server, agent);
    }

    [Fact]
    public async Task A_container_the_failed_apply_already_deleted_needs_no_restoring()
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
        Assert.Contains("Container 311 no longer exists", run.Detail);
    }

    [Fact]
    public async Task A_restore_that_fails_says_container_and_a_rerun_still_works()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await FailFirstApplyAsync(server, agent);
        await LiftAndFailApplyAsync(server, agent);

        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Failed, error: "connection refused");
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("Protection of container 311 could not be restored", run.Detail);

        await ReplaceToSuccessAsync(server, agent, previous: false);
    }

    [Theory]
    [InlineData(StoppedByPolicy, "it is manual, set by home")]
    [InlineData("", "it is automatic, set by default, following approval")]
    public async Task Replacing_an_established_container_waits_for_a_person(string policy, string reason)
    {
        await using var server = await StartAsync(policy);
        var agent = await server.ConnectAgentAsync("home");
        await EstablishAsync(server, agent);

        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(Replace, CannotUpdate), artifacts: FakeAgent.PlanArtifact("replace"));
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Contains("destroys a container and its disks", run.Detail);
        Assert.DoesNotContain("destroys a VM", run.Detail);
        Assert.Contains(reason, run.Detail);
        Assert.Equal(0, await SetProtectionAttemptsAsync(server));

        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        await server.Engine.ApproveAsync(run.Id, approval.Id, approval.PlanSha256!, "ops", "wipe it", CancellationToken.None);
        var unprotect = await agent.NextAsync("set-protection");
        Assert.Equal("lxc", JsonNode.Parse(unprotect.InputJson)!["guestKind"]!.GetValue<string>());
        await agent.ReportAsync(unprotect, Outcome.Succeeded, Lifted);
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());

        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal("ops", run.Steps.Single(s => s.Name == StepNames.Approval).DecidedBy);
    }

    [Fact]
    public async Task A_tainted_container_that_was_established_before_still_waits()
    {
        await using var server = await StartAsync(StoppedByPolicy);
        var agent = await server.ConnectAgentAsync("home");
        await EstablishAsync(server, agent);

        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(Replace, Tainted), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        Assert.Equal(RunStatuses.AwaitingApproval, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
    }

    [Fact]
    public async Task A_never_verified_container_replaced_by_an_edit_waits()
    {
        await using var server = await StartAsync(StoppedByPolicy);
        var agent = await server.ConnectAgentAsync("home");
        await FailFirstApplyAsync(server, agent);

        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(Replace, CannotUpdate), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        Assert.Equal(RunStatuses.AwaitingApproval, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
    }

    [Fact]
    public async Task Explicitly_automatic_destructive_replaces_an_established_container_without_a_person()
    {
        await using var server = await StartAsync("policy: { destructive: automatic }");
        var agent = await server.ConnectAgentAsync("home");
        await EstablishAsync(server, agent);

        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(Replace, CannotUpdate), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded, Lifted);

        var approval = (await server.LatestRunAsync(Guest, Operations.Provision)).Steps.Single(s => s.Name == StepNames.Approval);
        Assert.Equal("policy", approval.DecidedBy);
        Assert.Contains("Approval and destructive are automatic", approval.DecisionNote);
        Assert.Contains("destroys a container and its disks", approval.DecisionNote);
    }

    [Fact]
    public async Task A_plan_that_deletes_the_container_is_treated_like_replacing_it()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await EstablishAsync(server, agent);

        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(["delete"]), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        await server.Engine.ApproveAsync(run.Id, approval.Id, approval.PlanSha256!, "ops", null, CancellationToken.None);
        Assert.False(JsonNode.Parse((await agent.NextAsync("set-protection")).InputJson)!["protection"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(312, "korriban")]
    [InlineData(Vmid, "dromund")]
    [InlineData(null, null)]
    public async Task A_container_the_plan_does_not_identify_as_the_guests_keeps_its_protection(int? vmid, string? node)
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await FailFirstApplyAsync(server, agent);

        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(Replace, Tainted, vmid, node), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(StepStatuses.Skipped, run.Steps.Single(s => s.Name == StepNames.Unprotect).Status);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        await server.Engine.ApproveAsync(run.Id, approval.Id, approval.PlanSha256!, "ops", null, CancellationToken.None);
        Assert.Equal("apply", (await agent.NextAsync("apply")).Action);
        Assert.Equal(0, await SetProtectionAttemptsAsync(server));
    }

    [Fact]
    public async Task A_vm_resource_with_the_containers_id_does_not_lift_its_protection()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await FailFirstApplyAsync(server, agent);

        await PlanAgainAsync(server);
        var plan = ContainerPlan(Replace, Tainted);
        plan["changes"]!.AsArray()[0]!["type"] = GuestVmPlan.VmResourceType;
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, plan, artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(StepStatuses.Skipped, run.Steps.Single(s => s.Name == StepNames.Unprotect).Status);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Equal(0, await SetProtectionAttemptsAsync(server));
    }

    [Fact]
    public async Task Recovering_the_guests_container_does_not_excuse_replacing_another()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await FailFirstApplyAsync(server, agent);

        await PlanAgainAsync(server);
        var plan = ContainerPlan(Replace, Tainted);
        plan["changes"]!.AsArray().Add(ContainerChange(Replace, CannotUpdate, 312, "korriban", "sidecar"));
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, plan, artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        Assert.Equal(RunStatuses.AwaitingApproval, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
    }

    [Fact]
    public async Task The_builder_refuses_to_lift_protection_for_a_plan_that_keeps_the_container()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var plan = await agent.NextAsync("preview");
        await agent.ReportAsync(plan, Outcome.Succeeded, ContainerPlan(["update"]), artifacts: FakeAgent.PlanArtifact());
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
        Assert.Contains("container", refused.Message);
    }

    private async Task<ServerHarness> StartAsync(string policy = "")
    {
        var repository = Definitions.ContainerGuestTests.Repository();
        // Only the container: the test agent answers one guest's steps.
        repository.Delete("labs/home/proxmox/korriban/guests/krb-app-01");
        repository.Write("labs/home/lab.yaml", $"schemaVersion: 1\nexecution: {{ agentLabels: [home] }}\n{policy}\n");
        return await ServerHarness.StartAsync(postgres, repository);
    }

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
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(Replace, Tainted), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded, Lifted);
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Failed, error: BridgeMissing);
    }

    private static async Task ReplaceToSuccessAsync(ServerHarness server, FakeAgent agent, bool previous = true)
    {
        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(Replace, Tainted), artifacts: FakeAgent.PlanArtifact("again"));
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

    /// <summary>A plan with one change to a Proxmox container, as the agent summarizes it.</summary>
    private static JsonObject ContainerPlan(string[] actions, string? reason = null) => ContainerPlan(actions, reason, Vmid, "korriban");

    private static JsonObject ContainerPlan(string[] actions, string? reason, int? vmid, string? node) => new()
    {
        ["hasChanges"] = true,
        ["changes"] = new JsonArray(ContainerChange(actions, reason, vmid, node)),
        ["counts"] = new JsonObject
        {
            ["create"] = 0,
            ["update"] = actions is ["update"] ? 1 : 0,
            ["delete"] = actions is ["delete"] ? 1 : 0,
            ["replace"] = actions.Length == 2 ? 1 : 0,
        },
        ["toolVersion"] = "1.13.1",
    };

    private static JsonObject ContainerChange(string[] actions, string? reason, int? vmid, string? node, string name = "guest")
    {
        var change = new JsonObject
        {
            ["address"] = $"{GuestVmPlan.ContainerResourceType}.{name}",
            ["type"] = GuestVmPlan.ContainerResourceType,
            ["name"] = name,
            ["actions"] = new JsonArray(actions.Select(a => (JsonNode)a).ToArray()),
        };
        if (reason is not null) change["actionReason"] = reason;
        if (vmid is not null) change["vmId"] = vmid;
        if (node is not null) change["node"] = node;
        return change;
    }
}
