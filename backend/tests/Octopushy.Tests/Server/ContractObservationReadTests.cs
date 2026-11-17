using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>
/// What the backend reads of an observation, from <c>proxmox/observe.output.json</c> in contracts/agent/v1/examples, which the
/// agent's tests produce from its own code. The example is stored as the engine stores any observation, and the installation
/// and guest pages, the deploy wizard and the safety boundary have to come out with its values: a field one of them reads that
/// the example lacks, or reads under another name, comes out empty and fails. Every field of the example is read by one of them.
/// </summary>
public class ContractObservationReadTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string Installation = "home/korriban";

    /// <summary>
    /// The example's home: a cluster of korriban and moraband. The guest is declared on moraband, so the example, which shows its
    /// VMID 310 on korriban, shows it moved.
    /// </summary>
    private static TestRepository Repository()
    {
        var repository = TestRepository.Minimal();
        repository.Write("labs/home/proxmox/korriban/installation.yaml", """
            schemaVersion: 1
            type: cluster
            connection:
              endpoint: https://10.1.0.10:8006/
              tls: { verify: false }
              readOnlyToken: { store: proxmox/korriban/read-only-token }
              privilegedToken: { store: proxmox/korriban/privileged-token }
            hosts:
              korriban: { datastores: { images: local, disks: local-lvm }, bridges: [vmbr0] }
              moraband: { datastores: { images: local, disks: local-lvm }, bridges: [vmbr0] }
            """);
        const string file = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
        repository.Write(file, File.ReadAllText(Path.Combine(repository.Root, file)).Replace("host: korriban", "host: moraband"));
        return repository;
    }

    /// <summary>Asks for the next observation and reports <paramref name="outputs"/> for it, as an agent reports a success.</summary>
    private static async Task ObserveAsync(ServerHarness server, ContractExampleTests.Inbox inbox, FakeAgent agent, JsonObject outputs)
    {
        await server.WithDbAsync(db => db.Resources.Where(r => r.Kind == ResourceKinds.Installation)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.ObserveRequestedAt, DateTimeOffset.UtcNow.AddDays(-1))));
        await server.Services.GetRequiredService<Planner>().ScheduleObservesAsync(CancellationToken.None);
        Assert.True(await agent.ReportAsync(await inbox.NextAsync("proxmox", "observe"), Outcome.Succeeded, outputs));
    }

    private static JsonObject ByName(JsonNode? list, string key, JsonNode? value) =>
        Assert.Single(list!.AsArray().OfType<JsonObject>(), x => JsonNode.DeepEquals(x[key], value));

    /// <summary>
    /// Every field of an observed host or guest of the example, shown under its own name with its value. The pages name their
    /// fields as the observation does, so a field read under another name, or one the example renamed, shows as missing.
    /// </summary>
    private static void ShowsEvery(JsonObject observed, JsonObject shown, string what)
    {
        foreach (var (key, value) in observed)
        {
            var same = value?.GetValueKind() == System.Text.Json.JsonValueKind.Number
                ? shown[key]?.GetValueKind() == System.Text.Json.JsonValueKind.Number && value.GetValue<double>() == shown[key]!.GetValue<double>()
                : JsonNode.DeepEquals(value, shown[key]);
            Assert.True(same, $"{what} {key}: the example has {value?.ToJsonString() ?? "null"}, the page shows {shown[key]?.ToJsonString() ?? "nothing"}");
        }
    }

    [Fact]
    public async Task Every_reader_of_an_observation_comes_out_with_the_values_of_the_observe_example()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository());
        var agent = await server.ConnectAgentAsync("home");
        var inbox = new ContractExampleTests.Inbox(server, agent);
        var http = await server.SignedInClientAsync();
        await server.SyncAndTrustAsync(http);
        await agent.ReportAsync(await inbox.NextAsync("proxmox", "observe"), Outcome.Succeeded, new JsonObject());
        // The guest is made and bound first: what drifts is a bound guest's machine, observed after it was bound.
        await agent.ReportAsync(await inbox.NextAsync("opentofu", "preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await inbox.NextAsync("opentofu", "apply"), Outcome.Succeeded);
        await agent.ReportAsync(await inbox.NextAsync("proxmox", "verify-guest"), Outcome.Succeeded);
        await server.TickAsync();
        var guest = await server.ResourceAsync(Guest);
        Assert.Equal((Installation, 310), (guest.BoundInstallation, guest.BoundVmid));

        var example = ContractExamples.Output("proxmox", "observe");
        await ObserveAsync(server, inbox, agent, example);
        var installationId = await server.QueryAsync(db => db.Resources.Where(r => r.Address == Installation).Select(r => r.Id).SingleAsync());

        // The installation page: version, cluster name, every host's figures and every guest's.
        var page = (await http.GetFromJsonAsync<JsonObject>($"/api/installations/{installationId}"))!;
        var summary = page["installation"]!;
        Assert.Equal(example["version"]!.GetValue<string>(), summary["proxmoxVersion"]!.GetValue<string>());
        Assert.Equal(example["cluster"]!["name"]!.GetValue<string>(), page["clusterName"]!.GetValue<string>());
        Assert.Equal(example["hosts"]!.AsArray().Count, summary["hosts"]!.AsArray().Count);
        foreach (var host in example["hosts"]!.AsArray().OfType<JsonObject>())
        {
            ShowsEvery(host, ByName(summary["hosts"], "name", host["name"]), $"host {host["name"]}");
        }

        Assert.Equal(example["guests"]!.AsArray().Count, page["observedGuests"]!.AsArray().Count);
        foreach (var machine in example["guests"]!.AsArray().OfType<JsonObject>())
        {
            ShowsEvery(machine, ByName(page["observedGuests"], "vmid", machine["vmid"]), $"VMID {machine["vmid"]}");
        }

        Assert.Equal(Guest, ByName(page["observedGuests"], "vmid", 310)["managedBy"]!.GetValue<string>());

        // Machine drift: vmVisibility, cluster.quorate, and the bound VMID's host, which the example puts elsewhere.
        var own = Assert.IsType<JsonObject>(ByName(example["guests"], "vmid", 310));
        var drift = Assert.Single(summary["guests"]!.AsArray())!["drift"]!;
        Assert.Equal(MachineDriftKinds.Moved, drift["kind"]!.GetValue<string>());
        Assert.Equal(own["host"]!.GetValue<string>(), drift["observedHost"]!.GetValue<string>());
        Assert.Equal("moraband", drift["declaredHost"]!.GetValue<string>());

        // The deploy wizard: the observation proves every VM and host was seen, each host's state, and which VMIDs are taken.
        var targets = (await http.GetFromJsonAsync<JsonObject>("/api/deploy/targets"))!;
        var installation = ByName(targets["installations"], "address", Installation);
        Assert.Null(installation["vmidProblem"]);
        foreach (var host in example["hosts"]!.AsArray().OfType<JsonObject>())
            Assert.Equal(host["online"]!.GetValue<bool>() ? "online" : "offline", ByName(installation["hosts"], "name", host["name"])["status"]!.GetValue<string>());
        var observedVmids = example["guests"]!.AsArray().Select(g => g!["vmid"]!.GetValue<int>()).ToHashSet();
        var from = observedVmids.Where(v => v != 310).Min();
        var next = (await http.GetFromJsonAsync<JsonObject>($"/api/deploy/vmid?installation={Uri.EscapeDataString(Installation)}&from={from}"))!;
        Assert.Equal(Enumerable.Range(from, 1000).First(v => !observedVmids.Contains(v) && v != 310), next["vmid"]!.GetValue<int>());

        // The safety boundary: a VMID another machine holds is refused, naming that machine as observed.
        var other = example["guests"]!.AsArray().OfType<JsonObject>().First(g => g["vmid"]!.GetValue<int>() != 310);
        var collision = await server.QueryAsync(db => SafetyBoundary.VmidCollisionAsync(db, guest, Installation, other["vmid"]!.GetValue<int>(), CancellationToken.None));
        Assert.Contains($"on {other["host"]}", collision);
        Assert.Contains($"({other["name"]})", collision);

        // An observation that cannot show every VM: the wizard gives its reason, and no drift is told from it.
        var incomplete = ContractExamples.Output("proxmox", "observe", "incomplete-visibility");
        await ObserveAsync(server, inbox, agent, incomplete);
        targets = (await http.GetFromJsonAsync<JsonObject>("/api/deploy/targets"))!;
        Assert.Contains(incomplete["vmVisibility"]!["reason"]!.GetValue<string>(),
            ByName(targets["installations"], "address", Installation)["vmidProblem"]!.GetValue<string>());
        page = (await http.GetFromJsonAsync<JsonObject>($"/api/installations/{installationId}"))!;
        Assert.Null(Assert.Single(page["installation"]!["guests"]!.AsArray())!["drift"]);
    }
}
