using System.Net.Http.Json;
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
/// A provision run's <see cref="StepNames.Apply"/>: the hashes, revision and time it marks applied and synced, the module outputs
/// it keeps, and, for a guest not bound yet, the reservation marked applied only when it is for this run's machine and the
/// approved plan made that machine: the guest is not bound here. A plan that creates or replaces something proposes the guest's
/// setup and its services' again. Octopushy's own login key is recorded as provisioned, for a container only by the apply that
/// made it, and the guest's own host key as delivered by this run. Each case is one of <see cref="ProvisioningTests"/>,
/// <see cref="ManagedHostKeyTests"/>, <see cref="ManagedAccessTests"/> or <see cref="ContainerGuestTests"/>, pinned whole with a
/// failure, a late result and a second delivery of every result.
/// </summary>
public class ProvisionApplyEffectCharacterizationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    private const string WorkflowFile = "workflows/app/workflow.yaml";
    private const string NodeKeyStore = "proxmox/korriban/ssh-host-key";
    private const string NodeKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIKiijI7PL8NeaSlRIfsOf1JSG69ZMULksy817NNUwsNZ root@korriban";

    private static readonly JsonObject Applied = new() { ["outputs"] = new JsonObject { ["vm_id"] = 310 }, ["sensitiveOutputs"] = new JsonArray() };

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

    /// <summary>A plan with one change to the guest's container, as the agent summarizes it.</summary>
    private static JsonObject ContainerPlan(string[] actions) => SafetyBoundaryFixtures.MachinePlan(
        SafetyBoundaryFixtures.Change(GuestVmPlan.ContainerResourceType, actions, 310, "korriban"));

    /// <summary>Signed in, synced and trusted, with the host's pin and the installation's upload key in place, as for a managed host key.</summary>
    private static async Task<FakeAgent> ReadyAsync(ServerHarness server)
    {
        await server.SetSecretAsync("access/app/ssh-key", "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXk\n-----END OPENSSH PRIVATE KEY-----\n");
        await server.SetPlainAsync("access/app/host-key", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIL4yMxTo2ZC6CYBxvdtnHHtR5HBTjQeL+w9jI3rj+PyY");
        await server.SetSecretAsync("app/tailscale", "tskey-secret-value");
        await server.SetSecretAsync("app/git-key", "git-key-value");
        await server.SetPlainAsync(NodeKeyStore, NodeKey);
        using var http = server.CreateClient();
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await http.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);
        var installation = await server.ResourceAsync("home/korriban");
        (await http.PostAsync($"/api/installations/{installation.Id}/snippets-key", null)).EnsureSuccessStatusCode();
        await server.TickAsync();
        return agent;
    }

    /// <summary>A new guest Octopushy holds the login key and host key of, planned with <paramref name="plan"/>; returns its apply.</summary>
    private static async Task<Assignment> ManagedApplyAsync(ServerHarness server, FakeAgent agent, JsonObject? plan = null)
    {
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, plan ?? FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        return await agent.NextAsync("apply");
    }

    /// <summary>
    /// Gives the guest's setup and a service of the guest a proposal and an applied hash, as a guest configured before would
    /// have, so that proposing them again shows.
    /// </summary>
    private static Task ConfiguredAsync(ServerHarness server, bool service = true) =>
        server.WithDbAsync(async db =>
        {
            var guest = await db.Resources.SingleAsync(r => r.Address == Guest);
            var own = await db.GuestConfigurations.SingleOrDefaultAsync(c => c.ResourceId == guest.Id);
            if (own is null)
                db.GuestConfigurations.Add(own = new GuestConfiguration { ResourceId = guest.Id });
            own.ProposedHash = "proposed";
            own.AppliedHash = "applied";
            if (service)
            {
                var web = new Resource { Id = Guid.NewGuid(), Kind = ResourceKinds.Service, Address = $"{Guest}/web", ParentAddress = Guest, Lab = "home" };
                db.Resources.Add(web);
                db.GuestConfigurations.Add(new GuestConfiguration { ResourceId = web.Id, ProposedHash = "proposed", AppliedHash = "applied" });
            }

            await db.SaveChangesAsync();
        });

    private static Task GuestAsync(ServerHarness server, Action<Resource> change) =>
        server.WithDbAsync(async db =>
        {
            change(await db.Resources.SingleAsync(r => r.Address == Guest));
            await db.SaveChangesAsync();
        });

    /// <summary>Every time the apply's records hold, exactly, to tell a second delivery that wrote them again.</summary>
    private static Task<string> TimesAsync(ServerHarness server, Guid resource) =>
        server.QueryAsync(async db =>
        {
            static string Time(DateTimeOffset? time) => time?.ToString("O") ?? "-";
            var access = await db.GuestAccessKeys.AsNoTracking().Where(k => k.ResourceId == resource).OrderBy(k => k.Version).ToListAsync();
            var host = await db.GuestHostKeys.AsNoTracking().Where(k => k.ResourceId == resource).OrderBy(k => k.Version).ToListAsync();
            return await EffectPins.GuestTimesAsync(server, resource)
                   + "\n" + string.Join(" | ", access.Select(k => $"access v{k.Version} provisioned {Time(k.ProvisionedAt)}"))
                   + "\n" + string.Join(" | ", host.Select(k => $"host v{k.Version} delivered {Time(k.DeliveredAt)} spent {Time(k.SpentAt)}"));
        });

    [Fact]
    public async Task A_provision_apply_records_what_it_made_and_a_second_delivery_changes_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres, ManagedHostKeyTests.Managed());
        var agent = await ReadyAsync(server);
        var apply = await ManagedApplyAsync(server, agent);
        await ConfiguredAsync(server);
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: running | finished False | outputs False | attempts assigned | -
            configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
            access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
            resource: checked True | applied True | verified False | bound - | outputs True
            rows: attempts +1, secret_resolutions +1
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {"vm_id": 310}
            guest: bound - - at False | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            host key v1: delivered by this run at True | spent by this run at True
            container host keys: none
            service home/korriban/krb-app-01/web: proposed - | applied -
            """, await EffectPins.DeliverWithKeysAsync(server, agent, apply, Outcome.Succeeded, Applied));
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: running | finished False | outputs False | attempts assigned | -
            configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
            access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
            resource: checked True | applied True | verified False | bound - | outputs True
            rows: none
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {"vm_id": 310}
            guest: bound - - at False | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            host key v1: delivered by this run at True | spent by this run at True
            container host keys: none
            service home/korriban/krb-app-01/web: proposed - | applied -
            """, await EffectPins.DeliverWithKeysAsync(server, agent, apply, Outcome.Succeeded, Applied));
    }

    [Fact]
    public async Task A_provision_apply_that_fails_records_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres, ManagedHostKeyTests.Managed());
        var agent = await ReadyAsync(server);
        var apply = await ManagedApplyAsync(server, agent);
        await ConfiguredAsync(server);
        EffectPins.Check("""
            taken: True
            run: failed | finished True | The apply step failed: the provider failed
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: failed | finished True | outputs False | attempts failed | the provider failed
            step verify: skipped | finished False | outputs False | attempts  | An earlier step did not succeed.
            configuration: applied another | revision - | at False | action - | snapshot - | proposed another | desired another
            access key v1: last used - at False | provisioned False | authorized False | verified False | removed False
            resource: checked True | applied False | verified False | bound - | outputs False
            rows: notifications +1
            operation: provision
            guest: checked this run's this run's | applied - - | synced - | outputs -
            guest: bound - - at False | reserved home/korriban korriban 310 at True applied False
            guest: retired False | attached False by - | detached False | waiting -
            host key v1: delivered by - at False | spent by this run at True
            container host keys: none
            service home/korriban/krb-app-01/web: proposed proposed | applied applied
            """, await EffectPins.DeliverWithKeysAsync(server, agent, apply, Outcome.Failed, null, "the provider failed"));
    }

    [Fact]
    public async Task A_provision_apply_reported_uncertain_is_settled_once_by_its_late_result()
    {
        await using var server = await ServerHarness.StartAsync(postgres, ManagedHostKeyTests.Managed());
        var agent = await ReadyAsync(server);
        var apply = await ManagedApplyAsync(server, agent);
        await ConfiguredAsync(server);
        EffectPins.Check("""
            taken: True
            run: uncertain | finished False | The state of this apply was not saved: The agent returned no state. Inspect the guest and plan again.
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: uncertain | finished True | outputs False | attempts uncertain | The state of this apply was not saved: The agent returned no state. Inspect the guest and plan again.
            step verify: pending | finished False | outputs False | attempts  | -
            configuration: applied another | revision - | at False | action - | snapshot - | proposed another | desired another
            access key v1: last used - at False | provisioned False | authorized False | verified False | removed False
            resource: checked True | applied False | verified False | bound - | outputs False
            rows: notifications +1
            operation: provision
            guest: checked this run's this run's | applied - - | synced - | outputs -
            guest: bound - - at False | reserved home/korriban korriban 310 at True applied False
            guest: retired False | attached False by - | detached False | waiting -
            host key v1: delivered by - at False | spent by this run at True
            container host keys: none
            service home/korriban/krb-app-01/web: proposed proposed | applied applied
            """, await EffectPins.DeliverWithKeysAsync(server, agent, apply, Outcome.Uncertain, null, "lost"));
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | The result arrived after the agent had stopped reporting.
            step verify: running | finished False | outputs False | attempts assigned | -
            configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
            access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
            resource: checked True | applied True | verified False | bound - | outputs True
            rows: attempts +1, secret_resolutions +1
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {"vm_id": 310}
            guest: bound - - at False | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            host key v1: delivered by this run at True | spent by this run at True
            container host keys: none
            service home/korriban/krb-app-01/web: proposed - | applied -
            """, await EffectPins.DeliverWithKeysAsync(server, agent, apply, Outcome.Succeeded, Applied));
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | The result arrived after the agent had stopped reporting.
            step verify: running | finished False | outputs False | attempts assigned | -
            configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
            access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
            resource: checked True | applied True | verified False | bound - | outputs True
            rows: none
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {"vm_id": 310}
            guest: bound - - at False | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            host key v1: delivered by this run at True | spent by this run at True
            container host keys: none
            service home/korriban/krb-app-01/web: proposed - | applied -
            """, await EffectPins.DeliverWithKeysAsync(server, agent, apply, Outcome.Succeeded, Applied));
    }

    [Fact]
    public async Task A_provision_apply_that_creates_and_replaces_nothing_proposes_nothing_again()
    {
        await using var server = await ServerHarness.StartAsync(postgres, ManagedHostKeyTests.Managed());
        var agent = await ReadyAsync(server);
        var apply = await ManagedApplyAsync(server, agent, FakeAgent.Plan(update: 1));
        await ConfiguredAsync(server);
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: running | finished False | outputs False | attempts assigned | -
            configuration: applied another | revision - | at False | action - | snapshot - | proposed another | desired another
            access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
            resource: checked True | applied True | verified False | bound - | outputs True
            rows: attempts +1, secret_resolutions +1
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {"vm_id": 310}
            guest: bound - - at False | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            host key v1: delivered by this run at True | spent by this run at True
            container host keys: none
            service home/korriban/krb-app-01/web: proposed proposed | applied applied
            """, await EffectPins.DeliverWithKeysAsync(server, agent, apply, Outcome.Succeeded, Applied));
    }

    [Fact]
    public async Task A_provision_apply_of_a_guest_without_services_proposes_only_its_setup_again()
    {
        await using var server = await ServerHarness.StartAsync(postgres, ManagedHostKeyTests.Managed());
        var agent = await ReadyAsync(server);
        var apply = await ManagedApplyAsync(server, agent);
        await ConfiguredAsync(server, service: false);
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: running | finished False | outputs False | attempts assigned | -
            configuration: applied another | revision - | at False | action - | snapshot - | proposed - | desired another
            access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
            resource: checked True | applied True | verified False | bound - | outputs True
            rows: attempts +1, secret_resolutions +1
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {"vm_id": 310}
            guest: bound - - at False | reserved home/korriban korriban 310 at True applied True
            guest: retired False | attached False by - | detached False | waiting -
            host key v1: delivered by this run at True | spent by this run at True
            container host keys: none
            """, await EffectPins.DeliverWithKeysAsync(server, agent, apply, Outcome.Succeeded, Applied));
    }

    [Fact]
    public async Task A_provision_apply_that_replaces_something_proposes_the_setup_and_the_services_again()
    {
        await using var server = await ServerHarness.StartAsync(postgres, ManagedHostKeyTests.Managed());
        var agent = await ReadyAsync(server);
        var apply = await ManagedApplyAsync(server, agent, FakeAgent.Plan(replace: 1));
        await ConfiguredAsync(server);
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: running | finished False | outputs False | attempts assigned | -
            configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
            access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
            resource: checked True | applied True | verified False | bound - | outputs True
            rows: attempts +1, secret_resolutions +1
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {"vm_id": 310}
            guest: bound - - at False | reserved - - - at False applied False
            guest: retired False | attached False by - | detached False | waiting -
            host key v1: delivered by this run at True | spent by this run at True
            container host keys: none
            service home/korriban/krb-app-01/web: proposed - | applied -
            """, await EffectPins.DeliverWithKeysAsync(server, agent, apply, Outcome.Succeeded, Applied));
    }

    [Theory]
    [InlineData("vmid")]
    [InlineData("node")]
    [InlineData("installation")]
    public async Task A_provision_apply_marks_no_reservation_applied_that_is_not_for_its_machine(string other)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var apply = await ManagedApplyAsync(server, agent);
        await GuestAsync(server, guest =>
        {
            switch (other)
            {
                case "vmid": guest.ReservedVmid = 311; break;
                case "node": guest.ReservedNode = "dromund"; break;
                default: guest.ReservedInstallation = "home/dromund"; break;
            }
        });
        var actual = await EffectPins.DeliverWithGuestAsync(server, agent, apply, Outcome.Succeeded, Applied);
        if (other == "vmid")
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
                guest: bound - - at False | reserved home/korriban korriban 311 at True applied False
                guest: retired False | attached False by - | detached False | waiting -
                """, actual);
        else if (other == "node")
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
                guest: bound - - at False | reserved home/korriban dromund 310 at True applied False
                guest: retired False | attached False by - | detached False | waiting -
                """, actual);
        else
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
                guest: bound - - at False | reserved home/dromund korriban 310 at True applied False
                guest: retired False | attached False by - | detached False | waiting -
                """, actual);
    }

    [Fact]
    public async Task A_provision_apply_of_a_guest_bound_already_marks_no_reservation_applied()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var apply = await ManagedApplyAsync(server, agent);
        await GuestAsync(server, guest => (guest.BoundVmid, guest.BoundInstallation) = (310, "home/korriban"));
        EffectPins.Check("""
            taken: True
            run: running | finished False | -
            step plan: succeeded | finished True | outputs True | attempts succeeded | -
            step approval: succeeded | finished True | outputs False | attempts  | -
            step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's VM, so its protection stays as it is.
            step apply: succeeded | finished True | outputs True | attempts succeeded | -
            step verify: running | finished False | outputs False | attempts assigned | -
            configuration: none
            resource: checked True | applied True | verified False | bound 310 | outputs True
            rows: attempts +1, secret_resolutions +1
            operation: provision
            guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {"vm_id": 310}
            guest: bound home/korriban 310 at False | reserved home/korriban korriban 310 at True applied False
            guest: retired False | attached False by - | detached False | waiting -
            """, await EffectPins.DeliverWithGuestAsync(server, agent, apply, Outcome.Succeeded, Applied));
    }

    [Fact]
    public async Task A_provision_apply_whose_plan_makes_no_machine_marks_no_reservation_applied_even_for_its_own_machine()
    {
        // A reservation for this run's machine, as an earlier run's that did not finish leaves one, and a plan that only uploads a file.
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var apply = await ManagedApplyAsync(server, agent, SafetyBoundaryFixtures.MachinePlan(
            SafetyBoundaryFixtures.Change("proxmox_virtual_environment_file", ["create"], null, null, "snippet")));
        await GuestAsync(server, guest =>
            (guest.ReservedVmid, guest.ReservedNode, guest.ReservedInstallation, guest.ReservedAt) = (310, "korriban", "home/korriban", DateTimeOffset.UtcNow));
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
            guest: bound - - at False | reserved home/korriban korriban 310 at True applied False
            guest: retired False | attached False by - | detached False | waiting -
            """, await EffectPins.DeliverWithGuestAsync(server, agent, apply, Outcome.Succeeded, Applied));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public async Task A_container_apply_provisions_octopushys_key_only_when_it_made_the_container(string action)
    {
        await using var server = await ServerHarness.StartAsync(postgres, Container());
        var agent = await ReadyAsync(server);
        var apply = await ManagedApplyAsync(server, agent, ContainerPlan([action]));
        var actual = await EffectPins.DeliverWithKeysAsync(server, agent, apply, Outcome.Succeeded, Applied);
        if (action == "create")
            EffectPins.Check("""
                taken: True
                run: running | finished False | -
                step plan: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step unprotect: skipped | finished False | outputs False | attempts  | The plan does not replace or delete this guest's container, or change it in place, so its protection stays as it is.
                step apply: succeeded | finished True | outputs True | attempts succeeded | -
                step verify: running | finished False | outputs False | attempts assigned | -
                configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
                access key v1: last used - at False | provisioned True | authorized False | verified False | removed False
                resource: checked True | applied True | verified False | bound - | outputs True
                rows: attempts +1, secret_resolutions +1
                operation: provision
                guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {"vm_id": 310}
                guest: bound - - at False | reserved home/korriban korriban 310 at True applied True
                guest: retired False | attached False by - | detached False | waiting -
                container host keys: none
                """, actual);
        else
            EffectPins.Check("""
                taken: True
                run: running | finished False | -
                step plan: succeeded | finished True | outputs True | attempts succeeded | -
                step approval: succeeded | finished True | outputs False | attempts  | -
                step unprotect: skipped | finished False | outputs False | attempts  | This guest does not own container 310 on korriban, so its protection stays as it is.
                step apply: succeeded | finished True | outputs True | attempts succeeded | -
                step verify: running | finished False | outputs False | attempts assigned | -
                configuration: applied - | revision - | at False | action - | snapshot - | proposed - | desired another
                access key v1: last used - at False | provisioned False | authorized False | verified False | removed False
                resource: checked True | applied True | verified False | bound - | outputs True
                rows: attempts +1, secret_resolutions +1
                operation: provision
                guest: checked this run's this run's | applied this run's this run's | synced this run's | outputs {"vm_id": 310}
                guest: bound - - at False | reserved - - - at False applied False
                guest: retired False | attached False by - | detached False | waiting -
                container host keys: none
                """, actual);
    }

    [Fact]
    public async Task A_second_delivery_leaves_the_times_the_first_one_set()
    {
        var clock = new TestClock { Now = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero) };
        await using var server = await ServerHarness.StartAsync(postgres, ManagedHostKeyTests.Managed(), clock: clock);
        var agent = await ReadyAsync(server);
        var apply = await ManagedApplyAsync(server, agent);
        var resource = (await server.ResourceAsync(Guest)).Id;

        clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(await agent.ReportAsync(apply, Outcome.Succeeded, Applied.DeepClone().AsObject()));
        await server.TickAsync();
        var first = await TimesAsync(server, resource);
        clock.Now += TimeSpan.FromSeconds(5);
        await agent.ReportAsync(apply, Outcome.Succeeded, Applied.DeepClone().AsObject());
        await server.TickAsync();
        Assert.Equal(first, await TimesAsync(server, resource));
        EffectPins.Check("""
            checked 2026-11-17T09:00:00.0000000+00:00 | applied 2026-11-17T09:00:01.0000000+00:00 | verified - | bound - | reservation applied 2026-11-17T09:00:01.0000000+00:00 | retired - | attached -
            access v1 provisioned 2026-11-17T09:00:01.0000000+00:00
            host v1 delivered 2026-11-17T09:00:01.0000000+00:00 spent 2026-11-17T09:00:00.0000000+00:00
            """, first);
    }
}
