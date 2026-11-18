using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>
/// The two step names whose effect depends on the run's operation, <see cref="StepNames.Apply"/> and <see cref="StepNames.Verify"/>:
/// a retire's apply and an attach's verify, and, beside them, a provision's apply and verify, which share their names. Each run is
/// one of <see cref="ProvisioningTests"/> or <see cref="AttachTests"/>, pinned whole with a second delivery of every result. A run
/// whose operation the code does not know, as a row written by another version would hold, is pinned too.
/// </summary>
public class GuestMachineEffectCharacterizationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string Installation = "home/korriban";
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    private const string UnknownOperation = "no-such-operation";

    private static readonly JsonObject Applied = new() { ["outputs"] = new JsonObject { ["vm_id"] = 310 }, ["sensitiveOutputs"] = new JsonArray() };

    /// <summary>A new guest's provision run, planned to create its machine; returns its apply.</summary>
    private static async Task<Assignment> ProvisionApplyAsync(ServerHarness server, FakeAgent agent, JsonObject? plan = null)
    {
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, plan ?? FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        return await agent.NextAsync("apply");
    }

    /// <summary>A provisioned guest, retired; returns its retire run's apply.</summary>
    private static async Task<Assignment> RetireApplyAsync(ServerHarness server, FakeAgent agent)
    {
        await agent.ReportAsync(await ProvisionApplyAsync(server, agent), Outcome.Succeeded, Applied);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        server.Repository.Write(GuestFile, File.ReadAllText(Path.Combine(server.Repository.Root, GuestFile)) + "lifecycle: retired\n");
        server.Commit("Retire");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(delete: 1), artifacts: FakeAgent.PlanArtifact("destroy"));
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded,
            new JsonObject { ["previous"] = true, ["protection"] = false, ["absent"] = false });
        return await agent.NextAsync("apply");
    }

    private static Task<ServerHarness> StartAttachedAsync(PostgresFixture postgres, TimeProvider? clock = null)
    {
        var repository = TestRepository.Minimal();
        repository.Write(GuestFile, """
            schemaVersion: 1
            type: vm
            machine: attached
            host: korriban
            vmid: 310
            hardware: { cores: 2, memory: 2048 }
            network: { bridge: vmbr0, mode: static, address: 10.1.0.31/24, gateway: 10.1.0.1 }
            workflow: test-vm@1
            """);
        return ServerHarness.StartAsync(postgres, repository, clock: clock);
    }

    /// <summary>An attach run a person started on an observed VM; returns its verify.</summary>
    private static async Task<Assignment> AttachVerifyAsync(ServerHarness server, FakeAgent agent)
    {
        await server.SyncAndTrustAsync();
        await server.WithDbAsync(async db =>
        {
            var installation = await db.Resources.FirstAsync(r => r.Address == Installation);
            db.Observations.Add(new Observation
            {
                Id = Guid.CreateVersion7(),
                ResourceId = installation.Id,
                ObservedAt = DateTimeOffset.UtcNow,
                Data = new JsonObject
                {
                    ["cluster"] = null,
                    ["vmVisibility"] = new JsonObject { ["complete"] = true },
                    ["hosts"] = new JsonArray(new JsonObject { ["name"] = "korriban", ["online"] = true }),
                    ["guests"] = new JsonArray(new JsonObject { ["vmid"] = 310, ["name"] = "legacy-app", ["host"] = "korriban", ["status"] = "running", ["kind"] = "qemu" }),
                }.ToJsonString(),
            });
            await db.SaveChangesAsync();
        });
        var resource = await server.ResourceAsync(Guest);
        await server.Services.GetRequiredService<Planner>().AttachGuestAsync(resource.Id, "ops", CancellationToken.None);
        return await agent.NextAsync("verify-guest");
    }

    /// <summary>Gives the run of <paramref name="assignment"/> an operation the code does not know.</summary>
    private static Task RenameOperationAsync(ServerHarness server, Assignment assignment) =>
        server.WithDbAsync(db => db.Runs.Where(r => db.Attempts.Any(a => a.Id == Guid.Parse(assignment.AttemptId) && a.RunId == r.Id))
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Operation, UnknownOperation)));

    [Fact]
    public async Task A_provision_apply_and_verify_bind_the_guest_and_a_second_delivery_changes_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var apply = await ProvisionApplyAsync(server, agent);
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: running | finished False | outputs False | attempts assigned | -
            configuration: none
            resource: checked True | applied True | verified False | bound - | outputs True
            rows: attempts +1, secret_resolutions +1
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {"vm_id": 310}
            guest: bound - - at False | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            """, await EffectPins.DeliverWithGuestAsync(server, agent, apply, Outcome.Succeeded, Applied));
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: running | finished False | outputs False | attempts assigned | -
            configuration: none
            resource: checked True | applied True | verified False | bound - | outputs True
            rows: none
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {"vm_id": 310}
            guest: bound - - at False | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            """, await EffectPins.DeliverWithGuestAsync(server, agent, apply, Outcome.Succeeded, Applied));
        var verify = await agent.NextAsync("verify-guest");
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: none
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {"vm_id": 310}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            """, await EffectPins.DeliverWithGuestAsync(server, agent, verify, Outcome.Succeeded, new JsonObject { ["present"] = true }));
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: none
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {"vm_id": 310}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            """, await EffectPins.DeliverWithGuestAsync(server, agent, verify, Outcome.Succeeded, new JsonObject { ["present"] = true }));
    }

    [Fact]
    public async Task A_provision_apply_that_made_no_machine_binds_nothing_at_its_verify()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var apply = await ProvisionApplyAsync(server, agent, FakeAgent.Plan(create: 1, machine: false));
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: running | finished False | outputs False | attempts assigned | -
            configuration: none
            resource: checked True | applied True | verified False | bound - | outputs True
            rows: attempts +1, secret_resolutions +1
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {"vm_id": 310}
            guest: bound - - at False | reserved - - - at False applied False
            guest: retired False | attached False by - | detached False | waiting -
            """, await EffectPins.DeliverWithGuestAsync(server, agent, apply, Outcome.Succeeded, Applied));
        var verify = await agent.NextAsync("verify-guest");
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | VM 310 on korriban is not recorded as this guest's: no apply of an approved plan of this guest has created it.
            configuration: none
            resource: checked True | applied True | verified True | bound - | outputs True
            rows: none
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {"vm_id": 310}
            guest: bound - - at False | reserved - - - at False applied False
            guest: retired False | attached False by - | detached False | waiting -
            """, await EffectPins.DeliverWithGuestAsync(server, agent, verify, Outcome.Succeeded, new JsonObject { ["present"] = true }));
    }

    [Fact]
    public async Task A_provision_verify_of_a_machine_without_the_marks_fails_and_binds_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await agent.ReportAsync(await ProvisionApplyAsync(server, agent), Outcome.Succeeded, Applied);
        var verify = await agent.NextAsync("verify-guest");
        var unmarked = new JsonObject { ["present"] = true, ["node"] = "korriban", ["description"] = "Someone's VM." };
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The verify step failed: VM 310 on korriban does not carry Octopushy's tag 'octopushy' and the description 'Managed by Octopushy as home/korriban/krb-app-01.', so Octopushy does not record it as this guest's. Its provision module has to set both.
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: failed | finished True | outputs True | attempts succeeded | VM 310 on korriban does not carry Octopushy's tag 'octopushy' and the description 'Managed by Octopushy as home/korriban/krb-app-01.', so Octopushy does not record it as this guest's. Its provision module has to set both.
            configuration: none
            resource: checked True | applied True | verified False | bound - | outputs True
            rows: notifications +1
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {"vm_id": 310}
            guest: bound - - at False | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            """, await EffectPins.DeliverWithGuestAsync(server, agent, verify, Outcome.Succeeded, unmarked));
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The verify step failed: VM 310 on korriban does not carry Octopushy's tag 'octopushy' and the description 'Managed by Octopushy as home/korriban/krb-app-01.', so Octopushy does not record it as this guest's. Its provision module has to set both.
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: failed | finished True | outputs True | attempts succeeded | VM 310 on korriban does not carry Octopushy's tag 'octopushy' and the description 'Managed by Octopushy as home/korriban/krb-app-01.', so Octopushy does not record it as this guest's. Its provision module has to set both.
            configuration: none
            resource: checked True | applied True | verified False | bound - | outputs True
            rows: none
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {"vm_id": 310}
            guest: bound - - at False | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            """, await EffectPins.DeliverWithGuestAsync(server, agent, verify, Outcome.Succeeded, unmarked));
    }

    [Fact]
    public async Task A_retire_apply_forgets_the_outputs_and_a_second_delivery_changes_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var apply = await RetireApplyAsync(server, agent);
        var outputs = new JsonObject { ["outputs"] = new JsonObject() };
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: succeeded | finished True | outputs True | attempts succeeded | -
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-absent: running | finished False | outputs False | attempts assigned | -
            configuration: none
            resource: checked True | applied True | verified True | bound 310 | outputs False
            rows: attempts +1, secret_resolutions +1
            operation: retire
            guest: checked this run's this run's | applied another another | synced another | outputs -
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            """, await EffectPins.DeliverWithGuestAsync(server, agent, apply, Outcome.Succeeded, outputs));
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: succeeded | finished True | outputs True | attempts succeeded | -
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-absent: running | finished False | outputs False | attempts assigned | -
            configuration: none
            resource: checked True | applied True | verified True | bound 310 | outputs False
            rows: none
            operation: retire
            guest: checked this run's this run's | applied another another | synced another | outputs -
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            """, await EffectPins.DeliverWithGuestAsync(server, agent, apply, Outcome.Succeeded, outputs));
        var absent = await agent.NextAsync("verify-absent");
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: succeeded | finished True | outputs True | attempts succeeded | -
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-absent: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: none
            resource: checked True | applied True | verified True | bound - | outputs False
            rows: none
            operation: retire
            guest: checked this run's this run's | applied another another | synced another | outputs -
            guest: bound - - at True | reserved - - - at False applied False
            guest: retired True | attached False by - | detached False | waiting -
            """, await EffectPins.DeliverWithGuestAsync(server, agent, absent, Outcome.Succeeded, new JsonObject { ["present"] = false }));
    }

    [Fact]
    public async Task A_retire_apply_that_fails_keeps_the_outputs()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var apply = await RetireApplyAsync(server, agent);
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The apply step failed: the provider failed
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: succeeded | finished True | outputs True | attempts succeeded | -
            step apply: failed | finished True | outputs False | attempts failed | the provider failed
            step verify-absent: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            configuration: none
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: notifications +1
            operation: retire
            guest: checked this run's this run's | applied another another | synced another | outputs {"vm_id": 310}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            """, await EffectPins.DeliverWithGuestAsync(server, agent, apply, Outcome.Failed, null, "the provider failed"));
    }

    [Fact]
    public async Task An_attach_verify_records_the_time_and_binds_nothing_until_protect()
    {
        await using var server = await StartAttachedAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var verify = await AttachVerifyAsync(server, agent);
        var present = new JsonObject { ["present"] = true, ["node"] = "korriban" };
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            step protect: running | finished False | outputs False | attempts assigned | -
            configuration: none
            resource: checked False | applied False | verified True | bound - | outputs False
            rows: attempts +1, secret_resolutions +1
            operation: attach
            guest: checked - - | applied - - | synced - | outputs -
            guest: bound - - at False | reserved - - - at False applied False
            guest: retired False | attached False by - | detached False | waiting Waiting to be attached: use Attach on the guest's page.
            """, await EffectPins.DeliverWithGuestAsync(server, agent, verify, Outcome.Succeeded, present));
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            step protect: running | finished False | outputs False | attempts assigned | -
            configuration: none
            resource: checked False | applied False | verified True | bound - | outputs False
            rows: none
            operation: attach
            guest: checked - - | applied - - | synced - | outputs -
            guest: bound - - at False | reserved - - - at False applied False
            guest: retired False | attached False by - | detached False | waiting Waiting to be attached: use Attach on the guest's page.
            """, await EffectPins.DeliverWithGuestAsync(server, agent, verify, Outcome.Succeeded, present));
        var protect = await agent.NextAsync("set-protection");
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            step protect: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: none
            resource: checked False | applied False | verified True | bound 310 | outputs False
            rows: none
            operation: attach
            guest: checked - - | applied - - | synced - | outputs -
            guest: bound home/korriban 310 at True | reserved - - - at False applied False
            guest: retired False | attached True by ops | detached False | waiting -
            """, await EffectPins.DeliverWithGuestAsync(server, agent, protect, Outcome.Succeeded,
            new JsonObject { ["absent"] = false, ["previous"] = false, ["protection"] = true }));
    }

    [Fact]
    public async Task An_attach_verify_of_a_machine_without_the_marks_records_the_time_as_any_other()
    {
        // Octopushy did not create an attached machine, so its marks are not asked for.
        await using var server = await StartAttachedAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var verify = await AttachVerifyAsync(server, agent);
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            step protect: running | finished False | outputs False | attempts assigned | -
            configuration: none
            resource: checked False | applied False | verified True | bound - | outputs False
            rows: attempts +1, secret_resolutions +1
            operation: attach
            guest: checked - - | applied - - | synced - | outputs -
            guest: bound - - at False | reserved - - - at False applied False
            guest: retired False | attached False by - | detached False | waiting Waiting to be attached: use Attach on the guest's page.
            """, await EffectPins.DeliverWithGuestAsync(server, agent, verify, Outcome.Succeeded,
            new JsonObject { ["present"] = true, ["node"] = "korriban", ["description"] = "Someone's VM." }));
    }

    [Fact]
    public async Task An_attach_verify_that_fails_records_nothing()
    {
        await using var server = await StartAttachedAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var verify = await AttachVerifyAsync(server, agent);
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The verify step failed: VM 310 is on dromund, not korriban
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify: failed | finished True | outputs False | attempts failed | VM 310 is on dromund, not korriban
            step protect: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            configuration: none
            resource: checked False | applied False | verified False | bound - | outputs False
            rows: notifications +1
            operation: attach
            guest: checked - - | applied - - | synced - | outputs -
            guest: bound - - at False | reserved - - - at False applied False
            guest: retired False | attached False by - | detached False | waiting Waiting to be attached: use Attach on the guest's page.
            """, await EffectPins.DeliverWithGuestAsync(server, agent, verify, Outcome.Failed, null, "VM 310 is on dromund, not korriban"));
    }

    [Fact]
    public async Task An_apply_of_an_operation_the_code_does_not_know_forgets_the_outputs_as_a_retire_does()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var apply = await ProvisionApplyAsync(server, agent);
        await RenameOperationAsync(server, apply);
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: running | finished False | outputs False | attempts assigned | -
            configuration: none
            resource: checked True | applied False | verified False | bound - | outputs False
            rows: attempts +1, secret_resolutions +1
            operation: no-such-operation
            guest: checked this run's this run's | applied - - | synced - | outputs -
            guest: bound - - at False | reserved home/korriban korriban 310 at True applied False
            guest: retired False | attached False by - | detached False | waiting -
            """, await EffectPins.DeliverWithGuestAsync(server, agent, apply, Outcome.Succeeded, Applied));
    }

    [Fact]
    public async Task An_apply_of_an_operation_the_code_does_not_know_forgets_outputs_a_guest_had()
    {
        // The guest has outputs from its provision, so forgetting them shows.
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var apply = await RetireApplyAsync(server, agent);
        await RenameOperationAsync(server, apply);
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: succeeded | finished True | outputs True | attempts succeeded | -
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify-absent: running | finished False | outputs False | attempts assigned | -
            configuration: none
            resource: checked True | applied True | verified True | bound 310 | outputs False
            rows: attempts +1, secret_resolutions +1
            operation: no-such-operation
            guest: checked this run's this run's | applied another another | synced another | outputs -
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            """, await EffectPins.DeliverWithGuestAsync(server, agent, apply, Outcome.Succeeded, Applied));
    }

    [Fact]
    public async Task A_verify_of_an_operation_the_code_does_not_know_is_a_provision_verify()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await agent.ReportAsync(await ProvisionApplyAsync(server, agent), Outcome.Succeeded, Applied);
        var verify = await agent.NextAsync("verify-guest");
        await RenameOperationAsync(server, verify);
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: none
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            operation: no-such-operation
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {"vm_id": 310}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            """, await EffectPins.DeliverWithGuestAsync(server, agent, verify, Outcome.Succeeded, new JsonObject { ["present"] = true }));
    }

    [Fact]
    public async Task A_second_delivery_leaves_the_times_the_first_one_set()
    {
        // By seconds, well inside an attempt's lease, so every first delivery is on time.
        var clock = new TestClock { Now = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero) };
        await using var server = await ServerHarness.StartAsync(postgres, clock: clock);
        var agent = await server.ConnectAgentAsync("home");
        var resource = Guid.Empty;

        async Task<string> TwiceAsync(Assignment assignment, JsonObject outputs)
        {
            clock.Now += TimeSpan.FromSeconds(1);
            Assert.True(await agent.ReportAsync(assignment, Outcome.Succeeded, outputs.DeepClone().AsObject()));
            await server.TickAsync();
            resource = (await server.ResourceAsync(Guest)).Id;
            var first = await EffectPins.GuestTimesAsync(server, resource);
            clock.Now += TimeSpan.FromSeconds(5);
            await agent.ReportAsync(assignment, Outcome.Succeeded, outputs.DeepClone().AsObject());
            await server.TickAsync();
            Assert.Equal(first, await EffectPins.GuestTimesAsync(server, resource));
            return first;
        }

        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        var applied = await TwiceAsync(await agent.NextAsync("apply"), Applied);
        var verified = await TwiceAsync(await agent.NextAsync("verify-guest"), new JsonObject { ["present"] = true });

        server.Repository.Write(GuestFile, File.ReadAllText(Path.Combine(server.Repository.Root, GuestFile)) + "lifecycle: retired\n");
        server.Commit("Retire");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(delete: 1), artifacts: FakeAgent.PlanArtifact("destroy"));
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded,
            new JsonObject { ["previous"] = true, ["protection"] = false, ["absent"] = false });
        var retired = await TwiceAsync(await agent.NextAsync("apply"), new JsonObject { ["outputs"] = new JsonObject() });

        EffectPins.Check("""
            checked 2026-11-17T09:00:00.0000000+00:00 | applied 2026-11-17T09:00:01.0000000+00:00 | verified - | bound - | reservation applied 2026-11-17T09:00:01.0000000+00:00 | retired - | attached -
            checked 2026-11-17T09:00:00.0000000+00:00 | applied 2026-11-17T09:00:01.0000000+00:00 | verified 2026-11-17T09:00:07.0000000+00:00 | bound 2026-11-17T09:00:07.0000000+00:00 | reservation applied 2026-11-17T09:00:01.0000000+00:00 | retired - | attached -
            checked 2026-11-17T09:00:12.0000000+00:00 | applied 2026-11-17T09:00:01.0000000+00:00 | verified 2026-11-17T09:00:07.0000000+00:00 | bound 2026-11-17T09:00:07.0000000+00:00 | reservation applied 2026-11-17T09:00:01.0000000+00:00 | retired - | attached -
            """, string.Join("\n", applied, verified, retired));
    }

    [Fact]
    public async Task A_second_delivery_of_an_attach_verify_leaves_the_time_the_first_one_set()
    {
        var clock = new TestClock { Now = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero) };
        await using var server = await StartAttachedAsync(postgres, clock);
        var agent = await server.ConnectAgentAsync("home");
        var verify = await AttachVerifyAsync(server, agent);
        var resource = (await server.ResourceAsync(Guest)).Id;
        var present = new JsonObject { ["present"] = true, ["node"] = "korriban" };

        clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(await agent.ReportAsync(verify, Outcome.Succeeded, present.DeepClone().AsObject()));
        await server.TickAsync();
        var first = await EffectPins.GuestTimesAsync(server, resource);
        clock.Now += TimeSpan.FromSeconds(5);
        await agent.ReportAsync(verify, Outcome.Succeeded, present.DeepClone().AsObject());
        await server.TickAsync();
        Assert.Equal(first, await EffectPins.GuestTimesAsync(server, resource));
        EffectPins.Check("""
            checked - | applied - | verified 2026-11-17T09:00:01.0000000+00:00 | bound - | reservation applied - | retired - | attached -
            """, first);
    }
}
