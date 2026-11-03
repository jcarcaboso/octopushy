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
        var test = PipelineDefinitionTests.Repository();
        test.Write(PipelineDefinitionTests.PipelineFile,
            PipelineDefinitionTests.Pipeline.Replace("stages:", "policy: { approval: automatic, destructive: automatic }\nstages:"));
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

        var run = await server.LatestRunAsync(PipelineDefinitionTests.Address, Operations.Pipeline);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("Stage expose would delete or replace a Proxmox machine", run.Detail);
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

        Assert.Null(GuestVmPlan.ForeignRemoval(MachinePlan(Change(GuestVmPlan.VmResourceType, Replace, Vmid, "korriban")), guest));
        Assert.Null(GuestVmPlan.ForeignRemoval(MachinePlan(Change(GuestVmPlan.VmResourceType, ["delete"], Vmid, "korriban")), guest));
        // What is not a removal, or not a machine, is not this rule's.
        Assert.Null(GuestVmPlan.ForeignRemoval(MachinePlan(Change(GuestVmPlan.VmResourceType, ["update"], 100, "korriban")), guest));
        Assert.Null(GuestVmPlan.ForeignRemoval(MachinePlan(Change(GuestVmPlan.VmResourceType, ["create"], null, null)), stage));
        Assert.Null(GuestVmPlan.ForeignRemoval(FakeAgent.Plan(delete: 1, replace: 1), stage));
        Assert.Null(GuestVmPlan.ForeignRemoval(FakeAgent.Plan(), guest));
        Assert.Null(GuestVmPlan.ForeignRemoval(null, guest));

        Assert.Contains("VMID 100 on node korriban", GuestVmPlan.ForeignRemoval(MachinePlan(Change(GuestVmPlan.VmResourceType, Replace, 100, "korriban")), guest));
        Assert.Contains("VMID 310 on node pve01", GuestVmPlan.ForeignRemoval(MachinePlan(Change(GuestVmPlan.ContainerResourceType, ["delete"], Vmid, "pve01")), guest));
        Assert.Contains("an unknown VMID on node korriban", GuestVmPlan.ForeignRemoval(MachinePlan(Change(GuestVmPlan.VmResourceType, ["delete"], null, "korriban")), guest));
        Assert.Contains("Stage expose", GuestVmPlan.ForeignRemoval(MachinePlan(Change(GuestVmPlan.VmResourceType, ["delete"], Vmid, "korriban")), stage));
        // A plan that removes something without listing it cannot be shown to be harmless.
        var unlisted = FakeAgent.Plan(replace: 1);
        unlisted.Remove("changes");
        Assert.Contains("without listing them", GuestVmPlan.ForeignRemoval(unlisted, guest));
    }
}
