using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Api;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>
/// A guest declared <c>machine: attached</c> names a VM Octopushy did not create. A person attaches it: verified, protected,
/// then bound. Octopushy never plans, replaces or deletes it, and retiring it or deleting its folder detaches it.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AttachTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string Installation = "home/korriban";
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    private const int Vmid = 310;

    [Fact]
    public async Task A_person_attaches_an_existing_vm_which_is_verified_protected_and_bound_and_never_planned()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await ObserveAsync(server, (Vmid, "legacy-app", "korriban"));
        // A sync that sees the VM at the guest's VMID: an attached guest names a VM it does not own, on purpose.
        server.Repository.Write(GuestFile, File.ReadAllText(Path.Combine(server.Repository.Root, GuestFile)) + "description: The legacy app.\n");
        server.Commit("Describe it");
        await server.SyncAndTrustAsync();

        // It is valid, and nothing is planned for it.
        var resource = await server.ResourceAsync(Guest);
        Assert.True(resource.Valid, resource.Issues);
        Assert.Equal("Waiting to be attached: use Attach on the guest's page.", resource.ProvisionWaiting);
        Assert.Empty(agent.TakeAssignments());
        var planning = await Assert.ThrowsAsync<RunException>(() => server.Services.GetRequiredService<Planner>().PlanGuestAsync(resource.Id, CancellationToken.None));
        Assert.Contains("never plans its machine", planning.Message);

        var runId = await server.Services.GetRequiredService<Planner>().AttachGuestAsync(resource.Id, "ops", CancellationToken.None);
        var verify = await agent.NextAsync("verify-guest");
        var input = JsonNode.Parse(verify.InputJson)!;
        Assert.Equal((Vmid, "korriban", false), (input["vmid"]!.GetValue<int>(), input["node"]!.GetValue<string>(), input["expectRunning"]!.GetValue<bool>()));
        Assert.Null(input["name"]);
        Assert.Null((await server.ResourceAsync(Guest)).BoundVmid);
        await agent.ReportAsync(verify, Outcome.Succeeded, new JsonObject { ["present"] = true, ["node"] = "korriban" });

        var protect = await agent.NextAsync("set-protection");
        Assert.True(JsonNode.Parse(protect.InputJson)!["protection"]!.GetValue<bool>());
        Assert.Equal("octopushy@pve!writer=writer-token", Assert.Single(protect.Secrets).Value);
        await agent.ReportAsync(protect, Outcome.Succeeded, new JsonObject { ["absent"] = false, ["previous"] = false, ["protection"] = true });

        var run = await server.QueryAsync(db => db.Runs.Include(r => r.Steps).AsNoTracking().SingleAsync(r => r.Id == runId));
        Assert.Equal((Operations.Attach, RunStatuses.Succeeded), (run.Operation, run.Status));
        Assert.Equal("ops", run.Steps.Single(s => s.Name == StepNames.Approval).DecidedBy);
        resource = await server.ResourceAsync(Guest);
        Assert.Equal((Vmid, Installation, "ops"), (resource.BoundVmid, resource.BoundInstallation, resource.AttachedBy));
        Assert.Equal(GuestStatuses.InSync, Views.GuestStatus(resource, null, null).Status);
        Assert.Equal(0, await server.QueryAsync(db => db.Runs.CountAsync(r => r.ResourceId == resource.Id && r.Operation == Operations.Provision)));

        // Its folder deleted, it is detached: no retire run, nothing sent to Proxmox.
        server.Repository.Delete("labs/home/proxmox/korriban/guests/krb-app-01");
        server.Commit("Stop managing the legacy app");
        await server.SyncAndTrustAsync();
        resource = await server.ResourceAsync(Guest);
        Assert.Equal(("repository", (int?)null), (resource.DetachedBy, resource.BoundVmid));
        Assert.Equal(0, await server.QueryAsync(db => db.Runs.CountAsync(r => r.ResourceId == resource.Id && r.Operation == Operations.Retire)));
        Assert.Empty(agent.TakeAssignments());
    }

    [Fact]
    public async Task Retiring_an_attached_guest_detaches_it()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await ObserveAsync(server, (Vmid, "legacy-app", "korriban"));
        await AttachAsync(server, agent);

        server.Repository.Write(GuestFile, File.ReadAllText(Path.Combine(server.Repository.Root, GuestFile)) + "lifecycle: retired\n");
        server.Commit("Retire");
        await server.SyncAndTrustAsync();

        var resource = await server.ResourceAsync(Guest);
        Assert.NotNull(resource.DetachedAt);
        Assert.Equal(0, await server.QueryAsync(db => db.Runs.CountAsync(r => r.ResourceId == resource.Id && r.Operation == Operations.Retire)));
        Assert.Empty(agent.TakeAssignments());
    }

    [Fact]
    public async Task A_machine_that_fails_its_verify_is_neither_protected_nor_attached()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await ObserveAsync(server, (Vmid, "legacy-app", "korriban"));
        var resource = await server.ResourceAsync(Guest);
        var runId = await server.Services.GetRequiredService<Planner>().AttachGuestAsync(resource.Id, "ops", CancellationToken.None);

        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Failed, error: "VM 310 is on dromund, not korriban");
        await server.TickAsync();

        Assert.Equal(RunStatuses.Failed, (await server.QueryAsync(db => db.Runs.AsNoTracking().SingleAsync(r => r.Id == runId))).Status);
        Assert.Equal(0, await server.QueryAsync(db => db.Attempts.CountAsync(a => a.Action == "set-protection")));
        Assert.Null((await server.ResourceAsync(Guest)).BoundVmid);
    }

    [Fact]
    public async Task No_privileged_token_goes_to_an_attach_whose_vmid_became_another_guests_after_its_verify()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await ObserveAsync(server, (Vmid, "legacy-app", "korriban"));
        var resource = await server.ResourceAsync(Guest);
        await server.Services.GetRequiredService<Planner>().AttachGuestAsync(resource.Id, "ops", CancellationToken.None);
        var verify = await agent.NextAsync("verify-guest");
        await server.WithDbAsync(async db =>
        {
            db.Resources.Add(new Resource
            {
                Id = Guid.CreateVersion7(), Address = $"{Installation}/someone", Kind = ResourceKinds.Guest, Lab = "home", ParentAddress = Installation,
                BoundInstallation = Installation, BoundVmid = Vmid, BoundAt = DateTimeOffset.UtcNow, FirstSeenAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        await agent.ReportAsync(verify, Outcome.Succeeded, new JsonObject { ["present"] = true });

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.NextAsync("set-protection"));
        Assert.Null((await server.ResourceAsync(Guest)).BoundVmid);
        Assert.False(await server.QueryAsync(db => db.SecretResolutions.AnyAsync(s =>
            db.Attempts.Any(a => a.Id == s.AttemptId && a.Action == "set-protection"))));
    }

    [Fact]
    public async Task Run_again_does_not_plan_a_guest_that_became_attached()
    {
        var repository = TestRepository.Minimal();
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Failed, error: "the provider failed");
        await server.TickAsync();
        var failed = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, failed.Status);

        server.Repository.Write(GuestFile, File.ReadAllText(Path.Combine(server.Repository.Root, GuestFile)).Replace("type: vm\n", "type: vm\nmachine: attached\n"));
        server.Commit("It exists already");
        await server.SyncAndTrustAsync();

        var refused = await Assert.ThrowsAsync<RunException>(() => server.Services.GetRequiredService<Planner>().RunAgainAsync(failed.Id, CancellationToken.None));
        Assert.Contains("never plans its machine", refused.Message);
    }

    [Fact]
    public async Task Nothing_is_attached_that_is_not_observed_or_is_another_guests()
    {
        await using var server = await StartAsync();
        await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        var planner = server.Services.GetRequiredService<Planner>();
        var resource = await server.ResourceAsync(Guest);

        var unseen = await Assert.ThrowsAsync<RunException>(() => planner.AttachGuestAsync(resource.Id, "ops", CancellationToken.None));
        Assert.Contains("has not been observed", unseen.Message);
        await ObserveAsync(server, (311, "other", "korriban"));
        var absent = await Assert.ThrowsAsync<RunException>(() => planner.AttachGuestAsync(resource.Id, "ops", CancellationToken.None));
        Assert.Contains("shows no machine at VMID 310", absent.Message);

        await ObserveAsync(server, (Vmid, "legacy-app", "korriban"));
        await server.WithDbAsync(async db =>
        {
            var other = await db.Resources.FirstAsync(r => r.Address == Installation);
            db.Resources.Add(new Resource
            {
                Id = Guid.CreateVersion7(), Address = $"{Installation}/someone", Kind = ResourceKinds.Guest, Lab = "home", ParentAddress = Installation,
                BoundInstallation = Installation, BoundVmid = Vmid, BoundAt = DateTimeOffset.UtcNow, FirstSeenAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        var held = await Assert.ThrowsAsync<RunException>(() => planner.AttachGuestAsync(resource.Id, "ops", CancellationToken.None));
        Assert.Contains("home/korriban/someone's", held.Message);
    }

    [Fact]
    public async Task A_guest_that_was_attached_does_not_become_a_managed_one()
    {
        await using var server = await StartAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await ObserveAsync(server, (Vmid, "legacy-app", "korriban"));
        await AttachAsync(server, agent);

        server.Repository.Write(GuestFile, File.ReadAllText(Path.Combine(server.Repository.Root, GuestFile)).Replace("machine: attached\n", ""));
        server.Commit("Manage it with OpenTofu");
        await server.SyncAndTrustAsync();

        var resource = await server.ResourceAsync(Guest);
        Assert.False(resource.Valid);
        Assert.Contains("stays machine: attached", resource.Issues);
        Assert.Empty(agent.TakeAssignments());
    }

    [Fact]
    public async Task A_guest_that_becomes_attached_loses_the_plan_proposed_while_it_was_managed()
    {
        var repository = TestRepository.Minimal();
        repository.Write("labs/home/lab.yaml", "schemaVersion: 1\nexecution: { agentLabels: [home] }\npolicy: { approval: manual }\n");
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();
        Assert.Equal(RunStatuses.AwaitingApproval, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);

        server.Repository.Write(GuestFile, File.ReadAllText(Path.Combine(server.Repository.Root, GuestFile)).Replace("type: vm\n", "type: vm\nmachine: attached\n"));
        server.Commit("It exists already");
        await server.SyncAndTrustAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Cancelled, run.Status);
        Assert.Contains("has no provision runs", run.Detail);
        Assert.Empty(agent.TakeAssignments());
    }

    private async Task<ServerHarness> StartAsync()
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
        return await ServerHarness.StartAsync(postgres, repository);
    }

    private static async Task AttachAsync(ServerHarness server, FakeAgent agent)
    {
        var resource = await server.ResourceAsync(Guest);
        await server.Services.GetRequiredService<Planner>().AttachGuestAsync(resource.Id, "ops", CancellationToken.None);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject { ["present"] = true });
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded,
            new JsonObject { ["absent"] = false, ["previous"] = false, ["protection"] = true });
        Assert.NotNull((await server.ResourceAsync(Guest)).AttachedAt);
    }

    /// <summary>Records a complete observation of korriban that shows these VMs.</summary>
    private static Task ObserveAsync(ServerHarness server, params (int Vmid, string Name, string Host)[] vms) =>
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
                    ["guests"] = new JsonArray(vms.Select(vm => (JsonNode)new JsonObject
                    {
                        ["vmid"] = vm.Vmid, ["name"] = vm.Name, ["host"] = vm.Host, ["status"] = "running", ["kind"] = "qemu",
                    }).ToArray()),
                }.ToJsonString(),
            });
            await db.SaveChangesAsync();
        });
}
