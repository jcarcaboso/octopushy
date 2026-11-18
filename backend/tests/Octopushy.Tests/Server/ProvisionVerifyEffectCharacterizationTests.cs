using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Repository = Octopushy.Tests.Definitions.GuestConfigurationTests;

namespace Octopushy.Tests.Server;

/// <summary>
/// A provision run's <see cref="StepNames.Verify"/>, the verify of every run but an attach's: ownership comes only from a confirmed
/// creation, so a guest not bound yet is bound only when its reservation is for this machine, an apply marked it applied, and the
/// machine carries Octopushy's marks; without the marks the step fails. A container's host keys are pinned only from the answer of
/// the attempt that was asked, about the container the guest is bound to. The verified time is set unless the marks are missing.
/// Each case is one of <see cref="ProvisioningTests"/> or <see cref="ContainerGuestTests"/>, pinned whole with a failure, a late
/// result and a second delivery of every result. A run whose operation the code does not know, as a row written by another version
/// would hold, is pinned too.
/// </summary>
public class ProvisionVerifyEffectCharacterizationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    private const string WorkflowFile = "workflows/app/workflow.yaml";
    private const string UnknownOperation = "no-such-operation";
    private const string Ed25519 = "ed25519 SHA256:Zm9vYmFyYmF6cXV4cXV1eGNvcmdlZ3JhdWx0Z2FycA0";
    private const string Rsa = "rsa SHA256:cnNhcnNhcnNhcnNhcnNhcnNhcnNhcnNhcnNhcnNhcnM";

    /// <summary>The configure fixture as a container with Octopushy's own login and no host key named, as its tests have it.</summary>
    private static TestRepository Container()
    {
        var test = Repository.Repository();
        test.Write(GuestFile, File.ReadAllText(Path.Combine(test.Root, GuestFile))
            .Replace("type: vm", "type: container")
            .Replace("  sshKey: { store: access/app/ssh-key }\n", "")
            .Replace("  hostKey: { store: access/app/host-key }\n", ""));
        test.Write(WorkflowFile, File.ReadAllText(Path.Combine(test.Root, WorkflowFile)).Replace("manages: vm", "manages: container"));
        return test;
    }

    private static JsonObject ContainerPlan(string[] actions) => SafetyBoundaryFixtures.MachinePlan(
        SafetyBoundaryFixtures.Change(GuestVmPlan.ContainerResourceType, actions, 310, "korriban"));

    /// <summary>What verify-guest reports of the guest's container: on its node, with Octopushy's marks, and these host keys.</summary>
    private static JsonObject Verified(params string[] hostKeys) => new()
    {
        ["present"] = true,
        ["node"] = "korriban",
        ["status"] = "running",
        ["tags"] = new JsonArray(SafetyBoundary.OwnerTag, "home"),
        ["description"] = SafetyBoundary.OwnerDescription(Guest),
        ["ipv4Addresses"] = new JsonArray("10.1.0.31"),
        ["hostKeys"] = new JsonArray(hostKeys.Select(k => (JsonNode)k).ToArray()),
    };

    private static async Task<(ServerHarness Server, FakeAgent Agent)> StartContainerAsync(PostgresFixture postgres, TimeProvider? clock = null)
    {
        var server = await ServerHarness.StartAsync(postgres, Container(), clock: clock);
        await server.SetSecretAsync("app/tailscale", "tskey-secret-value");
        await server.SetSecretAsync("app/git-key", "git-key-value");
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        return (server, agent);
    }

    /// <summary>A new guest's container planned with <paramref name="actions"/> and applied; returns its verify.</summary>
    private static async Task<Assignment> ContainerVerifyAsync(FakeAgent agent, string action = "create")
    {
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan([action]), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        return await agent.NextAsync("verify-guest");
    }

    /// <summary>A new guest's VM created and applied; returns its verify.</summary>
    private static async Task<Assignment> VmVerifyAsync(ServerHarness server, FakeAgent agent)
    {
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        return await agent.NextAsync("verify-guest");
    }

    private static Task GuestAsync(ServerHarness server, Action<Resource> change) =>
        server.WithDbAsync(async db =>
        {
            change(await db.Resources.SingleAsync(r => r.Address == Guest));
            await db.SaveChangesAsync();
        });

    private static Task RenameOperationAsync(ServerHarness server, Assignment assignment) =>
        server.WithDbAsync(db => db.Runs.Where(r => db.Attempts.Any(a => a.Id == Guid.Parse(assignment.AttemptId) && a.RunId == r.Id))
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Operation, UnknownOperation)));

    [Fact]
    public async Task A_container_verify_binds_the_guest_and_pins_its_host_keys_and_a_second_delivery_changes_nothing()
    {
        var (server, agent) = await StartContainerAsync(postgres);
        await using var _ = server;
        var verify = await ContainerVerifyAsync(agent);
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's container, or change it in place, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
            access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: store_entries +1, store_versions +1
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            container host keys: current v1 | latest v1
            container host keys v1: revoked False | ed25519 SHA256:Zm9vYmFyYmF6cXV4cXV1eGNvcmdlZ3JhdWx0Z2FycA0 rsa SHA256:cnNhcnNhcnNhcnNhcnNhcnNhcnNhcnNhcnNhcnNhcnM
            """, await EffectPins.DeliverWithKeysAsync(server, agent, verify, Outcome.Succeeded, Verified(Rsa, Ed25519)));
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's container, or change it in place, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
            access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            container host keys: current v1 | latest v1
            container host keys v1: revoked False | ed25519 SHA256:Zm9vYmFyYmF6cXV4cXV1eGNvcmdlZ3JhdWx0Z2FycA0 rsa SHA256:cnNhcnNhcnNhcnNhcnNhcnNhcnNhcnNhcnNhcnNhcnM
            """, await EffectPins.DeliverWithKeysAsync(server, agent, verify, Outcome.Succeeded, Verified(Ed25519)));
    }

    [Fact]
    public async Task A_container_verify_that_fails_records_nothing()
    {
        var (server, agent) = await StartContainerAsync(postgres);
        await using var _ = server;
        var verify = await ContainerVerifyAsync(agent);
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The verify step failed: VM 310 is not on korriban
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's container, or change it in place, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: failed | finished True | outputs False | attempts failed | VM 310 is not on korriban
            configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
            access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
            resource: checked True | applied True | verified False | bound - | outputs True
            rows: notifications +1
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
            guest: bound - - at False | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            container host keys: none
            """, await EffectPins.DeliverWithKeysAsync(server, agent, verify, Outcome.Failed, null, "VM 310 is not on korriban"));
    }

    [Fact]
    public async Task A_container_verify_reported_uncertain_is_settled_once_by_its_late_result()
    {
        var (server, agent) = await StartContainerAsync(postgres);
        await using var _ = server;
        var verify = await ContainerVerifyAsync(agent);
        EffectPins.Check("""
            taken: True
            run: uncertain | finished False | lost
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's container, or change it in place, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: uncertain | finished True | outputs False | attempts uncertain | lost
            configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
            access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
            resource: checked True | applied True | verified False | bound - | outputs True
            rows: notifications +1
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
            guest: bound - - at False | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            container host keys: none
            """, await EffectPins.DeliverWithKeysAsync(server, agent, verify, Outcome.Uncertain, null, "lost"));
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's container, or change it in place, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | The result arrived after the agent had stopped reporting.
            configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
            access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: store_entries +1, store_versions +1
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            container host keys: current v1 | latest v1
            container host keys v1: revoked False | ed25519 SHA256:Zm9vYmFyYmF6cXV4cXV1eGNvcmdlZ3JhdWx0Z2FycA0
            """, await EffectPins.DeliverWithKeysAsync(server, agent, verify, Outcome.Succeeded, Verified(Ed25519)));
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's container, or change it in place, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | The result arrived after the agent had stopped reporting.
            configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
            access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            container host keys: current v1 | latest v1
            container host keys v1: revoked False | ed25519 SHA256:Zm9vYmFyYmF6cXV4cXV1eGNvcmdlZ3JhdWx0Z2FycA0
            """, await EffectPins.DeliverWithKeysAsync(server, agent, verify, Outcome.Succeeded, Verified(Rsa)));
    }

    [Fact]
    public async Task A_container_verify_whose_keys_could_not_be_read_says_so_and_pins_nothing()
    {
        var (server, agent) = await StartContainerAsync(postgres);
        await using var _ = server;
        var verify = await ContainerVerifyAsync(agent);
        var outputs = Verified();
        outputs.Remove("hostKeys");
        outputs["hostKeysError"] = "after the create task, Proxmox lists vzrestore for VMID 310";
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's container, or change it in place, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | The container's host keys could not be read again from the run that created it: after the create task, Proxmox lists vzrestore for VMID 310
            configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
            access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            container host keys: none
            """, await EffectPins.DeliverWithKeysAsync(server, agent, verify, Outcome.Succeeded, outputs));
    }

    [Fact]
    public async Task A_container_verify_without_keys_or_an_error_pins_nothing_and_says_nothing()
    {
        var (server, agent) = await StartContainerAsync(postgres);
        await using var _ = server;
        var verify = await ContainerVerifyAsync(agent);
        var outputs = Verified();
        outputs.Remove("hostKeys");
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's container, or change it in place, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
            access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            container host keys: none
            """, await EffectPins.DeliverWithKeysAsync(server, agent, verify, Outcome.Succeeded, outputs));
    }

    [Fact]
    public async Task Keys_an_attempt_reports_without_being_asked_pin_nothing()
    {
        var (server, agent) = await StartContainerAsync(postgres);
        await using var _ = server;
        var verify = await ContainerVerifyAsync(agent);
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        await server.WithDbAsync(async db =>
        {
            var stored = await db.Runs.SingleAsync(r => r.Id == run.Id);
            var asked = RunContext.Parse(stored.Context);
            stored.Context = (asked with { HostKeysRequest = asked.HostKeysRequest! with { AttemptId = Guid.NewGuid() } }).Serialize();
            await db.SaveChangesAsync();
        });
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's container, or change it in place, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
            access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            container host keys: none
            """, await EffectPins.DeliverWithKeysAsync(server, agent, verify, Outcome.Succeeded, Verified(Ed25519)));
    }

    [Fact]
    public async Task Keys_a_run_that_does_not_pin_its_containers_keys_was_asked_for_pin_nothing()
    {
        // A run whose guest names its own host key: no request is made for one, but were one recorded, the run still pins nothing.
        var (server, agent) = await StartContainerAsync(postgres);
        await using var _ = server;
        var verify = await ContainerVerifyAsync(agent);
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        await server.WithDbAsync(async db =>
        {
            var stored = await db.Runs.SingleAsync(r => r.Id == run.Id);
            stored.Context = (RunContext.Parse(stored.Context) with { ContainerHostKey = false }).Serialize();
            await db.SaveChangesAsync();
        });
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's container, or change it in place, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
            access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: none
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            container host keys: none
            """, await EffectPins.DeliverWithKeysAsync(server, agent, verify, Outcome.Succeeded, Verified(Ed25519)));
    }

    [Fact]
    public async Task A_container_verify_of_a_run_that_made_no_container_asks_for_no_keys_and_pins_none()
    {
        var (server, agent) = await StartContainerAsync(postgres);
        await using var _ = server;
        var verify = await ContainerVerifyAsync(agent, "update");
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | This guest does not own container 310 on korriban, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | container 310 on korriban is not recorded as this guest's: no apply of an approved plan of this guest has created it.
            configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
            access key v1: last used - at False | provisioned False | authorized False | verified False | removed False
            resource: checked True | applied True | verified True | bound - | outputs True
            rows: none
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
            guest: bound - - at False | reserved - - - at False applied False
            guest: retired False | attached False by - | detached False | waiting -
            container host keys: none
            """, await EffectPins.DeliverWithKeysAsync(server, agent, verify, Outcome.Succeeded, Verified(Ed25519)));
    }

    [Fact]
    public async Task A_container_verify_without_the_marks_fails_and_pins_no_keys()
    {
        var (server, agent) = await StartContainerAsync(postgres);
        await using var _ = server;
        var verify = await ContainerVerifyAsync(agent);
        var outputs = Verified(Ed25519);
        outputs["description"] = "Someone's container.";
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The verify step failed: container 310 on korriban does not carry Octopushy's tag 'octopushy' and the description 'Managed by Octopushy as home/korriban/krb-app-01.', so Octopushy does not record it as this guest's. Its provision module has to set both.
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's container, or change it in place, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: failed | finished True | outputs True | attempts succeeded | container 310 on korriban does not carry Octopushy's tag 'octopushy' and the description 'Managed by Octopushy as home/korriban/krb-app-01.', so Octopushy does not record it as this guest's. Its provision module has to set both.
            configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
            access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
            resource: checked True | applied True | verified False | bound - | outputs True
            rows: notifications +1
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
            guest: bound - - at False | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            container host keys: none
            """, await EffectPins.DeliverWithKeysAsync(server, agent, verify, Outcome.Succeeded, outputs));
    }

    [Theory]
    [InlineData("not applied")]
    [InlineData("reserved vmid")]
    [InlineData("reserved node")]
    [InlineData("reserved installation")]
    public async Task A_container_verify_of_a_machine_not_recorded_as_the_guests_binds_nothing_and_pins_no_keys(string why)
    {
        var (server, agent) = await StartContainerAsync(postgres);
        await using var _ = server;
        var verify = await ContainerVerifyAsync(agent);
        await GuestAsync(server, guest =>
        {
            switch (why)
            {
                case "not applied": guest.ReservationAppliedAt = null; break;
                case "reserved vmid": guest.ReservedVmid = 311; break;
                case "reserved node": guest.ReservedNode = "dromund"; break;
                default: guest.ReservedInstallation = "home/dromund"; break;
            }
        });
        var actual = await EffectPins.DeliverWithKeysAsync(server, agent, verify, Outcome.Succeeded, Verified(Ed25519));
        if (why == "not applied")
            EffectPins.Check("""
                taken: True
                run: succeeded | finished True | -
                step plan: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's container, or change it in place, so its protection stays as it is.
                step apply: succeeded | finished True | outputs True | attempts succeeded | -
                step verify: succeeded | finished True | outputs True | attempts succeeded | container 310 on korriban is not recorded as this guest's: no apply of an approved plan of this guest has created it.
                configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
                access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
                resource: checked True | applied True | verified True | bound - | outputs True
                rows: none
                operation: provision
                guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
                guest: bound - - at False | reserved home/korriban korriban 310 at True applied False
                guest: retired False | attached False by - | detached False | waiting -
                container host keys: none
                """, actual);
        else if (why == "reserved vmid")
            EffectPins.Check("""
                taken: True
                run: succeeded | finished True | -
                step plan: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's container, or change it in place, so its protection stays as it is.
                step apply: succeeded | finished True | outputs True | attempts succeeded | -
                step verify: succeeded | finished True | outputs True | attempts succeeded | container 310 on korriban is not recorded as this guest's: no apply of an approved plan of this guest has created it.
                configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
                access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
                resource: checked True | applied True | verified True | bound - | outputs True
                rows: none
                operation: provision
                guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
                guest: bound - - at False | reserved home/korriban korriban 311 at True applied True
                guest: retired False | attached False by - | detached False | waiting -
                container host keys: none
                """, actual);
        else if (why == "reserved node")
            EffectPins.Check("""
                taken: True
                run: succeeded | finished True | -
                step plan: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's container, or change it in place, so its protection stays as it is.
                step apply: succeeded | finished True | outputs True | attempts succeeded | -
                step verify: succeeded | finished True | outputs True | attempts succeeded | container 310 on korriban is not recorded as this guest's: no apply of an approved plan of this guest has created it.
                configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
                access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
                resource: checked True | applied True | verified True | bound - | outputs True
                rows: none
                operation: provision
                guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
                guest: bound - - at False | reserved home/korriban dromund 310 at True applied True
                guest: retired False | attached False by - | detached False | waiting -
                container host keys: none
                """, actual);
        else
            EffectPins.Check("""
                taken: True
                run: succeeded | finished True | -
                step plan: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's container, or change it in place, so its protection stays as it is.
                step apply: succeeded | finished True | outputs True | attempts succeeded | -
                step verify: succeeded | finished True | outputs True | attempts succeeded | container 310 on korriban is not recorded as this guest's: no apply of an approved plan of this guest has created it.
                configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
                access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
                resource: checked True | applied True | verified True | bound - | outputs True
                rows: none
                operation: provision
                guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
                guest: bound - - at False | reserved home/dromund korriban 310 at True applied True
                guest: retired False | attached False by - | detached False | waiting -
                container host keys: none
                """, actual);
    }

    [Theory]
    [InlineData(311, "home/korriban")]
    [InlineData(310, "home/dromund")]
    public async Task A_container_verify_of_a_guest_bound_to_another_machine_keeps_its_binding_and_pins_no_keys(int vmid, string installation)
    {
        var (server, agent) = await StartContainerAsync(postgres);
        await using var _ = server;
        var verify = await ContainerVerifyAsync(agent);
        await GuestAsync(server, guest => (guest.BoundVmid, guest.BoundInstallation) = (vmid, installation));
        var actual = await EffectPins.DeliverWithKeysAsync(server, agent, verify, Outcome.Succeeded, Verified(Ed25519));
        if (vmid == 311)
            EffectPins.Check("""
                taken: True
                run: succeeded | finished True | -
                step plan: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's container, or change it in place, so its protection stays as it is.
                step apply: succeeded | finished True | outputs True | attempts succeeded | -
                step verify: succeeded | finished True | outputs True | attempts succeeded | -
                configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
                access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
                resource: checked True | applied True | verified True | bound 311 | outputs True
                rows: none
                operation: provision
                guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
                guest: bound home/korriban 311 at False | reserved home/korriban korriban 310 at True applied True
                guest: retired False | attached False by - | detached False | waiting -
                container host keys: none
                """, actual);
        else
            EffectPins.Check("""
                taken: True
                run: succeeded | finished True | -
                step plan: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's container, or change it in place, so its protection stays as it is.
                step apply: succeeded | finished True | outputs True | attempts succeeded | -
                step verify: succeeded | finished True | outputs True | attempts succeeded | -
                configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
                access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
                resource: checked True | applied True | verified True | bound 310 | outputs True
                rows: none
                operation: provision
                guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
                guest: bound home/dromund 310 at False | reserved home/korriban korriban 310 at True applied True
                guest: retired False | attached False by - | detached False | waiting -
                container host keys: none
                """, actual);
    }

    [Fact]
    public async Task A_container_verify_of_an_operation_the_code_does_not_know_is_a_provision_verify()
    {
        var (server, agent) = await StartContainerAsync(postgres);
        await using var _ = server;
        var verify = await ContainerVerifyAsync(agent);
        await RenameOperationAsync(server, verify);
        EffectPins.Check("""
            taken: True
            run: succeeded | finished True | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's container, or change it in place, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: succeeded | finished True | outputs True | attempts succeeded | -
            configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
            access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
            resource: checked True | applied True | verified True | bound 310 | outputs True
            rows: store_entries +1, store_versions +1
            operation: no-such-operation
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            container host keys: current v1 | latest v1
            container host keys v1: revoked False | ed25519 SHA256:Zm9vYmFyYmF6cXV4cXV1eGNvcmdlZ3JhdWx0Z2FycA0
            """, await EffectPins.DeliverWithKeysAsync(server, agent, verify, Outcome.Succeeded, Verified(Ed25519)));
    }

    [Fact]
    public async Task A_vm_verify_pins_no_host_keys_whatever_it_reports()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        var verify = await VmVerifyAsync(server, agent);
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
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            container host keys: none
            """, await EffectPins.DeliverWithKeysAsync(server, agent, verify, Outcome.Succeeded,
            new JsonObject { ["present"] = true, ["hostKeys"] = new JsonArray(Ed25519) }));
    }

    [Fact]
    public async Task A_verify_of_a_bound_guest_planned_again_binds_nothing_new_and_moves_only_the_verified_time()
    {
        var clock = new TestClock { Now = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero) };
        await using var server = await ServerHarness.StartAsync(postgres, clock: clock);
        var agent = await server.ConnectAgentAsync("home");
        var resource = Guid.Empty;
        var verify = await VmVerifyAsync(server, agent);
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(await agent.ReportAsync(verify, Outcome.Succeeded, new JsonObject { ["present"] = true }));
        await server.TickAsync();
        resource = (await server.ResourceAsync(Guest)).Id;
        var first = await EffectPins.GuestTimesAsync(server, resource);

        clock.Now += TimeSpan.FromSeconds(5);
        await server.Services.GetRequiredService<Planner>().PlanGuestAsync(resource, CancellationToken.None);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        var again = await agent.NextAsync("verify-guest");
        clock.Now += TimeSpan.FromSeconds(5);
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
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {}
            guest: bound home/korriban 310 at True | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            """, await EffectPins.DeliverWithGuestAsync(server, agent, again, Outcome.Succeeded, new JsonObject { ["present"] = true }));
        EffectPins.Check("""
            checked 2026-11-17T09:00:00.0000000+00:00 | applied 2026-11-17T09:00:00.0000000+00:00 | verified 2026-11-17T09:00:01.0000000+00:00 | bound 2026-11-17T09:00:01.0000000+00:00 | reservation applied 2026-11-17T09:00:00.0000000+00:00 | retired - | attached -
            checked 2026-11-17T09:00:06.0000000+00:00 | applied 2026-11-17T09:00:06.0000000+00:00 | verified 2026-11-17T09:00:11.0000000+00:00 | bound 2026-11-17T09:00:01.0000000+00:00 | reservation applied 2026-11-17T09:00:00.0000000+00:00 | retired - | attached -
            """, first + "\n" + await EffectPins.GuestTimesAsync(server, resource));
    }

    [Fact]
    public async Task A_second_delivery_of_a_container_verify_leaves_the_times_and_the_pin_the_first_one_set()
    {
        var clock = new TestClock { Now = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero) };
        var (server, agent) = await StartContainerAsync(postgres, clock);
        await using var _ = server;
        var verify = await ContainerVerifyAsync(agent);
        var resource = (await server.ResourceAsync(Guest)).Id;
        var run = (await server.LatestRunAsync(Guest, Operations.Provision)).Id;

        clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(await agent.ReportAsync(verify, Outcome.Succeeded, Verified(Ed25519)));
        await server.TickAsync();
        var first = await EffectPins.GuestTimesAsync(server, resource) + "\n" + await EffectPins.KeysAsync(server, run);
        clock.Now += TimeSpan.FromSeconds(5);
        await agent.ReportAsync(verify, Outcome.Succeeded, Verified(Rsa));
        await server.TickAsync();
        Assert.Equal(first, await EffectPins.GuestTimesAsync(server, resource) + "\n" + await EffectPins.KeysAsync(server, run));
        EffectPins.Check("""
            checked 2026-11-17T09:00:00.0000000+00:00 | applied 2026-11-17T09:00:00.0000000+00:00 | verified 2026-11-17T09:00:01.0000000+00:00 | bound 2026-11-17T09:00:01.0000000+00:00 | reservation applied 2026-11-17T09:00:00.0000000+00:00 | retired - | attached -
            container host keys: current v1 | latest v1
            container host keys v1: revoked False | ed25519 SHA256:Zm9vYmFyYmF6cXV4cXV1eGNvcmdlZ3JhdWx0Z2FycA0
            """, first);
    }
}
