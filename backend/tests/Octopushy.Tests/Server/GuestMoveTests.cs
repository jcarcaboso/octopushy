using System.Text.Json.Nodes;
using Octopushy.Agent.V1;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>A guest's machine moves between the hosts of its cluster: migrated by a plan of its own, never replaced on the way.</summary>
public class GuestMoveTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    private const int Vmid = 310;

    [Fact]
    public async Task A_bound_vm_migrates_to_its_new_host_with_an_update_a_person_approves()
    {
        // Even where everything else goes ahead on its own, a migration waits for a person.
        await using var server = await StartAsync("policy: { destructive: automatic }");
        var agent = await server.ConnectAgentAsync("home");
        await EstablishAsync(server, agent);

        MoveTo(server, "dromund");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, Plan(["update"], "korriban", "dromund"), artifacts: FakeAgent.PlanArtifact("move"));
        await server.TickAsync();

        var held = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.AwaitingApproval, held.Status);
        Assert.Contains("migrates VM 310 from korriban to dromund", held.Detail);
        Assert.Contains("Accept placement", held.Detail);
        Assert.Empty(agent.TakeAssignments());
        var approval = held.Steps.Single(s => s.Name == StepNames.Approval);
        await server.Engine.ApproveAsync(held.Id, approval.Id, approval.PlanSha256!, "ops", "move it", CancellationToken.None);

        var apply = await agent.NextAsync("apply");
        Assert.Equal("apply", apply.Action);
        await agent.ReportAsync(apply, Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        var verify = await agent.NextAsync("verify-guest");
        Assert.Equal("dromund", JsonNode.Parse(verify.InputJson)!["node"]!.GetValue<string>());
        await agent.ReportAsync(verify, Outcome.Succeeded, new JsonObject());

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal(StepStatuses.Skipped, run.Steps.Single(s => s.Name == StepNames.Unprotect).Status);
        Assert.Equal(Vmid, (await server.ResourceAsync(Guest)).BoundVmid);
    }

    [Fact]
    public async Task A_vm_moved_in_proxmox_is_not_moved_back_without_a_person()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await EstablishAsync(server, agent);

        // VM 310 was moved to dromund in Proxmox; guest.yaml still says korriban, and an unrelated edit plans again.
        var path = Path.Combine(server.Repository.Root, GuestFile);
        server.Repository.Write(GuestFile, File.ReadAllText(path).Replace("memory: 2048", "memory: 4096"));
        server.Commit("More memory");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, Plan(["update"], "dromund", "korriban"), artifacts: FakeAgent.PlanArtifact("back"));
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Contains("from dromund to korriban", run.Detail);
        Assert.Empty(agent.TakeAssignments());
    }

    [Fact]
    public async Task A_plan_that_would_move_a_vm_the_guest_is_not_bound_to_is_refused()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        MoveTo(server, "dromund");
        await server.SyncAndTrustAsync();

        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, Plan(["update"], "korriban", "dromund"), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("not this guest's", run.Detail);
        Assert.Empty(agent.TakeAssignments());
    }

    [Fact]
    public async Task Replacing_a_bound_vm_to_move_it_is_refused_with_how_to_migrate_it()
    {
        await using var server = await StartAsync("policy: { destructive: automatic }");
        var agent = await server.ConnectAgentAsync("home");
        await EstablishAsync(server, agent);

        MoveTo(server, "dromund");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, Plan(["delete", "create"], "korriban", "dromund"), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("from korriban to dromund by replacing it", run.Detail);
        Assert.Contains("migrate = true", run.Detail);
        Assert.Empty(agent.TakeAssignments());
    }

    [Fact]
    public async Task An_update_that_names_another_vmid_on_the_old_host_is_still_refused()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await EstablishAsync(server, agent);

        MoveTo(server, "dromund");
        await server.SyncAndTrustAsync();
        var plan = Plan(["update"], "korriban", "dromund");
        plan["changes"]![0]!["targets"]![0]!["vmId"] = 311;
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, plan, artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();

        Assert.Equal(RunStatuses.Failed, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
        Assert.Empty(agent.TakeAssignments());
    }

    private async Task<ServerHarness> StartAsync(string policy = "")
    {
        var repository = TestRepository.Minimal();
        repository.Write("labs/home/lab.yaml", $"schemaVersion: 1\nexecution: {{ agentLabels: [home] }}\n{policy}\n");
        repository.Write("labs/home/proxmox/korriban/installation.yaml", """
            schemaVersion: 1
            type: cluster
            connection:
              endpoint: https://10.1.0.10:8006/
              tls: { verify: false }
              readOnlyToken: { store: proxmox/korriban/read-only-token }
              privilegedToken: { store: proxmox/korriban/privileged-token }
            hosts:
              korriban: { bridges: [vmbr0] }
              dromund: { bridges: [vmbr0] }
            """);
        return await ServerHarness.StartAsync(postgres, repository);
    }

    private static void MoveTo(ServerHarness server, string host)
    {
        var path = Path.Combine(server.Repository.Root, GuestFile);
        server.Repository.Write(GuestFile, File.ReadAllText(path).Replace("host: korriban", $"host: {host}"));
        server.Commit($"Move to {host}");
    }

    private static async Task EstablishAsync(ServerHarness server, FakeAgent agent)
    {
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        Assert.Equal(Vmid, (await server.ResourceAsync(Guest)).BoundVmid);
    }

    /// <summary>The guest's VM, from <paramref name="from"/> to <paramref name="to"/>, as the agent summarizes a plan.</summary>
    private static JsonObject Plan(string[] actions, string from, string to) => new()
    {
        ["hasChanges"] = true,
        ["changes"] = new JsonArray(new JsonObject
        {
            ["address"] = $"{GuestVmPlan.VmResourceType}.guest",
            ["type"] = GuestVmPlan.VmResourceType,
            ["name"] = "guest",
            ["actions"] = new JsonArray(actions.Select(a => (JsonNode)a).ToArray()),
            ["vmId"] = Vmid,
            ["node"] = from,
            ["proxmox"] = true,
            ["targets"] = new JsonArray(
                new JsonObject { ["vmId"] = Vmid, ["node"] = from },
                new JsonObject { ["vmId"] = Vmid, ["node"] = to }),
        }),
        ["counts"] = new JsonObject
        {
            ["create"] = 0,
            ["update"] = actions is ["update"] ? 1 : 0,
            ["delete"] = 0,
            ["replace"] = actions.Length == 2 ? 1 : 0,
        },
        ["toolVersion"] = "1.13.1",
    };
}
