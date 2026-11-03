using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;
using static Octopushy.Tests.Server.SafetyBoundaryFixtures;

namespace Octopushy.Tests.Server;

/// <summary>A guest whose VMID is a VM the installation's latest observation shows, and that is not the guest's own, is blocked. See docs/safety-boundary.md.</summary>
[Collection(PostgresCollection.Name)]
public class VmidCollisionTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_guest_whose_vmid_is_an_observed_unmanaged_vm_is_invalid_and_gets_no_run()
    {
        var test = TestRepository.Minimal();
        var guestYaml = File.ReadAllText(Path.Combine(test.Root, GuestFile));
        test.Delete("labs/home/proxmox/korriban/guests");
        await using var server = await ServerHarness.StartAsync(postgres, test);
        await server.SyncAndTrustAsync();
        await ObserveAsync(server, (100, "homeassistant"));

        test.Write(GuestFile, guestYaml.Replace("vmid: 310", "vmid: 100"));
        server.Commit("Add a guest at VMID 100");
        await server.SyncAndTrustAsync();

        var guest = await server.ResourceAsync(Guest);
        Assert.False(guest.Valid);
        Assert.Contains("VMID 100 on korriban is an existing VM Octopushy does not manage (homeassistant)", guest.Issues);
        Assert.Equal(0, await server.QueryAsync(db => db.Runs.CountAsync(r => r.ResourceId == guest.Id)));
        await Assert.ThrowsAsync<RunException>(() => server.Services.GetRequiredService<Planner>().PlanGuestAsync(guest.Id, CancellationToken.None));
    }

    [Fact]
    public async Task An_observation_newer_than_the_sync_stops_the_plan_at_dispatch_and_a_manual_plan()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        // No observation yet: nothing to compare with, so the guest is valid and its run is proposed.
        await server.SyncAndTrustAsync();
        Assert.True((await server.ResourceAsync(Guest)).Valid);
        await ObserveAsync(server, (Vmid, "c-fedora-01"));

        var agent = await server.ConnectAgentAsync("home");
        await server.TickAsync();
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "preview");
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("VMID 310 on korriban is an existing VM Octopushy does not manage (c-fedora-01)", run.Detail);

        var id = (await server.ResourceAsync(Guest)).Id;
        var refused = await Assert.ThrowsAsync<RunException>(() => server.Services.GetRequiredService<Planner>().PlanGuestAsync(id, CancellationToken.None));
        Assert.Contains("VMID 310 on korriban is an existing VM Octopushy does not manage", refused.Message);
    }

    [Fact]
    public async Task The_guests_own_bound_vm_is_not_a_collision()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await EstablishAsync(server, agent);
        await ObserveAsync(server, (Vmid, "krb-app-01"), (100, "homeassistant"));

        server.Repository.Write(GuestFile, File.ReadAllText(Path.Combine(server.Repository.Root, GuestFile)).Replace("cores: 2", "cores: 4"));
        server.Commit("More cores");
        await server.SyncAndTrustAsync();
        var guest = await server.ResourceAsync(Guest);
        Assert.True(guest.Valid, guest.Issues);
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Queued, run.Status);
        Assert.Equal("preview", (await agent.NextAsync("preview")).Action);
    }

    [Fact]
    public async Task A_vm_the_guests_own_failed_apply_made_is_not_a_collision()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        // An approved plan that creates the guest's own VM: sending its apply records the claim.
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded,
            MachinePlan(Change(GuestVmPlan.VmResourceType, ["create"], Vmid, "korriban")), artifacts: FakeAgent.PlanArtifact());
        var apply = await agent.NextAsync("apply");
        var guest = await server.ResourceAsync(Guest);
        Assert.Equal((Installation, "korriban", (int?)Vmid), (guest.ClaimedInstallation, guest.ClaimedNode, guest.ClaimedVmid));
        // The VM was made, starting it was not, and nothing bound it.
        await agent.ReportAsync(apply, Outcome.Failed, error: "bridge 'lab' does not exist");
        await server.TickAsync();
        Assert.Null((await server.ResourceAsync(Guest)).BoundVmid);
        await ObserveAsync(server, (Vmid, "krb-app-01"));

        await server.Services.GetRequiredService<Planner>().PlanGuestAsync((await server.ResourceAsync(Guest)).Id, CancellationToken.None);
        Assert.Equal("preview", (await agent.NextAsync("preview")).Action);
    }

    [Theory]
    [InlineData("data", false)]
    [InlineData("managed", true)]
    public async Task What_a_state_holds_does_not_make_a_vm_the_guests(string mode, bool imported)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        // A plan that creates no machine of the guest's, whose apply leaves VM 310 in the state: read as data, or adopted.
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Failed, error: "boom", artifacts: StateWithVm(Vmid, mode, imported));
        await server.TickAsync();
        var guest = await server.ResourceAsync(Guest);
        Assert.Equal(((int?)null, (int?)null), (guest.BoundVmid, guest.ClaimedVmid));
        await ObserveAsync(server, (Vmid, "c-fedora-01"));

        var refused = await Assert.ThrowsAsync<RunException>(() =>
            server.Services.GetRequiredService<Planner>().PlanGuestAsync(guest.Id, CancellationToken.None));
        Assert.Contains("VMID 310 on korriban is an existing VM Octopushy does not manage (c-fedora-01)", refused.Message);
    }

    [Fact]
    public async Task A_claim_on_another_node_is_not_the_observed_vm()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAndTrustAsync();
        await server.WithDbAsync(async db =>
        {
            var resource = await db.Resources.FirstAsync(r => r.Address == Guest);
            (resource.ClaimedInstallation, resource.ClaimedNode, resource.ClaimedVmid) = (Installation, "dromund", Vmid);
            await db.SaveChangesAsync();
        });
        await ObserveAsync(server, (Vmid, "c-fedora-01"));

        var id = (await server.ResourceAsync(Guest)).Id;
        await server.Engine.CancelAsync((await server.LatestRunAsync(Guest)).Id, "test", CancellationToken.None);
        await Assert.ThrowsAsync<RunException>(() => server.Services.GetRequiredService<Planner>().PlanGuestAsync(id, CancellationToken.None));
    }

    /// <summary>Records an observation of korriban that shows these VMs, as an agent would have reported it.</summary>
    private static Task ObserveAsync(ServerHarness server, params (int Vmid, string Name)[] vms) =>
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
                    ["hosts"] = new JsonArray(new JsonObject { ["name"] = "korriban", ["online"] = true }),
                    ["guests"] = new JsonArray(vms.Select(vm => (JsonNode)new JsonObject
                    {
                        ["vmid"] = vm.Vmid, ["name"] = vm.Name, ["host"] = "korriban", ["status"] = "running",
                    }).ToArray()),
                }.ToJsonString(),
            });
            await db.SaveChangesAsync();
        });

    /// <summary>An OpenTofu state that records one Proxmox VM.</summary>
    private static Artifact StateWithVm(int vmid, string mode = "managed", bool imported = false)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 4,
            serial = 1,
            lineage = "a-lineage",
            outputs = new { },
            resources = new[]
            {
                new
                {
                    mode,
                    type = GuestVmPlan.VmResourceType,
                    name = imported ? "adopted" : "guest",
                    instances = new[] { new { attributes = new { vm_id = vmid, node_name = "korriban" } } },
                },
            },
        });
        return new Artifact { Name = TofuStateStore.ArtifactName, Content = ByteString.CopyFrom(bytes), Sha256 = ArtifactStore.Sha256(bytes) };
    }
}
