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
        Assert.Equal(("lxc", Vmid, "korriban", true),
            (input["kind"]!.GetValue<string>(), input["vmid"]!.GetValue<int>(), input["node"]!.GetValue<string>(), input["hostKeys"]!.GetValue<bool>()));
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
        Assert.Contains("have not been read from its create task yet", run.Detail ?? string.Join(" ", run.Steps.Select(s => s.Detail)));
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
}
