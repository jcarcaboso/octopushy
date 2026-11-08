using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;
using Repository = Octopushy.Tests.Definitions.GuestConfigurationTests;

namespace Octopushy.Tests.Server;

/// <summary>
/// A Proxmox container is a guest like a VM: the same provision run, protection, ownership and safety boundary, through the
/// <c>lxc</c> endpoints. Its host keys come from its create task, and its Ansible work is pinned to them.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ContainerGuestTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    private const string WorkflowFile = "workflows/app/workflow.yaml";
    private const int Vmid = 310;
    private const string Ed25519 = "ed25519 SHA256:Zm9vYmFyYmF6cXV4cXV1eGNvcmdlZ3JhdWx0Z2FycA0";
    private const string Rsa = "rsa SHA256:cnNhcnNhcnNhcnNhcnNhcnNhcnNhcnNhcnNhcnNhcnM";

    /// <summary>The configure fixture as a container: Octopushy's own login, and no host key named.</summary>
    private static TestRepository Container(string policy = "", bool hostKey = false)
    {
        var test = Repository.Repository(policy);
        var guest = File.ReadAllText(Path.Combine(test.Root, GuestFile))
            .Replace("type: vm", "type: container")
            .Replace("  sshKey: { store: access/app/ssh-key }\n", "");
        if (!hostKey)
            guest = guest.Replace("  hostKey: { store: access/app/host-key }\n", "");
        test.Write(GuestFile, guest);
        test.Write(WorkflowFile, File.ReadAllText(Path.Combine(test.Root, WorkflowFile)).Replace("manages: vm", "manages: container"));
        return test;
    }

    private async Task<(ServerHarness Server, FakeAgent Agent)> StartAsync(TestRepository repository, bool hostKey = false)
    {
        var server = await ServerHarness.StartAsync(postgres, repository);
        await server.SetSecretAsync("app/tailscale", "tskey-secret-value");
        await server.SetSecretAsync("app/git-key", "git-key-value");
        if (hostKey)
            await server.SetPlainAsync("access/app/host-key", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIL4yMxTo2ZC6CYBxvdtnHHtR5HBTjQeL+w9jI3rj+PyY");
        var http = server.CreateClient();
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await http.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);
        return (server, agent);
    }

    private static JsonObject Input(Assignment assignment) => JsonNode.Parse(assignment.InputJson)!.AsObject();

    /// <summary>A plan with one change to the guest's container, as the agent summarizes it.</summary>
    private static JsonObject ContainerPlan(string[] actions, string? reason = null)
    {
        var change = new JsonObject
        {
            ["address"] = $"{GuestVmPlan.ContainerResourceType}.guest",
            ["type"] = GuestVmPlan.ContainerResourceType,
            ["name"] = "guest",
            ["actions"] = new JsonArray(actions.Select(a => (JsonNode)a).ToArray()),
            ["proxmox"] = true,
            ["vmId"] = Vmid,
            ["node"] = "korriban",
            ["targets"] = new JsonArray(new JsonObject { ["vmId"] = Vmid, ["node"] = "korriban" }),
        };
        if (reason is not null)
            change["actionReason"] = reason;
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

    private static async Task<(string Value, int Version)?> PinAsync(ServerHarness server)
    {
        var resource = await server.ResourceAsync(Guest);
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<VariableStore>().GetAsync(ContainerHostKeys.Key(resource.Id), CancellationToken.None);
    }

    private static async Task ProvisionAsync(FakeAgent agent, params string[] hostKeys)
    {
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(["create"]), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, Verified(hostKeys));
    }

    [Fact]
    public async Task A_container_is_verified_through_the_lxc_endpoints_and_its_setup_is_pinned_to_its_create_tasks_host_keys()
    {
        var (server, agent) = await StartAsync(Container());
        await using var _ = server;

        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(["create"]), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        var verify = await agent.NextAsync("verify-guest");
        var input = Input(verify);
        Assert.Equal(("lxc", Vmid, "korriban"),
            (input["kind"]!.GetValue<string>(), input["vmid"]!.GetValue<int>(), input["node"]!.GetValue<string>()));
        // The creation the keys must come from: this run's apply, by the token it ran with, in the window it ran.
        var creation = input["hostKeys"]!.AsObject();
        Assert.Equal("octopushy@pve!writer", creation["createdBy"]!.GetValue<string>());
        var (startedAgo, finishedAgo) = (creation["applyStartedSecondsAgo"]!.GetValue<long>(), creation["applyFinishedSecondsAgo"]!.GetValue<long>());
        Assert.True(finishedAgo >= 0 && startedAgo >= finishedAgo && startedAgo < 600, $"{startedAgo} {finishedAgo}");
        Assert.DoesNotContain("writer-token", verify.InputJson);
        await agent.ReportAsync(verify, Outcome.Succeeded, Verified(Rsa, Ed25519));
        await server.TickAsync();

        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
        Assert.Equal(Vmid, (await server.ResourceAsync(Guest)).BoundVmid);
        var pin = await PinAsync(server);
        Assert.Equal($"{Ed25519}\n{Rsa}\n", pin!.Value.Value);

        // The setup's preview and playbook carry the fingerprints as the pin, and say so.
        await server.Services.GetRequiredService<Planner>().ScheduleConfigurationsAsync(CancellationToken.None);
        var preview = await ConfigureRunTests.PreviewAsync(agent);
        var host = Input(preview)["host"]!;
        Assert.True(host["hostKeyFingerprints"]!.GetValue<bool>());
        Assert.Equal(pin.Value.Value, host["hostKey"]!.GetValue<string>());
        var playbook = await agent.NextAsync("run");
        Assert.True(Input(playbook)["host"]!["hostKeyFingerprints"]!.GetValue<bool>());
        Assert.Equal(pin.Value.Value, Input(playbook)["host"]!["hostKey"]!.GetValue<string>());
        await agent.ReportAsync(playbook, Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Configure)).Status);
    }

    [Fact]
    public async Task A_verify_without_host_keys_pins_nothing_and_the_setup_waits_for_them()
    {
        var (server, agent) = await StartAsync(Container());
        await using var _ = server;
        await ProvisionAsync(agent);
        await server.TickAsync();
        Assert.Null(await PinAsync(server));

        await server.Services.GetRequiredService<Planner>().ScheduleConfigurationsAsync(CancellationToken.None);
        await server.TickAsync();
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Executor == "ansible");
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Contains("have not been read from its create task", run.Detail ?? string.Join(" ", run.Steps.Select(s => s.Detail)));
    }

    [Fact]
    public async Task A_run_that_did_not_create_the_container_reads_no_host_keys()
    {
        // The container exists and was made with no pin to read, as a restored one would be: an update creates nothing.
        var (server, agent) = await StartAsync(Container());
        await using var _ = server;
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(["update"]), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        var verify = await agent.NextAsync("verify-guest");
        Assert.Null(Input(verify)["hostKeys"]);
        await agent.ReportAsync(verify, Outcome.Succeeded, Verified(Ed25519));
        await server.TickAsync();
        Assert.Null(await PinAsync(server));
    }

    [Fact]
    public async Task A_named_host_key_wins_and_the_verify_reads_none()
    {
        var (server, agent) = await StartAsync(Container(hostKey: true), hostKey: true);
        await using var _ = server;
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(["create"]), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        var verify = await agent.NextAsync("verify-guest");
        Assert.Equal("lxc", Input(verify)["kind"]!.GetValue<string>());
        Assert.Null(Input(verify)["hostKeys"]);
        await agent.ReportAsync(verify, Outcome.Succeeded, Verified(Ed25519));
        await server.TickAsync();
        Assert.Null(await PinAsync(server));

        await server.Services.GetRequiredService<Planner>().ScheduleConfigurationsAsync(CancellationToken.None);
        var preview = await ConfigureRunTests.PreviewAsync(agent);
        var host = Input(preview)["host"]!;
        Assert.False(host["hostKeyFingerprints"]!.GetValue<bool>());
        Assert.StartsWith("ssh-ed25519 AAAA", host["hostKey"]!.GetValue<string>());
    }

    [Fact]
    public async Task Replacing_the_guests_container_waits_for_a_person_and_lifts_its_protection_through_lxc()
    {
        var (server, agent) = await StartAsync(Container("policy: { approval: automatic, destructive: manual }"));
        await using var _ = server;
        await ProvisionAsync(agent, Ed25519);
        await server.TickAsync();
        var resource = await server.ResourceAsync(Guest);
        Assert.Equal(Vmid, resource.BoundVmid);

        await server.Services.GetRequiredService<Planner>().PlanGuestAsync(resource.Id, CancellationToken.None);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(["delete", "create"], "replace_because_cannot_update"),
            artifacts: FakeAgent.PlanArtifact("replace"));
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "set-protection");

        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        await server.Engine.ApproveAsync(run.Id, approval.Id, approval.PlanSha256!, "ops", null, CancellationToken.None);
        var unprotect = await agent.NextAsync("set-protection");
        Assert.Equal(("lxc", Vmid, false),
            (Input(unprotect)["kind"]!.GetValue<string>(), Input(unprotect)["vmid"]!.GetValue<int>(), Input(unprotect)["protection"]!.GetValue<bool>()));
        await agent.ReportAsync(unprotect, Outcome.Succeeded, new JsonObject { ["absent"] = false, ["previous"] = true, ["protection"] = false });
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, Verified(Rsa));
        await server.TickAsync();

        // The new container has new host keys: a new version of the pin.
        var pin = await PinAsync(server);
        Assert.Equal(($"{Rsa}\n", 2), (pin!.Value.Value, pin.Value.Version));
    }

    private static JsonObject Lifted => new() { ["absent"] = false, ["previous"] = true, ["protection"] = false };

    private static JsonObject Restored => new() { ["absent"] = false, ["previous"] = false, ["protection"] = true };

    /// <summary>Provisions and binds the guest's container, then plans it again to an update in place.</summary>
    private static async Task PlanUpdateAsync(ServerHarness server, FakeAgent agent)
    {
        await ProvisionAsync(agent, Ed25519);
        await server.TickAsync();
        var resource = await server.ResourceAsync(Guest);
        Assert.Equal(Vmid, resource.BoundVmid);
        await server.Services.GetRequiredService<Planner>().PlanGuestAsync(resource.Id, CancellationToken.None);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(["update"]), artifacts: FakeAgent.PlanArtifact("grow"));
    }

    [Fact]
    public async Task Growing_the_guests_container_lifts_its_protection_and_puts_it_back_after_the_apply()
    {
        // Proxmox: "can't update CT 500 drive 'rootfs' - protection mode enabled", for a root disk that only grows.
        var (server, agent) = await StartAsync(Container("policy: { approval: automatic, destructive: manual }"));
        await using var _ = server;
        await PlanUpdateAsync(server, agent);

        var unprotect = await agent.NextAsync("set-protection");
        Assert.Equal(("lxc", Vmid, false),
            (Input(unprotect)["kind"]!.GetValue<string>(), Input(unprotect)["vmid"]!.GetValue<int>(), Input(unprotect)["protection"]!.GetValue<bool>()));
        await agent.ReportAsync(unprotect, Outcome.Succeeded, Lifted);
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, Verified(Ed25519));
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Running, run.Status);
        var reprotect = await agent.NextAsync("set-protection");
        Assert.Equal(("lxc", Vmid, true),
            (Input(reprotect)["kind"]!.GetValue<string>(), Input(reprotect)["vmid"]!.GetValue<int>(), Input(reprotect)["protection"]!.GetValue<bool>()));
        await agent.ReportAsync(reprotect, Outcome.Succeeded, Restored);
        await server.TickAsync();

        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal(StepStatuses.Succeeded, run.Steps.Single(s => s.Name == StepNames.Reprotect).Status);
    }

    [Fact]
    public async Task A_failed_update_of_the_guests_container_puts_its_protection_back()
    {
        var (server, agent) = await StartAsync(Container("policy: { approval: automatic, destructive: manual }"));
        await using var _ = server;
        await PlanUpdateAsync(server, agent);
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded, Lifted);
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Failed, error: "boom");
        await server.TickAsync();

        var reprotect = await agent.NextAsync("set-protection");
        Assert.True(Input(reprotect)["protection"]!.GetValue<bool>());
        await agent.ReportAsync(reprotect, Outcome.Succeeded, Restored);
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("Protection of container 310 is back on", run.Detail);
    }

    [Fact]
    public async Task Cancelling_an_update_after_its_apply_still_puts_protection_back()
    {
        var (server, agent) = await StartAsync(Container("policy: { approval: automatic, destructive: manual }"));
        await using var _ = server;
        await PlanUpdateAsync(server, agent);
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded, Lifted);
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.NextAsync("verify-guest");

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        await server.Engine.CancelAsync(run.Id, "Cancelled by ops.", CancellationToken.None);
        await server.TickAsync();
        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(StepStatuses.Succeeded, run.Steps.Single(s => s.Name == StepNames.Apply).Status);
        Assert.Equal(StepStatuses.Cancelled, run.Steps.Single(s => s.Name == StepNames.Verify).Status);

        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded, Restored);
        await server.TickAsync();
        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Cancelled, run.Status);
        Assert.Contains("is back on", run.Detail);
    }

    /// <summary>Plans the guest again, and has a person approve the plan, which replaces the container.</summary>
    private static async Task ReplaceAsync(ServerHarness server, FakeAgent agent)
    {
        var resource = await server.ResourceAsync(Guest);
        await server.Services.GetRequiredService<Planner>().PlanGuestAsync(resource.Id, CancellationToken.None);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(["delete", "create"], "replace_because_cannot_update"),
            artifacts: FakeAgent.PlanArtifact("replace"));
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        await server.Engine.ApproveAsync(run.Id, approval.Id, approval.PlanSha256!, "ops", null, CancellationToken.None);
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded, new JsonObject { ["absent"] = false, ["previous"] = true, ["protection"] = false });
    }

    private static async Task<List<(int Version, bool Revoked)>> PinVersionsAsync(ServerHarness server)
    {
        var key = ContainerHostKeys.Key((await server.ResourceAsync(Guest)).Id);
        return (await server.QueryAsync(db => db.StoreVersions.AsNoTracking().Where(v => v.Key == key).OrderBy(v => v.Version).ToListAsync()))
            .Select(v => (v.Version, v.RevokedAt is not null)).ToList();
    }

    private static string Why(Run run) => run.Detail + " " + string.Join(" ", run.Steps.Select(s => s.Detail));

    [Fact]
    public async Task Once_a_replacement_is_applied_the_old_pin_reaches_no_connection_until_the_new_container_is_verified()
    {
        var (server, agent) = await StartAsync(Container("policy: { approval: automatic, destructive: manual }"));
        await using var _ = server;
        await ProvisionAsync(agent, Ed25519);
        await server.TickAsync();
        Assert.Equal([(1, false)], await PinVersionsAsync(server));

        // The replacement's apply is sent, so the old pin goes, kept for the record. The apply makes a container with other
        // keys, and its verify fails, as for a token that cannot read the create task.
        await ReplaceAsync(server, agent);
        var apply = await agent.NextAsync("apply");
        Assert.Equal([(1, true)], await PinVersionsAsync(server));
        await agent.ReportAsync(apply, Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Failed, error: "the create task could not be listed");
        await server.TickAsync();
        Assert.Equal(RunStatuses.Failed, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);

        // Neither a rotation nor the setup gets the old pin: each waits, saying why.
        var resource = await server.ResourceAsync(Guest);
        await server.Services.GetRequiredService<Planner>().RotateAccessAsync(resource.Id, CancellationToken.None);
        await server.TickAsync();
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Executor == "ansible");
        Assert.Contains("revoked: the container may have been replaced", Why(await server.LatestRunAsync(Guest, Operations.RotateAccess)));
        await server.Engine.CancelAsync((await server.LatestRunAsync(Guest, Operations.RotateAccess)).Id, "cancelled by the test", CancellationToken.None);
        // A setup is refused before it binds anything, since the guest's provisioning has not caught up.
        await Assert.ThrowsAsync<RunException>(() => server.Services.GetRequiredService<Planner>().ConfigureGuestAsync(resource.Id, "setup", CancellationToken.None));
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Executor == "ansible");
    }

    [Fact]
    public async Task A_container_made_without_ansible_work_never_counts_as_having_octopushys_key()
    {
        // A plain container first: no Ansible work, so no key of Octopushy's goes into it when it is made.
        var test = Container();
        test.Write("workflows/test-vm/workflow.yaml", File.ReadAllText(Path.Combine(test.Root, "workflows/test-vm/workflow.yaml")).Replace("manages: vm", "manages: container"));
        var withSetup = File.ReadAllText(Path.Combine(test.Root, GuestFile));
        test.Write(GuestFile, """
            schemaVersion: 1
            type: container
            host: korriban
            vmid: 310
            hardware: { cores: 2, memory: 2048 }
            network: { bridge: vmbr0, mode: static, address: 10.1.0.31/24, gateway: 10.1.0.1 }
            workflow: test-vm@1
            """);
        var (server, agent) = await StartAsync(test);
        await using var _ = server;
        await ProvisionAsync(agent);
        await server.TickAsync();
        Assert.False(await server.QueryAsync(db => db.GuestAccessKeys.AnyAsync()));

        // It moves to a workflow with a setup: Octopushy issues its key and plans, and the module, which ignores keys on a
        // container that exists, changes nothing that delivers it. An apply of that plan must not count as delivering it.
        server.Repository.Write(GuestFile, withSetup);
        server.Commit("Set the container up");
        await server.SyncAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(["update"]), artifacts: FakeAgent.PlanArtifact("update"));
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded, Lifted);
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, Verified());
        await server.TickAsync();
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded, Restored);
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
        var key = await server.QueryAsync(db => db.GuestAccessKeys.AsNoTracking().SingleAsync());
        Assert.Null(key.ProvisionedAt);

        var refused = await Assert.ThrowsAsync<RunException>(() =>
            server.Services.GetRequiredService<Planner>().ConfigureGuestAsync(key.ResourceId, "setup", CancellationToken.None));
        Assert.Contains("A container takes the key only when it is created", refused.Message);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Executor == "ansible");
    }

    [Fact]
    public async Task Host_keys_a_verify_reports_unasked_pin_nothing()
    {
        var (server, agent) = await StartAsync(Container());
        await using var _ = server;
        await ProvisionAsync(agent, Ed25519);
        await server.TickAsync();

        // An update creates nothing: the verify is asked for no keys, and keys it reports anyway are not this guest's pin.
        await server.Services.GetRequiredService<Planner>().PlanGuestAsync((await server.ResourceAsync(Guest)).Id, CancellationToken.None);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(["update"]), artifacts: FakeAgent.PlanArtifact("update"));
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded, Lifted);
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        var verify = await agent.NextAsync("verify-guest");
        Assert.Null(Input(verify)["hostKeys"]);
        await agent.ReportAsync(verify, Outcome.Succeeded, Verified(Rsa));
        await server.TickAsync();
        Assert.Equal(($"{Ed25519}\n", 1), (await PinAsync(server))!.Value);
        Assert.Equal([(1, false)], await PinVersionsAsync(server));
    }

    [Fact]
    public async Task A_container_plan_that_deletes_another_machine_is_refused()
    {
        var (server, agent) = await StartAsync(Container());
        await using var _ = server;
        var plan = ContainerPlan(["create"]);
        plan["changes"]!.AsArray().Add(new JsonObject
        {
            ["address"] = $"{GuestVmPlan.ContainerResourceType}.other",
            ["type"] = GuestVmPlan.ContainerResourceType,
            ["name"] = "other",
            ["actions"] = new JsonArray("delete"),
            ["proxmox"] = true,
            ["vmId"] = 100,
            ["node"] = "korriban",
            ["targets"] = new JsonArray(new JsonObject { ["vmId"] = 100, ["node"] = "korriban" }),
        });
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, plan, artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("not this guest's container 310", run.Steps.Single(s => s.Name == StepNames.Plan).Detail);
    }

    [Fact]
    public async Task A_vm_is_verified_as_qemu_and_reads_no_host_keys()
    {
        await using var server = await ServerHarness.StartAsync(postgres, TestRepository.Minimal());
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        var verify = await agent.NextAsync("verify-guest");
        Assert.Equal("qemu", Input(verify)["kind"]!.GetValue<string>());
        Assert.Null(Input(verify)["hostKeys"]);
        Assert.Equal("qemu", RunContext.Parse((await server.LatestRunAsync(Guest, Operations.Provision)).Context).Proxmox.Kind);
    }

    [Fact]
    public async Task Retiring_a_container_removes_its_pin()
    {
        var (server, agent) = await StartAsync(Container());
        await using var _ = server;
        await ProvisionAsync(agent, Ed25519);
        await server.TickAsync();
        Assert.NotNull(await PinAsync(server));
        var resourceId = (await server.ResourceAsync(Guest)).Id;

        server.Repository.Write(GuestFile, File.ReadAllText(Path.Combine(server.Repository.Root, GuestFile)) + "lifecycle: retired\n");
        server.Commit("Retire the container");
        await server.SyncAsync();
        var retire = ContainerPlan(["delete"]);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, retire, artifacts: FakeAgent.PlanArtifact("retire"));
        var run = await server.LatestRunAsync(Guest, Operations.Retire);
        if (run.Status == RunStatuses.AwaitingApproval && run.Steps.Single(s => s.Name == StepNames.Approval) is var approval)
            await server.Engine.ApproveAsync(run.Id, approval.Id, approval.PlanSha256!, "ops", null, CancellationToken.None);
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded, new JsonObject { ["absent"] = false, ["previous"] = true, ["protection"] = false });
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        var absent = await agent.NextAsync("verify-absent");
        Assert.Null(Input(absent)["hostKeys"]);
        await agent.ReportAsync(absent, Outcome.Succeeded, new JsonObject { ["present"] = false });
        await server.TickAsync();

        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Retire)).Status);
        Assert.False(await server.QueryAsync(db => db.StoreEntries.AnyAsync(e => e.Key == ContainerHostKeys.Key(resourceId))));
    }

    [Fact]
    public async Task Review_rejected_replacement_keeps_the_old_pin()
    {
        var (server, agent) = await StartAsync(Container("policy: { approval: automatic, destructive: manual }"));
        await using var _ = server;
        await ProvisionAsync(agent, Ed25519);
        await server.TickAsync();
        await server.Services.GetRequiredService<Planner>().PlanGuestAsync((await server.ResourceAsync(Guest)).Id, CancellationToken.None);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(["delete", "create"], "replace_because_cannot_update"), artifacts: FakeAgent.PlanArtifact("replace"));
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        await server.Engine.RejectAsync(run.Id, run.Steps.Single(s => s.Name == StepNames.Approval).Id, "ops", null, CancellationToken.None);
        await server.TickAsync();
        Assert.Equal([(1, false)], await PinVersionsAsync(server));
        Assert.NotNull(await PinAsync(server));
    }

    [Fact]
    public async Task Review_revoked_pin_stays_unusable_after_restart()
    {
        var (server, agent) = await StartAsync(Container("policy: { approval: automatic, destructive: manual }"));
        await using var _ = server;
        await ProvisionAsync(agent, Ed25519);
        await server.TickAsync();
        await ReplaceAsync(server, agent);
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Failed, error: "temporary API error");
        await server.TickAsync();
        await server.RestartFixtureAsync();
        agent = await server.ConnectAgentAsync("home");
        await server.Services.GetRequiredService<Planner>().RotateAccessAsync((await server.ResourceAsync(Guest)).Id, CancellationToken.None);
        await server.TickAsync();
        Assert.Equal([(1, true)], await PinVersionsAsync(server));
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Executor == "ansible");
        Assert.Contains("revoked: the container may have been replaced", Why(await server.LatestRunAsync(Guest, Operations.RotateAccess)));
    }

    /// <summary>Replaces the container, whose verify then fails as for an API error, and plans the guest again: no changes.</summary>
    private static async Task<Assignment> ReplanAfterAFailedVerifyAsync(ServerHarness server, FakeAgent agent)
    {
        await ProvisionAsync(agent, Ed25519);
        await server.TickAsync();
        await ReplaceAsync(server, agent);
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Failed, error: "temporary API error");
        await server.TickAsync();
        var replaced = await server.LatestRunAsync(Guest, Operations.Provision);
        await server.Services.GetRequiredService<Planner>().PlanGuestAsync((await server.ResourceAsync(Guest)).Id, CancellationToken.None);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(), artifacts: FakeAgent.PlanArtifact("no changes"));
        var verify = await agent.NextAsync("verify-guest");
        var request = Input(verify)["hostKeys"]!.AsObject();
        // It asks about the replacement's creation, by the token that applied it, and does not fail for it: nothing is created now.
        Assert.Equal(("octopushy@pve!writer", true), (request["createdBy"]!.GetValue<string>(), request["optional"]!.GetValue<bool>()));
        Assert.Equal(replaced.Id, RunContext.Parse((await server.LatestRunAsync(Guest, Operations.Provision)).Context).HostKeysRequest!.CreatedBy);
        return verify;
    }

    [Fact]
    public async Task Review_no_op_replan_recovers_a_pin_after_transient_verify_failure()
    {
        var (server, agent) = await StartAsync(Container("policy: { approval: automatic, destructive: manual }"));
        await using var _ = server;
        var verify = await ReplanAfterAFailedVerifyAsync(server, agent);
        await agent.ReportAsync(verify, Outcome.Succeeded, Verified(Rsa));
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
        // A new version, from the replacement's creation; the old one is never revived.
        Assert.Equal(($"{Rsa}\n", 2), (await PinAsync(server))!.Value);
        Assert.Equal([(1, true), (2, false)], await PinVersionsAsync(server));
    }

    [Fact]
    public async Task A_replan_that_cannot_vouch_again_says_planning_will_not_fix_it()
    {
        var (server, agent) = await StartAsync(Container("policy: { approval: automatic, destructive: manual }"));
        await using var _ = server;
        var verify = await ReplanAfterAFailedVerifyAsync(server, agent);
        var outputs = Verified();
        outputs.Remove("hostKeys");
        outputs["hostKeysError"] = "after the create task …, Proxmox lists vzrestore … for VMID 310";
        await agent.ReportAsync(verify, Outcome.Succeeded, outputs);
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Contains("could not be read again", run.Steps.Single(s => s.Name == StepNames.Verify).Detail);
        Assert.Equal([(1, true)], await PinVersionsAsync(server));

        await server.Services.GetRequiredService<Planner>().RotateAccessAsync((await server.ResourceAsync(Guest)).Id, CancellationToken.None);
        await server.TickAsync();
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Executor == "ansible");
        var why = Why(await server.LatestRunAsync(Guest, Operations.RotateAccess));
        Assert.Contains("planning again will not fix it: set access.hostKey, or rebuild the container", why);
    }

    [Fact]
    public async Task Review_unrequested_keys_after_a_create_pin_nothing_when_the_apply_token_was_revoked()
    {
        var (server, agent) = await StartAsync(Container());
        await using var _ = server;
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(["create"]), artifacts: FakeAgent.PlanArtifact());
        var apply = await agent.NextAsync("apply");
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        var context = RunContext.Parse(run.Context);
        var key = context.Secrets[SecretNames.ProxmoxPrivilegedToken];
        var version = context.SecretVersions[SecretNames.ProxmoxPrivilegedToken];
        await using (var scope = server.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<VariableStore>().RevokeAsync(key, version, "retired after apply", "ops", CancellationToken.None);
        await agent.ReportAsync(apply, Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });

        // What the apply was sent with was kept when it was sent, so the creation can still be asked about.
        var verify = await agent.NextAsync("verify-guest");
        Assert.Equal("octopushy@pve!writer", Input(verify)["hostKeys"]!["createdBy"]!.GetValue<string>());

        // Only the attempt that was asked is heard: here the request names another attempt, so the keys pin nothing.
        run = await server.LatestRunAsync(Guest, Operations.Provision);
        var asked = RunContext.Parse(run.Context);
        Assert.Equal(Guid.Parse(verify.AttemptId), asked.HostKeysRequest!.AttemptId);
        await server.WithDbAsync(async db =>
        {
            var stored = await db.Runs.SingleAsync(r => r.Id == run.Id);
            stored.Context = (asked with { HostKeysRequest = asked.HostKeysRequest with { AttemptId = Guid.NewGuid() } }).Serialize();
            await db.SaveChangesAsync();
        });
        await agent.ReportAsync(verify, Outcome.Succeeded, Verified(Ed25519));
        await server.TickAsync();
        Assert.Null(await PinAsync(server));
    }

    [Fact]
    public async Task The_creating_verify_pins_even_after_the_apply_token_is_revoked()
    {
        var (server, agent) = await StartAsync(Container());
        await using var _ = server;
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(["create"]), artifacts: FakeAgent.PlanArtifact());
        var apply = await agent.NextAsync("apply");
        var context = RunContext.Parse((await server.LatestRunAsync(Guest, Operations.Provision)).Context);
        await using (var scope = server.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<VariableStore>().RevokeAsync(context.Secrets[SecretNames.ProxmoxPrivilegedToken],
                context.SecretVersions[SecretNames.ProxmoxPrivilegedToken], "retired after apply", "ops", CancellationToken.None);
        await agent.ReportAsync(apply, Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        var verify = await agent.NextAsync("verify-guest");
        Assert.False(Input(verify)["hostKeys"]!["optional"]!.GetValue<bool>());
        await agent.ReportAsync(verify, Outcome.Succeeded, Verified(Ed25519));
        await server.TickAsync();
        Assert.Equal(($"{Ed25519}\n", 1), (await PinAsync(server))!.Value);
    }

    [Fact]
    public async Task Keys_in_verify_outputs_that_cannot_be_masked_after_a_restart_pin_nothing()
    {
        var (server, agent) = await StartAsync(Container());
        await using var _ = server;
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, ContainerPlan(["create"]), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        var verify = await agent.NextAsync("verify-guest");
        Assert.NotNull(Input(verify)["hostKeys"]);

        // The server forgets what it sent, and the token the verify was given can no longer be read back to mask with.
        await server.RestartFixtureAsync();
        await server.WithDbAsync(db => db.StoreVersions.Where(v => v.Key == "proxmox/korriban/read-only-token").ExecuteDeleteAsync());
        await agent.ReportAsync(verify, Outcome.Succeeded, Verified(Ed25519));
        await server.TickAsync();

        var attempt = await server.QueryAsync(db => db.Attempts.AsNoTracking().SingleAsync(a => a.Id == Guid.Parse(verify.AttemptId)));
        Assert.Null(attempt.Outputs);
        Assert.Null(await PinAsync(server));
    }
}
