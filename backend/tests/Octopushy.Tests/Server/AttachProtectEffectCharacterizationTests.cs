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
/// An attach run's <see cref="StepNames.Protect"/>: once protection is on, and only then, the guest is bound to the machine a person
/// attached it to, recorded as attached by the person who approved the run, and no longer waiting. The machine is checked again
/// first: a guest bound to another one, a VMID another guest holds or reserved, or an installation whose latest observation shows no
/// machine there, or none, leaves the step failed and the guest unbound, with protection on. Each case is one of
/// <see cref="AttachTests"/>, pinned whole with a failure, a late result and a second delivery of every result. A run whose
/// operation the code does not know, as a row written by another version would hold, is pinned too.
/// </summary>
public class AttachProtectEffectCharacterizationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string Other = "home/korriban/krb-other-01";
    private const string Installation = "home/korriban";
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    private const string UnknownOperation = "no-such-operation";

    private static readonly JsonObject Protected = new() { ["absent"] = false, ["previous"] = false, ["protection"] = true };

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

    /// <summary>Records an observation of korriban that shows these VMs, the latest one.</summary>
    private static Task ObserveAsync(ServerHarness server, params int[] vmids) =>
        server.WithDbAsync(async db =>
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
                    ["guests"] = new JsonArray(vmids.Select(vmid => (JsonNode)new JsonObject
                    {
                        ["vmid"] = vmid,
                        ["name"] = "legacy-app",
                        ["host"] = "korriban",
                        ["status"] = "running",
                        ["kind"] = "qemu",
                    }).ToArray()),
                }.ToJsonString(),
            });
            await db.SaveChangesAsync();
        });

    /// <summary>An attach run a person started on an observed VM, verified; returns its protect.</summary>
    private static async Task<Assignment> ProtectAsync(ServerHarness server, FakeAgent agent)
    {
        await server.SyncAndTrustAsync();
        await ObserveAsync(server, 310);
        var resource = await server.ResourceAsync(Guest);
        await server.Services.GetRequiredService<Planner>().AttachGuestAsync(resource.Id, "ops", CancellationToken.None);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject { ["present"] = true, ["node"] = "korriban" });
        return await agent.NextAsync("set-protection");
    }

    /// <summary>Adds another guest of the installation, bound to or holding a reservation of <paramref name="vmid"/>.</summary>
    private static Task OtherGuestAsync(ServerHarness server, int vmid, bool bound) =>
        server.WithDbAsync(async db =>
        {
            db.Resources.Add(new Resource
            {
                Id = Guid.NewGuid(),
                Kind = ResourceKinds.Guest,
                Address = Other,
                ParentAddress = Installation,
                Lab = "home",
                BoundVmid = bound ? vmid : null,
                BoundInstallation = bound ? Installation : null,
                BoundAt = bound ? DateTimeOffset.UtcNow : null,
                ReservedVmid = bound ? null : vmid,
                ReservedInstallation = bound ? null : Installation,
                ReservedNode = bound ? null : "korriban",
                ReservedAt = bound ? null : DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });

    private static Task RenameOperationAsync(ServerHarness server, Assignment assignment) =>
        server.WithDbAsync(db => db.Runs.Where(r => db.Attempts.Any(a => a.Id == Guid.Parse(assignment.AttemptId) && a.RunId == r.Id))
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Operation, UnknownOperation)));

    [Fact]
    public async Task An_attach_protect_binds_the_guest_on_the_persons_word_and_a_second_delivery_changes_nothing()
    {
        await using var server = await StartAttachedAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var protect = await ProtectAsync(server, agent);
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
            """, await EffectPins.DeliverWithGuestAsync(server, agent, protect, Outcome.Succeeded, Protected));
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
            """, await EffectPins.DeliverWithGuestAsync(server, agent, protect, Outcome.Succeeded, Protected));
    }

    [Fact]
    public async Task An_attach_protect_that_fails_binds_nothing()
    {
        await using var server = await StartAttachedAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var protect = await ProtectAsync(server, agent);
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The protect step failed: permission denied
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            step protect: failed | finished True | outputs False | attempts failed | permission denied
            configuration: none
            resource: checked False | applied False | verified True | bound - | outputs False
            rows: notifications +1
            operation: attach
            guest: checked - - | applied - - | synced - | outputs -
            guest: bound - - at False | reserved - - - at False applied False
            guest: retired False | attached False by - | detached False | waiting Waiting to be attached: use Attach on the guest's page.
            """, await EffectPins.DeliverWithGuestAsync(server, agent, protect, Outcome.Failed, null, "permission denied"));
    }

    [Fact]
    public async Task An_attach_protect_reported_uncertain_is_settled_once_by_its_late_result()
    {
        await using var server = await StartAttachedAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var protect = await ProtectAsync(server, agent);
        EffectPins.Check("""
            taken: True
            run: uncertain | finished False | lost
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            step protect: uncertain | finished True | outputs False | attempts uncertain | lost
            configuration: none
            resource: checked False | applied False | verified True | bound - | outputs False
            rows: notifications +1
            operation: attach
            guest: checked - - | applied - - | synced - | outputs -
            guest: bound - - at False | reserved - - - at False applied False
            guest: retired False | attached False by - | detached False | waiting Waiting to be attached: use Attach on the guest's page.
            """, await EffectPins.DeliverWithGuestAsync(server, agent, protect, Outcome.Uncertain, new JsonObject(), "lost"));
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            step protect: succeeded | finished True | outputs True | attempts succeeded | The result arrived after the agent had stopped reporting.
            configuration: none
            resource: checked False | applied False | verified True | bound 310 | outputs False
            rows: none
            operation: attach
            guest: checked - - | applied - - | synced - | outputs -
            guest: bound home/korriban 310 at True | reserved - - - at False applied False
            guest: retired False | attached True by ops | detached False | waiting -
            """, await EffectPins.DeliverWithGuestAsync(server, agent, protect, Outcome.Succeeded, Protected));
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            step protect: succeeded | finished True | outputs True | attempts succeeded | The result arrived after the agent had stopped reporting.
            configuration: none
            resource: checked False | applied False | verified True | bound 310 | outputs False
            rows: none
            operation: attach
            guest: checked - - | applied - - | synced - | outputs -
            guest: bound home/korriban 310 at True | reserved - - - at False applied False
            guest: retired False | attached True by ops | detached False | waiting -
            """, await EffectPins.DeliverWithGuestAsync(server, agent, protect, Outcome.Succeeded, Protected));
    }

    [Fact]
    public async Task An_attach_protect_whose_vmid_became_another_guests_binds_nothing()
    {
        await using var server = await StartAttachedAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var protect = await ProtectAsync(server, agent);
        await OtherGuestAsync(server, 310, bound: true);
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The protect step failed: Protection is on, and the guest is not attached: VMID 310 on home/korriban is home/korriban/krb-other-01's.
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            step protect: failed | finished True | outputs True | attempts succeeded | Protection is on, and the guest is not attached: VMID 310 on home/korriban is home/korriban/krb-other-01's.
            configuration: none
            resource: checked False | applied False | verified True | bound - | outputs False
            rows: notifications +1
            operation: attach
            guest: checked - - | applied - - | synced - | outputs -
            guest: bound - - at False | reserved - - - at False applied False
            guest: retired False | attached False by - | detached False | waiting Waiting to be attached: use Attach on the guest's page.
            """, await EffectPins.DeliverWithGuestAsync(server, agent, protect, Outcome.Succeeded, Protected));
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The protect step failed: Protection is on, and the guest is not attached: VMID 310 on home/korriban is home/korriban/krb-other-01's.
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            step protect: failed | finished True | outputs True | attempts succeeded | Protection is on, and the guest is not attached: VMID 310 on home/korriban is home/korriban/krb-other-01's.
            configuration: none
            resource: checked False | applied False | verified True | bound - | outputs False
            rows: none
            operation: attach
            guest: checked - - | applied - - | synced - | outputs -
            guest: bound - - at False | reserved - - - at False applied False
            guest: retired False | attached False by - | detached False | waiting Waiting to be attached: use Attach on the guest's page.
            """, await EffectPins.DeliverWithGuestAsync(server, agent, protect, Outcome.Succeeded, Protected));
    }

    [Fact]
    public async Task An_attach_protect_whose_vmid_another_guest_reserved_binds_nothing()
    {
        await using var server = await StartAttachedAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var protect = await ProtectAsync(server, agent);
        await OtherGuestAsync(server, 310, bound: false);
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The protect step failed: Protection is on, and the guest is not attached: VMID 310 on home/korriban is home/korriban/krb-other-01's.
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            step protect: failed | finished True | outputs True | attempts succeeded | Protection is on, and the guest is not attached: VMID 310 on home/korriban is home/korriban/krb-other-01's.
            configuration: none
            resource: checked False | applied False | verified True | bound - | outputs False
            rows: notifications +1
            operation: attach
            guest: checked - - | applied - - | synced - | outputs -
            guest: bound - - at False | reserved - - - at False applied False
            guest: retired False | attached False by - | detached False | waiting Waiting to be attached: use Attach on the guest's page.
            """, await EffectPins.DeliverWithGuestAsync(server, agent, protect, Outcome.Succeeded, Protected));
    }

    [Fact]
    public async Task An_attach_protect_of_a_guest_bound_to_another_machine_binds_nothing()
    {
        await using var server = await StartAttachedAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var protect = await ProtectAsync(server, agent);
        await server.WithDbAsync(db => db.Resources.Where(r => r.Address == Guest)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.BoundVmid, 311).SetProperty(r => r.BoundInstallation, Installation)));
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The protect step failed: Protection is on, and the guest is not attached: the guest is bound to VMID 311 on home/korriban.
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            step protect: failed | finished True | outputs True | attempts succeeded | Protection is on, and the guest is not attached: the guest is bound to VMID 311 on home/korriban.
            configuration: none
            resource: checked False | applied False | verified True | bound 311 | outputs False
            rows: notifications +1
            operation: attach
            guest: checked - - | applied - - | synced - | outputs -
            guest: bound home/korriban 311 at False | reserved - - - at False applied False
            guest: retired False | attached False by - | detached False | waiting Waiting to be attached: use Attach on the guest's page.
            """, await EffectPins.DeliverWithGuestAsync(server, agent, protect, Outcome.Succeeded, Protected));
    }

    [Fact]
    public async Task An_attach_protect_whose_machine_the_latest_observation_does_not_show_binds_nothing()
    {
        await using var server = await StartAttachedAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var protect = await ProtectAsync(server, agent);
        await ObserveAsync(server, 311);
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The protect step failed: Protection is on, and the guest is not attached: home/korriban's latest observation shows no machine at VMID 310.
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            step protect: failed | finished True | outputs True | attempts succeeded | Protection is on, and the guest is not attached: home/korriban's latest observation shows no machine at VMID 310.
            configuration: none
            resource: checked False | applied False | verified True | bound - | outputs False
            rows: notifications +1
            operation: attach
            guest: checked - - | applied - - | synced - | outputs -
            guest: bound - - at False | reserved - - - at False applied False
            guest: retired False | attached False by - | detached False | waiting Waiting to be attached: use Attach on the guest's page.
            """, await EffectPins.DeliverWithGuestAsync(server, agent, protect, Outcome.Succeeded, Protected));
    }

    [Fact]
    public async Task An_attach_protect_of_an_installation_with_no_observation_binds_nothing()
    {
        await using var server = await StartAttachedAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var protect = await ProtectAsync(server, agent);
        await server.WithDbAsync(db => db.Observations.ExecuteDeleteAsync());
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The protect step failed: Protection is on, and the guest is not attached: home/korriban has not been observed, so there is no machine at VMID 310 to attach yet.
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            step protect: failed | finished True | outputs True | attempts succeeded | Protection is on, and the guest is not attached: home/korriban has not been observed, so there is no machine at VMID 310 to attach yet.
            configuration: none
            resource: checked False | applied False | verified True | bound - | outputs False
            rows: notifications +1
            operation: attach
            guest: checked - - | applied - - | synced - | outputs -
            guest: bound - - at False | reserved - - - at False applied False
            guest: retired False | attached False by - | detached False | waiting Waiting to be attached: use Attach on the guest's page.
            """, await EffectPins.DeliverWithGuestAsync(server, agent, protect, Outcome.Succeeded, Protected));
    }

    [Fact]
    public async Task A_protect_of_an_operation_the_code_does_not_know_binds_nothing()
    {
        await using var server = await StartAttachedAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var protect = await ProtectAsync(server, agent);
        await RenameOperationAsync(server, protect);
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            step protect: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: none
            resource: checked False | applied False | verified True | bound - | outputs False
            rows: none
            operation: no-such-operation
            guest: checked - - | applied - - | synced - | outputs -
            guest: bound - - at False | reserved - - - at False applied False
            guest: retired False | attached False by - | detached False | waiting Waiting to be attached: use Attach on the guest's page.
            """, await EffectPins.DeliverWithGuestAsync(server, agent, protect, Outcome.Succeeded, Protected));
    }

    [Fact]
    public async Task A_second_delivery_leaves_the_times_the_first_one_set()
    {
        var clock = new TestClock { Now = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero) };
        await using var server = await StartAttachedAsync(postgres, clock);
        var agent = await server.ConnectAgentAsync("home");
        var protect = await ProtectAsync(server, agent);
        var resource = (await server.ResourceAsync(Guest)).Id;

        clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(await agent.ReportAsync(protect, Outcome.Succeeded, Protected.DeepClone().AsObject()));
        await server.TickAsync();
        var first = await EffectPins.GuestTimesAsync(server, resource);
        clock.Now += TimeSpan.FromSeconds(5);
        await agent.ReportAsync(protect, Outcome.Succeeded, Protected.DeepClone().AsObject());
        await server.TickAsync();
        Assert.Equal(first, await EffectPins.GuestTimesAsync(server, resource));
        EffectPins.Check("""
            checked - | applied - | verified 2026-11-17T09:00:00.0000000+00:00 | bound 2026-11-17T09:00:01.0000000+00:00 | reservation applied - | retired - | attached 2026-11-17T09:00:01.0000000+00:00
            """, first);
    }
}
