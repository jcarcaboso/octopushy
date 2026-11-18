using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using static Octopushy.Tests.Server.SafetyBoundaryFixtures;

namespace Octopushy.Tests.Server;

/// <summary>
/// A guest's <see cref="StepNames.Plan"/>, of a provision and of a retire: the check it records, the steps a plan without changes
/// skips, and whether unprotect stays to run, only when the plan removes the guest's machine or changes its container in place and
/// the guest owns that machine. The plan kept on the step and the approval of it are pinned too, and so are the refusal of a plan
/// that acts on another machine, which comes first, a failure, a late result and a second delivery of every result. A run whose
/// operation the code does not know, as a row written by another version would hold, is pinned too. Each case is one of
/// <see cref="ProvisioningTests"/>, <see cref="VmidCollisionTests"/> or <see cref="ContainerGuestTests"/>.
/// </summary>
public class PlanEffectCharacterizationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string UnknownOperation = "no-such-operation";

    private static JsonObject Machine(string[] actions, int vmid = Vmid, string type = GuestVmPlan.VmResourceType) =>
        MachinePlan(Change(type, actions, vmid, "korriban"));

    /// <summary>The minimal lab with its guest a container.</summary>
    private static TestRepository Container()
    {
        var repository = TestRepository.Minimal();
        repository.Write(GuestFile, File.ReadAllText(Path.Combine(repository.Root, GuestFile)).Replace("type: vm", "type: container"));
        repository.Write("workflows/test-vm/workflow.yaml",
            File.ReadAllText(Path.Combine(repository.Root, "workflows/test-vm/workflow.yaml")).Replace("manages: vm", "manages: container"));
        return repository;
    }

    /// <summary>A bound guest planned again; returns its plan.</summary>
    private static async Task<Assignment> PlanAgainAsync(ServerHarness server, FakeAgent agent)
    {
        await EstablishAsync(server, agent);
        await server.Services.GetRequiredService<Planner>().PlanGuestAsync((await server.ResourceAsync(Guest)).Id, CancellationToken.None);
        return await agent.NextAsync("preview");
    }

    /// <summary>A bound guest retired; returns its retire run's plan.</summary>
    private static async Task<Assignment> RetirePlanAsync(ServerHarness server, FakeAgent agent)
    {
        await EstablishAsync(server, agent);
        await RetireAsync(server);
        return await agent.NextAsync("preview");
    }

    /// <summary>Gives the run of <paramref name="assignment"/> an operation the code does not know.</summary>
    private static Task RenameOperationAsync(ServerHarness server, Assignment assignment) =>
        server.WithDbAsync(db => db.Runs.Where(r => db.Attempts.Any(a => a.Id == Guid.Parse(assignment.AttemptId) && a.RunId == r.Id))
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Operation, UnknownOperation)));

    /// <summary>Records an observation of korriban, at <paramref name="at"/>, that shows a VM at the guest's VMID.</summary>
    private static Task ObserveAsync(ServerHarness server, DateTimeOffset at) =>
        server.WithDbAsync(async db =>
        {
            var installation = await db.Resources.FirstAsync(r => r.Address == Installation);
            db.Observations.Add(new Observation
            {
                Id = Guid.CreateVersion7(),
                ResourceId = installation.Id,
                ObservedAt = at,
                Data = new JsonObject
                {
                    ["cluster"] = null,
                    ["vmVisibility"] = new JsonObject { ["complete"] = true },
                    ["hosts"] = new JsonArray(new JsonObject { ["name"] = "korriban", ["online"] = true }),
                    ["guests"] = new JsonArray(new JsonObject { ["vmid"] = Vmid, ["name"] = "c-fedora-01", ["host"] = "korriban", ["status"] = "running", ["kind"] = "qemu" }),
                }.ToJsonString(),
            });
            await db.SaveChangesAsync();
        });

    [Fact]
    public async Task A_provision_plan_that_creates_the_machine_lifts_no_protection_and_a_second_delivery_changes_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var plan = await agent.NextAsync("preview");
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: running | finished False | outputs False | attempts assigned | -
            step verify: pending | finished False | outputs False | attempts  | -
            configuration: none
            resource: checked True | applied False | verified False | bound - | outputs False
            rows: artifacts +1, attempts +1, secret_resolutions +1, tofu_states +1
            operation: provision
            guest: checked this run's this run's | applied - - | synced - | outputs -
            guest: bound - - at False | reserved home/korriban korriban 310 at True applied False
            guest: retired False | attached False by - | detached False | waiting -
            plan: {"counts": {"create": 1, "delete": 0, "update": 0, "replace": 0}, "changes": [{"name": "guest", "node": "korriban", "type": "proxmox_virtual_environment_vm", "vmId": 310, "actions": ["create"], "address": "proxmox_virtual_environment_vm.guest", "importing": false}], "hasChanges": true, "planSha256": "524f4859b1f8693c7359567af064d22b4980e3e3c1b5b9c8501968ed1c5149e9", "toolVersion": "1.13.1"}
            approval: required False | decision approved by policy at True | hash the plan's digest
            """, await EffectPins.DeliverPlanAsync(server, agent, plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), null, FakeAgent.PlanArtifact()));
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: running | finished False | outputs False | attempts assigned | -
            step verify: pending | finished False | outputs False | attempts  | -
            configuration: none
            resource: checked True | applied False | verified False | bound - | outputs False
            rows: none
            operation: provision
            guest: checked this run's this run's | applied - - | synced - | outputs -
            guest: bound - - at False | reserved home/korriban korriban 310 at True applied False
            guest: retired False | attached False by - | detached False | waiting -
            plan: {"counts": {"create": 1, "delete": 0, "update": 0, "replace": 0}, "changes": [{"name": "guest", "node": "korriban", "type": "proxmox_virtual_environment_vm", "vmId": 310, "actions": ["create"], "address": "proxmox_virtual_environment_vm.guest", "importing": false}], "hasChanges": true, "planSha256": "524f4859b1f8693c7359567af064d22b4980e3e3c1b5b9c8501968ed1c5149e9", "toolVersion": "1.13.1"}
            approval: required False | decision approved by policy at True | hash the plan's digest
            """, await EffectPins.DeliverPlanAsync(server, agent, plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), null, FakeAgent.PlanArtifact()));
    }

    [Theory]
    [InlineData(Operations.Provision)]
    [InlineData(Operations.Retire)]
    public async Task A_plan_without_changes_skips_what_would_change_and_only_a_provision_marks_the_guest_synced(string operation)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        // A new guest's provision, so that what it marks shows.
        if (operation == Operations.Provision)
            await server.SyncAndTrustAsync();
        var plan = operation == Operations.Provision ? await agent.NextAsync("preview") : await RetirePlanAsync(server, agent);
        EffectPins.Check(operation == Operations.Provision
            ? """
                taken: True
                run: running | finished False | -
                step plan: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: skipped | finished False | outputs False | attempts  | The plan has no changes.
                step unprotect: skipped | finished False | outputs False | attempts  | The plan has no changes.
                step apply: skipped | finished False | outputs False | attempts  | The plan has no changes.
                step verify: running | finished False | outputs False | attempts assigned | -
                configuration: none
                resource: checked True | applied False | verified False | bound - | outputs False
                rows: attempts +1, secret_resolutions +1
                operation: provision
                guest: checked this run's this run's | applied - - | synced this run's | outputs -
                guest: bound - - at False | reserved - - - at False applied False
                guest: retired False | attached False by - | detached False | waiting -
                plan: {"counts": {"create": 0, "delete": 0, "update": 0, "replace": 0}, "changes": [], "hasChanges": false, "toolVersion": "1.13.1"}
                approval: required False | decision - by - at False | hash -
                """
            : """
                taken: True
                run: running | finished False | -
                step plan: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: skipped | finished False | outputs False | attempts  | The plan has no changes.
                step unprotect: skipped | finished False | outputs False | attempts  | The plan has no changes.
                step apply: skipped | finished False | outputs False | attempts  | The plan has no changes.
                step verify-absent: running | finished False | outputs False | attempts assigned | -
                configuration: none
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: attempts +1, secret_resolutions +1
                operation: retire
                guest: checked this run's this run's | applied another another | synced another | outputs {}
                guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
                guest: retired False | attached False by - | detached False | waiting -
                plan: {"counts": {"create": 0, "delete": 0, "update": 0, "replace": 0}, "changes": [], "hasChanges": false, "toolVersion": "1.13.1"}
                approval: required False | decision - by - at False | hash -
                """, await EffectPins.DeliverPlanAsync(server, agent, plan, Outcome.Succeeded, FakeAgent.Plan()));
        EffectPins.Check(operation == Operations.Provision
            ? """
                taken: True
                run: running | finished False | -
                step plan: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: skipped | finished False | outputs False | attempts  | The plan has no changes.
                step unprotect: skipped | finished False | outputs False | attempts  | The plan has no changes.
                step apply: skipped | finished False | outputs False | attempts  | The plan has no changes.
                step verify: running | finished False | outputs False | attempts assigned | -
                configuration: none
                resource: checked True | applied False | verified False | bound - | outputs False
                rows: none
                operation: provision
                guest: checked this run's this run's | applied - - | synced this run's | outputs -
                guest: bound - - at False | reserved - - - at False applied False
                guest: retired False | attached False by - | detached False | waiting -
                plan: {"counts": {"create": 0, "delete": 0, "update": 0, "replace": 0}, "changes": [], "hasChanges": false, "toolVersion": "1.13.1"}
                approval: required False | decision - by - at False | hash -
                """
            : """
                taken: True
                run: running | finished False | -
                step plan: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: skipped | finished False | outputs False | attempts  | The plan has no changes.
                step unprotect: skipped | finished False | outputs False | attempts  | The plan has no changes.
                step apply: skipped | finished False | outputs False | attempts  | The plan has no changes.
                step verify-absent: running | finished False | outputs False | attempts assigned | -
                configuration: none
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: none
                operation: retire
                guest: checked this run's this run's | applied another another | synced another | outputs {}
                guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
                guest: retired False | attached False by - | detached False | waiting -
                plan: {"counts": {"create": 0, "delete": 0, "update": 0, "replace": 0}, "changes": [], "hasChanges": false, "toolVersion": "1.13.1"}
                approval: required False | decision - by - at False | hash -
                """, await EffectPins.DeliverPlanAsync(server, agent, plan, Outcome.Succeeded, FakeAgent.Plan()));
    }

    [Fact]
    public async Task A_provision_plan_that_replaces_the_bound_machine_keeps_unprotect_and_waits_for_a_person()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var plan = await PlanAgainAsync(server, agent);
        EffectPins.Check("""
            taken: True
            run: awaiting-approval | finished False | The plan destroys a VM and its disks. That waits for approval unless destructive is set to automatic explicitly (it is automatic, set by default, following approval).
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: awaiting-approval | finished False | outputs False | attempts  | The plan destroys a VM and its disks. That waits for approval unless destructive is set to automatic explicitly (it is automatic, set by default, following approval).
            step unprotect: pending | finished False | outputs False | attempts  | -
            step apply: pending | finished False | outputs False | attempts  | -
            step verify: pending | finished False | outputs False | attempts  | -
            configuration: none
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: artifacts +1, notifications +1
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            plan: {"counts": {"create": 0, "delete": 0, "update": 0, "replace": 1}, "changes": [{"name": "guest", "node": "korriban", "type": "proxmox_virtual_environment_vm", "vmId": 310, "actions": ["delete", "create"], "address": "proxmox_virtual_environment_vm.guest"}], "hasChanges": true, "planSha256": "7ab3778776cde4fa728a162a53ad3abcc967d1bd7361039e0942888240c4ce86", "toolVersion": "1.13.1"}
            approval: required True | decision - by - at False | hash the plan's digest
            """, await EffectPins.DeliverPlanAsync(server, agent, plan, Outcome.Succeeded, Machine(Replace), null, FakeAgent.PlanArtifact("replace")));
        EffectPins.Check("""
            taken: True
            run: awaiting-approval | finished False | The plan destroys a VM and its disks. That waits for approval unless destructive is set to automatic explicitly (it is automatic, set by default, following approval).
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: awaiting-approval | finished False | outputs False | attempts  | The plan destroys a VM and its disks. That waits for approval unless destructive is set to automatic explicitly (it is automatic, set by default, following approval).
            step unprotect: pending | finished False | outputs False | attempts  | -
            step apply: pending | finished False | outputs False | attempts  | -
            step verify: pending | finished False | outputs False | attempts  | -
            configuration: none
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            plan: {"counts": {"create": 0, "delete": 0, "update": 0, "replace": 1}, "changes": [{"name": "guest", "node": "korriban", "type": "proxmox_virtual_environment_vm", "vmId": 310, "actions": ["delete", "create"], "address": "proxmox_virtual_environment_vm.guest"}], "hasChanges": true, "planSha256": "7ab3778776cde4fa728a162a53ad3abcc967d1bd7361039e0942888240c4ce86", "toolVersion": "1.13.1"}
            approval: required True | decision - by - at False | hash the plan's digest
            """, await EffectPins.DeliverPlanAsync(server, agent, plan, Outcome.Succeeded, Machine(Replace), null, FakeAgent.PlanArtifact("replace")));
    }

    [Fact]
    public async Task A_provision_plan_that_updates_the_bound_vm_in_place_keeps_its_protection()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var plan = await PlanAgainAsync(server, agent);
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: running | finished False | outputs False | attempts assigned | -
            step verify: pending | finished False | outputs False | attempts  | -
            configuration: none
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: artifacts +1, attempts +1, secret_resolutions +1
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            plan: {"counts": {"create": 0, "delete": 0, "update": 1, "replace": 0}, "changes": [{"name": "guest", "node": "korriban", "type": "proxmox_virtual_environment_vm", "vmId": 310, "actions": ["update"], "address": "proxmox_virtual_environment_vm.guest"}], "hasChanges": true, "planSha256": "2937013f2181810606b2a799b05bda2849f3e369a20982a4138f0e0a55984ce4", "toolVersion": "1.13.1"}
            approval: required False | decision approved by policy at True | hash the plan's digest
            """, await EffectPins.DeliverPlanAsync(server, agent, plan, Outcome.Succeeded, Machine(["update"]), null, FakeAgent.PlanArtifact("update")));
    }

    [Fact]
    public async Task A_provision_plan_that_replaces_a_machine_the_guest_does_not_own_keeps_its_protection()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var plan = await agent.NextAsync("preview");
        EffectPins.Check("""
            taken: True
            run: awaiting-approval | finished False | The plan destroys a VM and its disks. That waits for approval unless destructive is set to automatic explicitly (it is automatic, set by default, following approval).
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: awaiting-approval | finished False | outputs False | attempts  | The plan destroys a VM and its disks. That waits for approval unless destructive is set to automatic explicitly (it is automatic, set by default, following approval).
            step unprotect: skipped | finished False | outputs False | attempts  | This guest does not own VM 310 on korriban, so its protection stays as it is.
            step apply: pending | finished False | outputs False | attempts  | -
            step verify: pending | finished False | outputs False | attempts  | -
            configuration: none
            resource: checked True | applied False | verified False | bound - | outputs False
            rows: artifacts +1, notifications +1
            operation: provision
            guest: checked this run's this run's | applied - - | synced - | outputs -
            guest: bound - - at False | reserved - - - at False applied False
            guest: retired False | attached False by - | detached False | waiting -
            plan: {"counts": {"create": 0, "delete": 0, "update": 0, "replace": 1}, "changes": [{"name": "guest", "node": "korriban", "type": "proxmox_virtual_environment_vm", "vmId": 310, "actions": ["delete", "create"], "address": "proxmox_virtual_environment_vm.guest"}], "hasChanges": true, "planSha256": "7ab3778776cde4fa728a162a53ad3abcc967d1bd7361039e0942888240c4ce86", "toolVersion": "1.13.1"}
            approval: required True | decision - by - at False | hash the plan's digest
            """, await EffectPins.DeliverPlanAsync(server, agent, plan, Outcome.Succeeded, Machine(Replace), null, FakeAgent.PlanArtifact("replace")));
    }

    /// <summary>
    /// A create that did not finish leaves a reservation. While it is recoverable the half-made machine is the guest's, and its
    /// replacement keeps unprotect; once an observation from before the reservation shows a machine there, it is not.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_provision_plan_that_replaces_a_reserved_machine_keeps_unprotect_only_while_the_reservation_is_recoverable(bool recoverable)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Failed, error: "boom");
        await server.TickAsync();
        var guest = await server.ResourceAsync(Guest);
        Assert.Equal((Vmid, (int?)null), (guest.ReservedVmid, guest.BoundVmid));
        await server.Services.GetRequiredService<Planner>().PlanGuestAsync(guest.Id, CancellationToken.None);
        var plan = await agent.NextAsync("preview");
        if (!recoverable)
            await ObserveAsync(server, guest.ReservedAt!.Value.AddMinutes(-1));
        EffectPins.Check(recoverable
            ? """
                taken: True
                run: awaiting-approval | finished False | The plan destroys a VM and its disks. That waits for approval unless destructive is set to automatic explicitly (it is automatic, set by default, following approval).
                step plan: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: awaiting-approval | finished False | outputs False | attempts  | The plan destroys a VM and its disks. That waits for approval unless destructive is set to automatic explicitly (it is automatic, set by default, following approval).
                step unprotect: pending | finished False | outputs False | attempts  | -
                step apply: pending | finished False | outputs False | attempts  | -
                step verify: pending | finished False | outputs False | attempts  | -
                configuration: none
                resource: checked True | applied False | verified False | bound - | outputs False
                rows: artifacts +1, notifications +1
                operation: provision
                guest: checked this run's this run's | applied - - | synced - | outputs -
                guest: bound - - at False | reserved home/korriban korriban 310 at True applied False
                guest: retired False | attached False by - | detached False | waiting -
                plan: {"counts": {"create": 0, "delete": 0, "update": 0, "replace": 1}, "changes": [{"name": "guest", "node": "korriban", "type": "proxmox_virtual_environment_vm", "vmId": 310, "actions": ["delete", "create"], "address": "proxmox_virtual_environment_vm.guest"}], "hasChanges": true, "planSha256": "7ab3778776cde4fa728a162a53ad3abcc967d1bd7361039e0942888240c4ce86", "toolVersion": "1.13.1"}
                approval: required True | decision - by - at False | hash the plan's digest
                """
            : """
                taken: True
                run: awaiting-approval | finished False | The plan destroys a VM and its disks. That waits for approval unless destructive is set to automatic explicitly (it is automatic, set by default, following approval).
                step plan: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: awaiting-approval | finished False | outputs False | attempts  | The plan destroys a VM and its disks. That waits for approval unless destructive is set to automatic explicitly (it is automatic, set by default, following approval).
                step unprotect: skipped | finished False | outputs False | attempts  | This guest does not own VM 310 on korriban, so its protection stays as it is.
                step apply: pending | finished False | outputs False | attempts  | -
                step verify: pending | finished False | outputs False | attempts  | -
                configuration: none
                resource: checked True | applied False | verified False | bound - | outputs False
                rows: artifacts +1, notifications +1
                operation: provision
                guest: checked this run's this run's | applied - - | synced - | outputs -
                guest: bound - - at False | reserved home/korriban korriban 310 at True applied False
                guest: retired False | attached False by - | detached False | waiting -
                plan: {"counts": {"create": 0, "delete": 0, "update": 0, "replace": 1}, "changes": [{"name": "guest", "node": "korriban", "type": "proxmox_virtual_environment_vm", "vmId": 310, "actions": ["delete", "create"], "address": "proxmox_virtual_environment_vm.guest"}], "hasChanges": true, "planSha256": "7ab3778776cde4fa728a162a53ad3abcc967d1bd7361039e0942888240c4ce86", "toolVersion": "1.13.1"}
                approval: required True | decision - by - at False | hash the plan's digest
                """, await EffectPins.DeliverPlanAsync(server, agent, plan, Outcome.Succeeded, Machine(Replace), null, FakeAgent.PlanArtifact("replace")));
    }

    /// <summary>A container's update in place lifts its protection, since Proxmox refuses any change to a protected one's disks.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_provision_plan_of_a_container_keeps_unprotect_when_it_changes_the_container_in_place(bool inPlace)
    {
        await using var server = await ServerHarness.StartAsync(postgres, Container());
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, Machine(["create"], type: GuestVmPlan.ContainerResourceType),
            artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        await server.TickAsync();
        Assert.Equal(Vmid, (await server.ResourceAsync(Guest)).BoundVmid);
        await server.Services.GetRequiredService<Planner>().PlanGuestAsync((await server.ResourceAsync(Guest)).Id, CancellationToken.None);
        var plan = await agent.NextAsync("preview");
        var outputs = inPlace
            ? Machine(["update"], type: GuestVmPlan.ContainerResourceType)
            : MachinePlan(Change("proxmox_virtual_environment_file", ["update"], null, "korriban", "snippet"));
        EffectPins.Check(inPlace
            ? """
                taken: True
                run: running | finished False | -
                step plan: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step unprotect: running | finished False | outputs False | attempts assigned | -
                step apply: pending | finished False | outputs False | attempts  | -
                step verify: pending | finished False | outputs False | attempts  | -
                configuration: none
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: artifacts +1, attempts +1, secret_resolutions +1
                operation: provision
                guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
                guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
                guest: retired False | attached False by - | detached False | waiting -
                plan: {"counts": {"create": 0, "delete": 0, "update": 1, "replace": 0}, "changes": [{"name": "guest", "node": "korriban", "type": "proxmox_virtual_environment_container", "vmId": 310, "actions": ["update"], "address": "proxmox_virtual_environment_container.guest"}], "hasChanges": true, "planSha256": "2937013f2181810606b2a799b05bda2849f3e369a20982a4138f0e0a55984ce4", "toolVersion": "1.13.1"}
                approval: required False | decision approved by policy at True | hash the plan's digest
                """
            : """
                taken: True
                run: running | finished False | -
                step plan: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's container, or change it in place, so its protection stays as it is.
                step apply: running | finished False | outputs False | attempts assigned | -
                step verify: pending | finished False | outputs False | attempts  | -
                configuration: none
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: artifacts +1, attempts +1, secret_resolutions +1
                operation: provision
                guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
                guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
                guest: retired False | attached False by - | detached False | waiting -
                plan: {"counts": {"create": 0, "delete": 0, "update": 1, "replace": 0}, "changes": [{"name": "snippet", "node": "korriban", "type": "proxmox_virtual_environment_file", "actions": ["update"], "address": "proxmox_virtual_environment_file.snippet"}], "hasChanges": true, "planSha256": "2937013f2181810606b2a799b05bda2849f3e369a20982a4138f0e0a55984ce4", "toolVersion": "1.13.1"}
                approval: required False | decision approved by policy at True | hash the plan's digest
                """, await EffectPins.DeliverPlanAsync(server, agent, plan, Outcome.Succeeded, outputs, null, FakeAgent.PlanArtifact("update")));
    }

    [Fact]
    public async Task A_plan_that_fails_records_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var plan = await agent.NextAsync("preview");
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The plan step failed: Unsupported argument
            step plan: failed | finished True | outputs False | attempts failed | Unsupported argument
            step approval: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            step unprotect: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            step apply: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            step verify: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            configuration: none
            resource: checked False | applied False | verified False | bound - | outputs False
            rows: notifications +1
            operation: provision
            guest: checked - - | applied - - | synced - | outputs -
            guest: bound - - at False | reserved - - - at False applied False
            guest: retired False | attached False by - | detached False | waiting -
            plan: -
            approval: required False | decision - by - at False | hash -
            """, await EffectPins.DeliverPlanAsync(server, agent, plan, Outcome.Failed, null, "Unsupported argument"));
    }

    /// <summary>Refused before the plan's effect: nothing of the check is recorded, and no step after it is touched.</summary>
    [Fact]
    public async Task A_plan_that_changes_another_machine_is_refused_before_anything_is_recorded()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var plan = await agent.NextAsync("preview");
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The plan step failed: The plan would change a Proxmox resource that is not this guest's VM 310 on korriban (proxmox_virtual_environment_vm.guest deletes VMID 100 on node korriban). Octopushy refuses to act on a machine it does not manage, whatever the policy says.
            step plan: failed | finished True | outputs True | attempts succeeded | The plan would change a Proxmox resource that is not this guest's VM 310 on korriban (proxmox_virtual_environment_vm.guest deletes VMID 100 on node korriban). Octopushy refuses to act on a machine it does not manage, whatever the policy says.
            step approval: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            step unprotect: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            step apply: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            step verify: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            configuration: none
            resource: checked False | applied False | verified False | bound - | outputs False
            rows: artifacts +1, notifications +1
            operation: provision
            guest: checked - - | applied - - | synced - | outputs -
            guest: bound - - at False | reserved - - - at False applied False
            guest: retired False | attached False by - | detached False | waiting -
            plan: {"counts": {"create": 0, "delete": 1, "update": 0, "replace": 0}, "changes": [{"name": "guest", "node": "korriban", "type": "proxmox_virtual_environment_vm", "vmId": 100, "actions": ["delete"], "address": "proxmox_virtual_environment_vm.guest"}], "hasChanges": true, "planSha256": "6f61ec5c7a0ab645b7b956d703d6e91bbd804f2c4dce1cc81058ef9b17e0ccaa", "toolVersion": "1.13.1"}
            approval: required False | decision - by - at False | hash -
            """, await EffectPins.DeliverPlanAsync(server, agent, plan, Outcome.Succeeded, Machine(["delete"], vmid: 100), null, FakeAgent.PlanArtifact("destroy")));
    }

    [Fact]
    public async Task A_plan_reported_uncertain_is_settled_once_by_its_late_result()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var plan = await RetirePlanAsync(server, agent);
        EffectPins.Check("""
            taken: True
            run: uncertain | finished False | lost
            step plan: uncertain | finished True | outputs False | attempts uncertain | lost
            step approval: pending | finished False | outputs False | attempts  | -
            step unprotect: pending | finished False | outputs False | attempts  | -
            step apply: pending | finished False | outputs False | attempts  | -
            step verify-absent: pending | finished False | outputs False | attempts  | -
            configuration: none
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: notifications +1
            operation: retire
            guest: checked another another | applied another another | synced another | outputs {}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            plan: -
            approval: required False | decision - by - at False | hash -
            """, await EffectPins.DeliverPlanAsync(server, agent, plan, Outcome.Uncertain, new JsonObject(), "lost"));
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | The result arrived after the agent had stopped reporting.
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: running | finished False | outputs False | attempts assigned | -
            step apply: pending | finished False | outputs False | attempts  | -
            step verify-absent: pending | finished False | outputs False | attempts  | -
            configuration: none
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: artifacts +1, attempts +1, secret_resolutions +1
            operation: retire
            guest: checked this run's this run's | applied another another | synced another | outputs {}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            plan: {"counts": {"create": 0, "delete": 1, "update": 0, "replace": 0}, "changes": [{"name": "guest", "node": "korriban", "type": "proxmox_virtual_environment_vm", "vmId": 310, "actions": ["delete"], "address": "proxmox_virtual_environment_vm.guest", "importing": false}], "hasChanges": true, "planSha256": "6f61ec5c7a0ab645b7b956d703d6e91bbd804f2c4dce1cc81058ef9b17e0ccaa", "toolVersion": "1.13.1"}
            approval: required False | decision approved by policy at True | hash the plan's digest
            """, await EffectPins.DeliverPlanAsync(server, agent, plan, Outcome.Succeeded, FakeAgent.Plan(delete: 1), null, FakeAgent.PlanArtifact("destroy")));
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | The result arrived after the agent had stopped reporting.
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: running | finished False | outputs False | attempts assigned | -
            step apply: pending | finished False | outputs False | attempts  | -
            step verify-absent: pending | finished False | outputs False | attempts  | -
            configuration: none
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            operation: retire
            guest: checked this run's this run's | applied another another | synced another | outputs {}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            plan: {"counts": {"create": 0, "delete": 1, "update": 0, "replace": 0}, "changes": [{"name": "guest", "node": "korriban", "type": "proxmox_virtual_environment_vm", "vmId": 310, "actions": ["delete"], "address": "proxmox_virtual_environment_vm.guest", "importing": false}], "hasChanges": true, "planSha256": "6f61ec5c7a0ab645b7b956d703d6e91bbd804f2c4dce1cc81058ef9b17e0ccaa", "toolVersion": "1.13.1"}
            approval: required False | decision approved by policy at True | hash the plan's digest
            """, await EffectPins.DeliverPlanAsync(server, agent, plan, Outcome.Succeeded, FakeAgent.Plan(delete: 1), null, FakeAgent.PlanArtifact("destroy")));
    }

    [Fact]
    public async Task A_retire_plan_that_deletes_the_bound_machine_keeps_unprotect_and_a_second_delivery_changes_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var plan = await RetirePlanAsync(server, agent);
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: running | finished False | outputs False | attempts assigned | -
            step apply: pending | finished False | outputs False | attempts  | -
            step verify-absent: pending | finished False | outputs False | attempts  | -
            configuration: none
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: artifacts +1, attempts +1, secret_resolutions +1
            operation: retire
            guest: checked this run's this run's | applied another another | synced another | outputs {}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            plan: {"counts": {"create": 0, "delete": 1, "update": 0, "replace": 0}, "changes": [{"name": "guest", "node": "korriban", "type": "proxmox_virtual_environment_vm", "vmId": 310, "actions": ["delete"], "address": "proxmox_virtual_environment_vm.guest", "importing": false}], "hasChanges": true, "planSha256": "6f61ec5c7a0ab645b7b956d703d6e91bbd804f2c4dce1cc81058ef9b17e0ccaa", "toolVersion": "1.13.1"}
            approval: required False | decision approved by policy at True | hash the plan's digest
            """, await EffectPins.DeliverPlanAsync(server, agent, plan, Outcome.Succeeded, FakeAgent.Plan(delete: 1), null, FakeAgent.PlanArtifact("destroy")));
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: running | finished False | outputs False | attempts assigned | -
            step apply: pending | finished False | outputs False | attempts  | -
            step verify-absent: pending | finished False | outputs False | attempts  | -
            configuration: none
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            operation: retire
            guest: checked this run's this run's | applied another another | synced another | outputs {}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            plan: {"counts": {"create": 0, "delete": 1, "update": 0, "replace": 0}, "changes": [{"name": "guest", "node": "korriban", "type": "proxmox_virtual_environment_vm", "vmId": 310, "actions": ["delete"], "address": "proxmox_virtual_environment_vm.guest", "importing": false}], "hasChanges": true, "planSha256": "6f61ec5c7a0ab645b7b956d703d6e91bbd804f2c4dce1cc81058ef9b17e0ccaa", "toolVersion": "1.13.1"}
            approval: required False | decision approved by policy at True | hash the plan's digest
            """, await EffectPins.DeliverPlanAsync(server, agent, plan, Outcome.Succeeded, FakeAgent.Plan(delete: 1), null, FakeAgent.PlanArtifact("destroy")));
    }

    [Fact]
    public async Task A_retire_plan_that_does_not_delete_the_machine_keeps_its_protection()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var plan = await RetirePlanAsync(server, agent);
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: running | finished False | outputs False | attempts assigned | -
            step verify-absent: pending | finished False | outputs False | attempts  | -
            configuration: none
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: artifacts +1, attempts +1, secret_resolutions +1
            operation: retire
            guest: checked this run's this run's | applied another another | synced another | outputs {}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            plan: {"counts": {"create": 0, "delete": 1, "update": 0, "replace": 0}, "changes": [{"name": "r0", "type": "terraform_data", "actions": ["delete", "create"], "address": "terraform_data.r0", "importing": false}], "hasChanges": true, "planSha256": "6f61ec5c7a0ab645b7b956d703d6e91bbd804f2c4dce1cc81058ef9b17e0ccaa", "toolVersion": "1.13.1"}
            approval: required False | decision approved by policy at True | hash the plan's digest
            """, await EffectPins.DeliverPlanAsync(server, agent, plan, Outcome.Succeeded, FakeAgent.Plan(delete: 1, machine: false), null, FakeAgent.PlanArtifact("destroy")));
    }

    /// <summary>The reported case: an apply that made no machine leaves the guest unbound, and then it is retired.</summary>
    [Fact]
    public async Task A_retire_plan_of_a_guest_that_owns_nothing_keeps_its_protection()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1, machine: false), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Failed, error: "boom");
        await server.TickAsync();
        await RetireAsync(server);
        var plan = await agent.NextAsync("preview");
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | This guest does not own VM 310 on korriban, so its protection stays as it is.
            step apply: running | finished False | outputs False | attempts assigned | -
            step verify-absent: pending | finished False | outputs False | attempts  | -
            configuration: none
            resource: checked True | applied False | verified False | bound - | outputs False
            rows: artifacts +1, attempts +1, secret_resolutions +1
            operation: retire
            guest: checked this run's this run's | applied - - | synced - | outputs -
            guest: bound - - at False | reserved - - - at False applied False
            guest: retired False | attached False by - | detached False | waiting -
            plan: {"counts": {"create": 0, "delete": 1, "update": 0, "replace": 0}, "changes": [{"name": "guest", "node": "korriban", "type": "proxmox_virtual_environment_vm", "vmId": 310, "actions": ["delete"], "address": "proxmox_virtual_environment_vm.guest"}], "hasChanges": true, "planSha256": "6f61ec5c7a0ab645b7b956d703d6e91bbd804f2c4dce1cc81058ef9b17e0ccaa", "toolVersion": "1.13.1"}
            approval: required False | decision approved by policy at True | hash the plan's digest
            """, await EffectPins.DeliverPlanAsync(server, agent, plan, Outcome.Succeeded, Machine(["delete"]), null, FakeAgent.PlanArtifact("destroy")));
    }

    /// <summary>
    /// Neither a provision nor a retire: the check is recorded and a plan without changes skips, and unprotect is left as it is,
    /// whether the plan removes the guest's machine or not.
    /// </summary>
    [Theory]
    [InlineData("removes")]
    [InlineData("keeps")]
    [InlineData("none")]
    public async Task A_plan_of_an_operation_the_code_does_not_know_records_the_check_and_leaves_unprotect(string changes)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var plan = await RetirePlanAsync(server, agent);
        await RenameOperationAsync(server, plan);
        var outputs = changes switch
        {
            "removes" => Machine(["delete"]),
            "keeps" => FakeAgent.Plan(delete: 1, machine: false),
            _ => FakeAgent.Plan(),
        };
        EffectPins.Check(changes switch
        {
            "removes" => """
                taken: True
                run: running | finished False | -
                step plan: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step unprotect: running | finished False | outputs False | attempts assigned | -
                step apply: pending | finished False | outputs False | attempts  | -
                step verify-absent: pending | finished False | outputs False | attempts  | -
                configuration: none
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: artifacts +1, attempts +1, secret_resolutions +1
                operation: no-such-operation
                guest: checked this run's this run's | applied another another | synced another | outputs {}
                guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
                guest: retired False | attached False by - | detached False | waiting -
                plan: {"counts": {"create": 0, "delete": 1, "update": 0, "replace": 0}, "changes": [{"name": "guest", "node": "korriban", "type": "proxmox_virtual_environment_vm", "vmId": 310, "actions": ["delete"], "address": "proxmox_virtual_environment_vm.guest"}], "hasChanges": true, "planSha256": "6f61ec5c7a0ab645b7b956d703d6e91bbd804f2c4dce1cc81058ef9b17e0ccaa", "toolVersion": "1.13.1"}
                approval: required False | decision approved by policy at True | hash the plan's digest
                """,
            "keeps" => """
                taken: True
                run: running | finished False | -
                step plan: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step unprotect: running | finished False | outputs False | attempts assigned | -
                step apply: pending | finished False | outputs False | attempts  | -
                step verify-absent: pending | finished False | outputs False | attempts  | -
                configuration: none
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: artifacts +1, attempts +1, secret_resolutions +1
                operation: no-such-operation
                guest: checked this run's this run's | applied another another | synced another | outputs {}
                guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
                guest: retired False | attached False by - | detached False | waiting -
                plan: {"counts": {"create": 0, "delete": 1, "update": 0, "replace": 0}, "changes": [{"name": "r0", "type": "terraform_data", "actions": ["delete", "create"], "address": "terraform_data.r0", "importing": false}], "hasChanges": true, "planSha256": "6f61ec5c7a0ab645b7b956d703d6e91bbd804f2c4dce1cc81058ef9b17e0ccaa", "toolVersion": "1.13.1"}
                approval: required False | decision approved by policy at True | hash the plan's digest
                """,
            _ => """
                taken: True
                run: running | finished False | -
                step plan: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: skipped | finished False | outputs False | attempts  | The plan has no changes.
                step unprotect: skipped | finished False | outputs False | attempts  | The plan has no changes.
                step apply: skipped | finished False | outputs False | attempts  | The plan has no changes.
                step verify-absent: running | finished False | outputs False | attempts assigned | -
                configuration: none
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: artifacts +1, attempts +1, secret_resolutions +1
                operation: no-such-operation
                guest: checked this run's this run's | applied another another | synced another | outputs {}
                guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
                guest: retired False | attached False by - | detached False | waiting -
                plan: {"counts": {"create": 0, "delete": 0, "update": 0, "replace": 0}, "changes": [], "hasChanges": false, "planSha256": "6f61ec5c7a0ab645b7b956d703d6e91bbd804f2c4dce1cc81058ef9b17e0ccaa", "toolVersion": "1.13.1"}
                approval: required False | decision - by - at False | hash -
                """,
        }, await EffectPins.DeliverPlanAsync(server, agent, plan, Outcome.Succeeded, outputs, null, FakeAgent.PlanArtifact("destroy")));
    }

    [Fact]
    public async Task A_second_delivery_leaves_the_time_the_first_one_set()
    {
        // By seconds, well inside an attempt's lease, so every first delivery is on time.
        var clock = new TestClock { Now = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero) };
        await using var server = await ServerHarness.StartAsync(postgres, clock: clock);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var resource = (await server.ResourceAsync(Guest)).Id;

        async Task<string> TwiceAsync(Assignment assignment, JsonObject outputs, Artifact artifact)
        {
            clock.Now += TimeSpan.FromSeconds(1);
            Assert.True(await agent.ReportAsync(assignment, Outcome.Succeeded, outputs.DeepClone().AsObject(), artifacts: artifact));
            await server.TickAsync();
            var first = await EffectPins.GuestTimesAsync(server, resource);
            clock.Now += TimeSpan.FromSeconds(5);
            await agent.ReportAsync(assignment, Outcome.Succeeded, outputs.DeepClone().AsObject(), artifacts: artifact);
            await server.TickAsync();
            Assert.Equal(first, await EffectPins.GuestTimesAsync(server, resource));
            return first;
        }

        var provisioned = await TwiceAsync(await agent.NextAsync("preview"), FakeAgent.Plan(create: 1), FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        await RetireAsync(server);
        var retired = await TwiceAsync(await agent.NextAsync("preview"), FakeAgent.Plan(delete: 1), FakeAgent.PlanArtifact("destroy"));

        EffectPins.Check("""
            checked 2026-11-17T09:00:01.0000000+00:00 | applied - | verified - | bound - | reservation applied - | retired - | attached -
            checked 2026-11-17T09:00:07.0000000+00:00 | applied 2026-11-17T09:00:06.0000000+00:00 | verified 2026-11-17T09:00:06.0000000+00:00 | bound 2026-11-17T09:00:06.0000000+00:00 | reservation applied 2026-11-17T09:00:06.0000000+00:00 | retired - | attached -
            """, string.Join("\n", provisioned, retired));
    }
}
