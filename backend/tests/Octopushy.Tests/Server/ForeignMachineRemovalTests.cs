using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Octopushy.Tests.Definitions;
using static Octopushy.Tests.Server.SafetyBoundaryFixtures;

namespace Octopushy.Tests.Server;

/// <summary>A plan that deletes or replaces a Proxmox machine that is not its run's own is refused, whatever the policy. See docs/safety-boundary.md.</summary>
[Collection(PostgresCollection.Name)]
public class ForeignMachineRemovalTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_provision_plan_that_removes_a_foreign_container_is_refused()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Policy("policy: { approval: automatic, destructive: automatic }"));
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();

        var plan = MachinePlan(Change(GuestVmPlan.ContainerResourceType, ["delete"], 100, "korriban", "homeassistant"));
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, plan, artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("proxmox_virtual_environment_container.homeassistant deletes VMID 100 on node korriban", run.Detail);
        Assert.Equal(StepStatuses.Skipped, run.Steps.Single(s => s.Name == StepNames.Approval).Status);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action is "apply" or "set-protection");
    }

    [Fact]
    public async Task A_retire_plan_removes_only_the_guests_own_vm()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Policy("policy: { destructive: automatic }"));
        var agent = await server.ConnectAgentAsync("home");
        await EstablishAsync(server, agent);
        await RetireAsync(server);

        var plan = MachinePlan(Change(GuestVmPlan.VmResourceType, ["delete"], Vmid, "korriban"), Change(GuestVmPlan.VmResourceType, ["delete"], 100, "korriban", "c-fedora-01"));
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, plan, artifacts: FakeAgent.PlanArtifact("destroy"));
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Retire);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("proxmox_virtual_environment_vm.c-fedora-01 deletes VMID 100 on node korriban", run.Detail);
        Assert.DoesNotContain("VMID 310 on", run.Detail);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action is "apply" or "set-protection");
        Assert.Equal(Vmid, (await server.ResourceAsync(Guest)).BoundVmid);
    }

    [Fact]
    public async Task A_retire_plan_that_deletes_the_guests_own_vm_still_goes_to_approval()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Policy("policy: { destructive: manual }"));
        var agent = await server.ConnectAgentAsync("home");
        await EstablishAsync(server, agent);
        await RetireAsync(server);

        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded,
            MachinePlan(Change(GuestVmPlan.VmResourceType, ["delete"], Vmid, "korriban")), artifacts: FakeAgent.PlanArtifact("destroy"));
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Retire);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        await server.Engine.ApproveAsync(run.Id, approval.Id, approval.PlanSha256!, "ops", null, CancellationToken.None);
        Assert.False(JsonNode.Parse((await agent.NextAsync("set-protection")).InputJson)!["protection"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(Vmid, "korriban")]
    [InlineData(100, "korriban")]
    [InlineData(null, null)]
    public async Task A_stage_plan_that_removes_any_machine_is_refused(int? vmid, string? node)
    {
        // Explicitly automatic: nothing a policy says lets a stage remove a machine.
        var test = ServiceDefinitionTests.Repository();
        test.Write(ServiceDefinitionTests.ServiceFile,
            ServiceDefinitionTests.Service.Replace("workflow:", "policy: { approval: automatic, destructive: automatic }\nworkflow:"));
        await using var server = await ServerHarness.StartAsync(postgres, test);
        await server.SetSecretAsync("web/password", "a-web-password");
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        await server.Services.GetRequiredService<Planner>().ScheduleConfigurationsAsync(CancellationToken.None);
        await ConfigureRunTests.PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        await server.Services.GetRequiredService<Planner>().SchedulePipelinesAsync(CancellationToken.None);

        var planned = await agent.NextAsync("preview");
        Assert.Equal("opentofu", planned.Executor);
        await agent.ReportAsync(planned, Outcome.Succeeded, MachinePlan(Change(GuestVmPlan.VmResourceType, Replace, vmid, node)),
            artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        var run = await server.LatestRunAsync(ServiceDefinitionTests.Address, Operations.Pipeline);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("Stage expose would change a Proxmox resource that acts on a machine", run.Detail);
        Assert.Contains("A pipeline stage owns no machine", run.Detail);
        Assert.Equal(StepStatuses.Skipped, run.Steps.Single(s => s.Name == StepNames.Approval).Status);
        Assert.DoesNotContain(run.Steps.SelectMany(s => s.Attempts), a => a.Mutating);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action is "apply" or "run");
    }

    [Fact]
    public void Removals_are_refused_unless_they_are_provably_the_guests_own()
    {
        var guest = new RunContext
        {
            Address = Guest,
            Proxmox = new ProxmoxContext("https://10.1.0.10:8006/", false, null, "korriban", Vmid, "krb-app-01", null),
        };
        var stage = guest with { Stage = "expose" };

        Assert.Null(GuestVmPlan.ForeignChange(MachinePlan(Change(GuestVmPlan.VmResourceType, Replace, Vmid, "korriban")), guest));
        Assert.Null(GuestVmPlan.ForeignChange(MachinePlan(Change(GuestVmPlan.VmResourceType, ["delete"], Vmid, "korriban")), guest));
        Assert.Null(GuestVmPlan.ForeignChange(MachinePlan(Change(GuestVmPlan.VmResourceType, ["update"], Vmid, "korriban")), guest));
        // What is not a Proxmox resource is not this rule's.
        Assert.Null(GuestVmPlan.ForeignChange(FakeAgent.Plan(delete: 1, replace: 1), stage));
        Assert.Null(GuestVmPlan.ForeignChange(FakeAgent.Plan(), guest));
        Assert.Null(GuestVmPlan.ForeignChange(null, guest));

        Assert.Contains("VMID 100 on node korriban", GuestVmPlan.ForeignChange(MachinePlan(Change(GuestVmPlan.VmResourceType, Replace, 100, "korriban")), guest));
        Assert.Contains("updates VMID 100 on node korriban", GuestVmPlan.ForeignChange(MachinePlan(Change(GuestVmPlan.VmResourceType, ["update"], 100, "korriban")), guest));
        Assert.Contains("creates an unknown VMID on an unknown node", GuestVmPlan.ForeignChange(MachinePlan(Change(GuestVmPlan.VmResourceType, ["create"], null, null)), stage));
        Assert.Contains("VMID 310 on node pve01", GuestVmPlan.ForeignChange(MachinePlan(Change(GuestVmPlan.ContainerResourceType, ["delete"], Vmid, "pve01")), guest));
        Assert.Contains("an unknown VMID on node korriban", GuestVmPlan.ForeignChange(MachinePlan(Change(GuestVmPlan.VmResourceType, ["delete"], null, "korriban")), guest));
        Assert.Contains("Stage expose", GuestVmPlan.ForeignChange(MachinePlan(Change(GuestVmPlan.VmResourceType, ["delete"], Vmid, "korriban")), stage));
        // A plan that removes something without listing it cannot be shown to be harmless.
        var unlisted = FakeAgent.Plan(replace: 1);
        unlisted.Remove("changes");
        Assert.Contains("without listing them", GuestVmPlan.ForeignChange(unlisted, guest));
    }

    [Fact]
    public void Any_proxmox_resource_has_to_name_the_runs_own_machine()
    {
        var guest = new RunContext
        {
            Address = Guest,
            Proxmox = new ProxmoxContext("https://10.1.0.10:8006/", false, null, "korriban", Vmid, "krb-app-01", null),
        };
        var stage = guest with { Stage = "expose" };
        const string Firewall = "proxmox_virtual_environment_firewall_options";

        // The guest's own VM, named through targets as the agent reports them.
        Assert.Null(GuestVmPlan.ForeignChange(MachinePlan(Targeted(Firewall, ["create"], (Vmid, "korriban"))), guest));
        Assert.Null(GuestVmPlan.ForeignChange(MachinePlan(Targeted(GuestVmPlan.VmResourceType, ["update"], (Vmid, "korriban"))), guest));
        // Files on a datastore never act on a VM, for a guest or a stage.
        foreach (var free in GuestVmPlan.MachineFreeTypes)
        {
            Assert.Null(GuestVmPlan.ForeignChange(MachinePlan(Targeted(free, ["create"], (null, "korriban"))), guest));
            Assert.Null(GuestVmPlan.ForeignChange(MachinePlan(Targeted(free, Replace, (null, "pve01"))), stage));
        }

        // Firewall options on VM 100: a create, not a removal, is refused all the same.
        Assert.Contains("creates VMID 100 on node korriban", GuestVmPlan.ForeignChange(MachinePlan(Targeted(Firewall, ["create"], (100, "korriban"))), guest));
        // A change that moves from the guest's VM to another names both.
        Assert.Contains("VMID 311 on node korriban",
            GuestVmPlan.ForeignChange(MachinePlan(Targeted(GuestVmPlan.VmResourceType, ["update"], (Vmid, "korriban"), (311, "korriban"))), guest));
        // Its own VMID without a node, a node without a VM, and nothing at all, cannot be shown to be its own.
        Assert.Contains("VMID 310 on an unknown node", GuestVmPlan.ForeignChange(MachinePlan(Targeted(Firewall, ["create"], (Vmid, null))), guest));
        Assert.Contains("an unknown VMID on node korriban",
            GuestVmPlan.ForeignChange(MachinePlan(Targeted("proxmox_virtual_environment_network_linux_bridge", ["create"], (null, "korriban"))), guest));
        Assert.NotNull(GuestVmPlan.ForeignChange(MachinePlan(Targeted("proxmox_virtual_environment_acme_account", ["update"])), guest));
        // Not known until apply: it might be anything.
        var unknown = Targeted(Firewall, ["create"], (Vmid, "korriban"));
        unknown["targetUnknown"] = true;
        Assert.Contains("not known until apply", GuestVmPlan.ForeignChange(MachinePlan(unknown), guest));
        // A newer provider's name for a VM resource, flagged by the agent, counts as Proxmox.
        var renamed = Targeted("pve_vm", ["update"], (100, "korriban"));
        renamed["proxmox"] = true;
        Assert.NotNull(GuestVmPlan.ForeignChange(MachinePlan(renamed), guest));
        // A stage may touch no machine at all, not even the guest's.
        Assert.Contains("Stage expose", GuestVmPlan.ForeignChange(MachinePlan(Targeted(Firewall, ["update"], (Vmid, "korriban"))), stage));
    }

    [Fact]
    public void A_plan_that_imports_anything_is_refused()
    {
        var guest = new RunContext
        {
            Address = Guest,
            Proxmox = new ProxmoxContext("https://10.1.0.10:8006/", false, null, "korriban", Vmid, "krb-app-01", null),
        };
        // Adopting VM 100 and updating it in place, or adopting it and changing nothing yet.
        foreach (var actions in new[] { ["update"], new[] { "no-op" } })
        {
            var adopt = Targeted(GuestVmPlan.VmResourceType, actions, (100, "korriban"));
            adopt["importing"] = true;
            Assert.Contains("imports an existing resource (proxmox_virtual_environment_vm.guest)", GuestVmPlan.ForeignChange(MachinePlan(adopt), guest));
        }

        // Whatever it imports: an import of the guest's own VMID is refused too.
        var own = Targeted(GuestVmPlan.VmResourceType, ["no-op"], (Vmid, "korriban"));
        own["importing"] = true;
        Assert.NotNull(GuestVmPlan.ForeignChange(MachinePlan(own), guest));
        var other = new JsonObject { ["address"] = "terraform_data.x", ["type"] = "terraform_data", ["actions"] = new JsonArray("no-op"), ["importing"] = true };
        Assert.NotNull(GuestVmPlan.ForeignChange(MachinePlan(other), guest with { Stage = "expose" }));
    }

    [Fact]
    public async Task A_provision_plan_that_sets_firewall_options_on_another_vm_is_refused_before_approval()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Policy("policy: { approval: automatic, destructive: automatic }"));
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();

        var plan = MachinePlan(Targeted(GuestVmPlan.VmResourceType, ["create"], (Vmid, "korriban")),
            Targeted("proxmox_virtual_environment_firewall_options", ["create"], (100, "korriban")));
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, plan, artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("proxmox_virtual_environment_firewall_options.guest creates VMID 100 on node korriban", run.Detail);
        Assert.Equal(StepStatuses.Skipped, run.Steps.Single(s => s.Name == StepNames.Approval).Status);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action is "apply" or "set-protection");
    }

    [Fact]
    public async Task A_provision_plan_that_imports_a_vm_is_refused_before_approval()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Policy("policy: { approval: automatic, destructive: automatic }"));
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();

        var adopt = Targeted(GuestVmPlan.VmResourceType, ["update"], (100, "korriban"));
        adopt["importing"] = true;
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, MachinePlan(adopt), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("Octopushy refuses imports", run.Detail);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action is "apply" or "set-protection");
    }

    /// <summary>A change of a Proxmox resource as the agent reports it, with the VMs and nodes it names.</summary>
    private static JsonObject Targeted(string type, string[] actions, params (int? Vmid, string? Node)[] targets)
    {
        var change = new JsonObject
        {
            ["address"] = $"{type}.guest",
            ["type"] = type,
            ["name"] = "guest",
            ["actions"] = new JsonArray(actions.Select(a => (JsonNode)a).ToArray()),
            ["importing"] = false,
            ["proxmox"] = true,
            ["targets"] = new JsonArray(targets.Select(t =>
            {
                var target = new JsonObject();
                if (t.Vmid is not null) target["vmId"] = t.Vmid;
                if (t.Node is not null) target["node"] = t.Node;
                return (JsonNode)target;
            }).ToArray()),
        };
        return change;
    }
}
