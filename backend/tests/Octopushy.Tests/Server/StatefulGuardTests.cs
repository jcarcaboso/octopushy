using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>
/// A stateful guest's data outlives approval: once it has been deployed, a plan that would replace, delete or
/// recreate it fails before anyone is asked, and a first deployment that failed can still be retried. A VMID an
/// installation reserves is off limits to every plan, because nothing else may be keeping it safe.
/// </summary>
[Collection(PostgresCollection.Name)]
public class StatefulGuardTests(PostgresFixture postgres)
{
    private const string Guest = Definitions.ContainerGuestTests.Guest;
    private const string GuestFile = Definitions.ContainerGuestTests.GuestPath;
    private const string Folder = "labs/home/proxmox/korriban/guests/krb-ct-01";
    private const int Vmid = 311;
    private const string Tainted = "replace_because_tainted";
    private const string Forced = "replace_because_cannot_update";
    private static readonly string[] Replace = ["delete", "create"];

    [Fact]
    public async Task A_deployed_stateful_container_cannot_be_replaced_whatever_the_policy_says()
    {
        await using var server = await StartAsync("policy: { destructive: automatic }");
        var agent = await server.ConnectAgentAsync("home");
        await DeployAsync(server, agent);

        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(Replace, Forced), artifacts: FakeAgent.PlanArtifact("replace"));
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("is stateful, and this plan would destroy container 311", run.Detail);
        Assert.Contains("no approval can let this plan through", run.Detail);
        Assert.Contains("in a commit of its own", run.Detail);
        Assert.DoesNotContain("exclude", run.Detail);
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        Assert.Equal(StepStatuses.Failed, approval.Status);
        Assert.Null(approval.Decision);
        Assert.Equal(0, await SetProtectionAttemptsAsync(server));
    }

    [Fact]
    public async Task A_change_in_place_passes()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await DeployAsync(server, agent);

        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(["update"]), artifacts: FakeAgent.PlanArtifact("update"));
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
    }

    [Fact]
    public async Task A_first_deployment_that_failed_can_be_retried()
    {
        await using var server = await StartAsync("policy: { destructive: manual }");
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 2), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Failed, error: "bridge 'lab' does not exist");
        await server.TickAsync();
        Assert.Equal(RunStatuses.Failed, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
        Assert.False(StatefulGuard.Armed(await server.ResourceAsync(Guest)));

        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(Replace, Tainted), artifacts: FakeAgent.PlanArtifact("again"));
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded,
            new JsonObject { ["absent"] = false, ["previous"] = true, ["protection"] = false });
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Contains("whose first apply did not finish", run.Steps.Single(s => s.Name == StepNames.Approval).DecisionNote);
        Assert.True(StatefulGuard.Armed(await server.ResourceAsync(Guest)));
    }

    [Fact]
    public async Task A_guest_that_applied_but_was_never_verified_goes_past_a_person_not_the_guard()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 2), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Failed, error: "no address yet");
        await server.TickAsync();

        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(Replace, Forced), artifacts: FakeAgent.PlanArtifact("again"));
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
    }

    [Fact]
    public async Task A_container_deleted_outside_octopushy_is_not_quietly_recreated_empty()
    {
        await using var server = await StartAsync("policy: { destructive: automatic }");
        var agent = await server.ConnectAgentAsync("home");
        await DeployAsync(server, agent);

        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(["create"], vmid: null), artifacts: FakeAgent.PlanArtifact("recreate"));
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("is gone: this plan would create it again, empty", run.Detail);
    }

    [Fact]
    public async Task Taking_the_marker_out_plans_again_and_the_replacement_waits_for_a_person()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await DeployAsync(server, agent);

        server.Repository.Write(GuestFile, Text(server).Replace("stateful: true\n", "").TrimEnd('\n'));
        server.Commit("Stop guarding the container");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(Replace, Forced), artifacts: FakeAgent.PlanArtifact("replace"));
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Contains("destroys a container and its disks", run.Detail);
    }

    [Fact]
    public async Task Retiring_a_stateful_guest_stops_until_the_marker_is_out()
    {
        await using var server = await StartAsync("policy: { destructive: manual }");
        var agent = await server.ConnectAgentAsync("home");
        await DeployAsync(server, agent);

        server.Repository.Write(GuestFile, ("lifecycle: retired\n" + Text(server)).TrimEnd('\n'));
        server.Commit("Retire it");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(["delete"]), artifacts: FakeAgent.PlanArtifact("destroy"));
        await server.TickAsync();
        var refused = await server.LatestRunAsync(Guest, Operations.Retire);
        Assert.Equal(RunStatuses.Failed, refused.Status);
        Assert.Contains("is stateful", refused.Detail);
        Assert.Equal(0, await SetProtectionAttemptsAsync(server));

        // One commit is enough here: the run is built from this revision.
        server.Repository.Write(GuestFile, Text(server).Replace("stateful: true\n", "").TrimEnd('\n'));
        server.Commit("Take the marker out");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(["delete"]), artifacts: FakeAgent.PlanArtifact("destroy-2"));
        await server.TickAsync();
        Assert.Equal(RunStatuses.AwaitingApproval, (await server.LatestRunAsync(Guest, Operations.Retire)).Status);
    }

    [Fact]
    public async Task A_deleted_folder_stops_too_and_a_later_commit_that_took_the_marker_out_lets_it_go()
    {
        await using var server = await StartAsync("policy: { destructive: manual }");
        var agent = await server.ConnectAgentAsync("home");
        await DeployAsync(server, agent);
        var original = Text(server);

        server.Repository.Delete(Folder);
        server.Commit("Remove the guest");
        await server.SyncAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(["delete"]), artifacts: FakeAgent.PlanArtifact("destroy"));
        await server.TickAsync();
        var refused = await server.LatestRunAsync(Guest, Operations.Retire);
        Assert.Equal(RunStatuses.Failed, refused.Status);
        Assert.Contains("restore the guest's folder", refused.Detail);

        // Restored without the marker. Nothing changes in OpenTofu, so the last applied revision still has it.
        server.Repository.Write(GuestFile, original.Replace("stateful: true\n", "").TrimEnd('\n'));
        server.Commit("Restore it, unguarded");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(), artifacts: FakeAgent.PlanArtifact("nothing"));
        await server.TickAsync();

        server.Repository.Delete(Folder);
        server.Commit("Remove the guest again");
        await server.SyncAsync();
        var plan = await agent.NextAsync("preview");
        Assert.True(JsonNode.Parse(plan.InputJson)!["destroy"]!.GetValue<bool>());
        await agent.ReportAsync(plan, Outcome.Succeeded, ContainerPlan(["delete"]), artifacts: FakeAgent.PlanArtifact("destroy-2"));
        await server.TickAsync();
        Assert.Equal(RunStatuses.AwaitingApproval, (await server.LatestRunAsync(Guest, Operations.Retire)).Status);
    }

    [Fact]
    public async Task No_plan_may_change_a_reserved_vmid_even_on_a_guest_that_is_not_stateful()
    {
        var repository = Repository(stateful: false);
        repository.Write(InstallationFile, "reservedVmids: [100]\n" + File.ReadAllText(Path.Combine(repository.Root, InstallationFile)).TrimEnd('\n'));
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();

        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(["update"], vmid: 100), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("names VMID 100, which the installation reserves", run.Detail);
    }

    [Fact]
    public async Task A_reserved_vmid_is_found_on_any_resource_of_a_plan()
    {
        var repository = Repository(stateful: false);
        repository.Write(InstallationFile, "reservedVmids: [100]\n" + File.ReadAllText(Path.Combine(repository.Root, InstallationFile)).TrimEnd('\n'));
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();

        var plan = FakeAgent.Plan(create: 1);
        plan["changes"] = new JsonArray(new JsonObject
        {
            ["address"] = "proxmox_virtual_environment_firewall_rules.other",
            ["type"] = "proxmox_virtual_environment_firewall_rules",
            ["name"] = "other",
            ["actions"] = new JsonArray("update"),
            ["vmIds"] = new JsonArray(100),
        });
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, plan, artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("names VMID 100", run.Detail);
    }

    [Fact]
    public async Task A_commit_that_breaks_the_definition_does_not_lift_the_guard()
    {
        await using var server = await StartAsync("policy: { destructive: automatic }");
        var agent = await server.ConnectAgentAsync("home");
        await DeployAsync(server, agent);

        server.Repository.Write(GuestFile, ("notAField: true\n" + Text(server)).TrimEnd('\n'));
        server.Commit("Break it");
        await server.SyncAsync();
        Assert.False((await server.ResourceAsync(Guest)).Valid);
        Assert.True(StatefulGuard.Declared(await server.ResourceAsync(Guest)));

        server.Repository.Delete(Folder);
        server.Commit("Remove the guest");
        await server.SyncAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(["delete"]), artifacts: FakeAgent.PlanArtifact("destroy"));
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Retire);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("is stateful", run.Detail);
        Assert.Equal(0, await SetProtectionAttemptsAsync(server));
    }

    [Fact]
    public void A_guest_cannot_use_a_reserved_vmid()
    {
        using var repository = Repository(stateful: false);
        repository.Write(InstallationFile, $"reservedVmids: [{Vmid}]\n" + File.ReadAllText(Path.Combine(repository.Root, InstallationFile)).TrimEnd('\n'));
        var issues = LabRepositoryLoader.Load(repository.Root).Issues;
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains($"VMID {Vmid} is reserved"));
    }

    private const string InstallationFile = "labs/home/proxmox/korriban/installation.yaml";

    private static string Text(ServerHarness server) => File.ReadAllText(Path.Combine(server.Repository.Root, GuestFile));

    private static TestRepository Repository(bool stateful = true, string labPolicy = "")
    {
        var repository = Definitions.ContainerGuestTests.Repository();
        repository.Delete("labs/home/proxmox/korriban/guests/krb-app-01");
        repository.Write("labs/home/lab.yaml", $"schemaVersion: 1\nexecution: {{ agentLabels: [home] }}\n{labPolicy}\n");
        if (stateful)
            repository.Write(GuestFile, ("stateful: true\n" + File.ReadAllText(Path.Combine(repository.Root, GuestFile))).TrimEnd('\n'));
        return repository;
    }

    private Task<ServerHarness> StartAsync(string policy = "") => ServerHarness.StartAsync(postgres, Repository(labPolicy: policy));

    private static async Task DeployAsync(ServerHarness server, FakeAgent agent)
    {
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        Assert.True(StatefulGuard.Armed(await server.ResourceAsync(Guest)));
    }

    private static async Task PlanAgainAsync(ServerHarness server)
    {
        var resource = await server.ResourceAsync(Guest);
        await server.Services.GetRequiredService<Planner>().PlanGuestAsync(resource.Id, CancellationToken.None);
    }

    private static Task<int> SetProtectionAttemptsAsync(ServerHarness server) =>
        server.QueryAsync(db => db.Attempts.CountAsync(a => a.Action == "set-protection"));

    private static JsonObject ContainerPlan(string[] actions, string? reason = null, int? vmid = Vmid)
    {
        var change = new JsonObject
        {
            ["address"] = $"{GuestVmPlan.ContainerResourceType}.guest",
            ["type"] = GuestVmPlan.ContainerResourceType,
            ["name"] = "guest",
            ["actions"] = new JsonArray(actions.Select(a => (JsonNode)a).ToArray()),
        };
        if (reason is not null) change["actionReason"] = reason;
        if (vmid is not null)
        {
            change["vmId"] = vmid;
            change["node"] = "korriban";
        }

        return new JsonObject
        {
            ["hasChanges"] = true,
            ["changes"] = new JsonArray(change),
            ["counts"] = new JsonObject
            {
                ["create"] = actions is ["create"] ? 1 : 0,
                ["update"] = actions is ["update"] ? 1 : 0,
                ["delete"] = actions is ["delete"] ? 1 : 0,
                ["replace"] = actions.Length == 2 ? 1 : 0,
            },
            ["toolVersion"] = "1.13.1",
        };
    }
}
