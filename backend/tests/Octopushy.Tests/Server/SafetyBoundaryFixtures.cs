using System.Text.Json.Nodes;
using Octopushy.Agent.V1;

namespace Octopushy.Tests.Server;

/// <summary>What the safety boundary's tests share. See docs/safety-boundary.md.</summary>
internal static class SafetyBoundaryFixtures
{
    internal const string Guest = "home/korriban/krb-app-01";
    internal const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    internal const string Installation = "home/korriban";
    internal const int Vmid = 310;
    internal static readonly string[] Replace = ["delete", "create"];

    internal static TestRepository Policy(string policy)
    {
        var repository = TestRepository.Minimal();
        repository.Write("labs/home/lab.yaml", $"schemaVersion: 1\nexecution: {{ agentLabels: [home] }}\n{policy}\n");
        return repository;
    }

    internal static async Task EstablishAsync(ServerHarness server, FakeAgent agent)
    {
        await server.SyncAndTrustAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        Assert.Equal(Vmid, (await server.ResourceAsync(Guest)).BoundVmid);
    }

    internal static async Task RetireAsync(ServerHarness server)
    {
        server.Repository.Write(GuestFile, File.ReadAllText(Path.Combine(server.Repository.Root, GuestFile)) + "lifecycle: retired\n");
        server.Commit("Retire");
        await server.SyncAndTrustAsync();
    }

    internal static JsonObject MachinePlan(params JsonObject[] changes) => new()
    {
        ["hasChanges"] = true,
        ["changes"] = new JsonArray(changes.Select(c => (JsonNode)c).ToArray()),
        ["counts"] = new JsonObject
        {
            ["create"] = changes.Count(c => c["actions"]!.AsArray().Select(a => a!.GetValue<string>()).SequenceEqual(["create"])),
            ["update"] = changes.Count(c => c["actions"]!.AsArray().Select(a => a!.GetValue<string>()).SequenceEqual(["update"])),
            ["delete"] = changes.Count(c => c["actions"]!.AsArray().Select(a => a!.GetValue<string>()).SequenceEqual(["delete"])),
            ["replace"] = changes.Count(c => c["actions"]!.AsArray().Count == 2),
        },
        ["toolVersion"] = "1.13.1",
    };

    internal static JsonObject Change(string type, string[] actions, int? vmid, string? node, string name = "guest")
    {
        var change = new JsonObject
        {
            ["address"] = $"{type}.{name}",
            ["type"] = type,
            ["name"] = name,
            ["actions"] = new JsonArray(actions.Select(a => (JsonNode)a).ToArray()),
        };
        if (vmid is not null) change["vmId"] = vmid;
        if (node is not null) change["node"] = node;
        return change;
    }
}
