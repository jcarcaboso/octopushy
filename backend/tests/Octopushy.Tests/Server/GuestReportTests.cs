using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Octopushy.Tests.Definitions;

namespace Octopushy.Tests.Server;

/// <summary>
/// What a guest's setup reports reaches the guests that name it among their peers, and a new report proposes their setup once:
/// two members exchange certificate fingerprints with no commit and no copying.
/// </summary>
public class GuestReportTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string First = "home/korriban/krb-app-01";
    private const string Second = "home/korriban/krb-app-02";
    private const string FirstFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    private const string SecondFile = "labs/home/proxmox/korriban/guests/krb-app-02/guest.yaml";
    private static readonly string FirstPin = new('a', 64);
    private static readonly string SecondPin = new('b', 64);

    private const string Reports = """
        reports:
          certificate_sha256: { pattern: "[0-9a-f]{64}", description: The node's own certificate }
          role: { pattern: standalone|primary|secondary }
        peers: inputs.members[]
        """;

    /// <summary>The configure fixture's guest and a second one like it, naming each other, whose workflow declares reports.</summary>
    internal static TestRepository Lab(string workflow = Reports)
    {
        var test = GuestConfigurationTests.Repository(extraWorkflow: workflow);
        var text = File.ReadAllText(Path.Combine(test.Root, FirstFile));
        Assert.Contains("inputs: { greeting: hello }", text);
        text = text.Replace("inputs: { greeting: hello }", "inputs: { greeting: hello, members: [krb-app-01, krb-app-02] }");
        test.Write(FirstFile, text);
        test.Write(SecondFile, text.Replace("vmid: 310", "vmid: 311").Replace("10.1.0.31", "10.1.0.32"));
        return test;
    }

    /// <summary>What each guest's playbook reports, by its address.</summary>
    private static JsonObject ReportsOf(string address) => address switch
    {
        "10.1.0.31" => new JsonObject { ["certificate_sha256"] = FirstPin, ["role"] = "primary", ["undeclared"] = "x" },
        _ => new JsonObject { ["certificate_sha256"] = SecondPin, ["role"] = "not a role" },
    };

    private static JsonObject Input(Assignment assignment) => JsonNode.Parse(assignment.InputJson)!.AsObject();

    /// <summary>Answers everything until nothing more comes, and returns the ansible runs, in order.</summary>
    private static async Task<List<Assignment>> WorkAsync(ServerHarness server, FakeAgent agent, Func<string, JsonObject>? reports = null)
    {
        reports ??= ReportsOf;
        var runs = new List<Assignment>();
        var planner = server.Services.GetRequiredService<Planner>();
        var idle = 0;
        for (var i = 0; i < 40 && idle < 2; i++)
        {
            await server.TickAsync();
            await planner.ScheduleConfigurationsAsync(CancellationToken.None);
            await server.TickAsync();
            var assignments = agent.TakeAssignments();
            idle = assignments.Count == 0 ? idle + 1 : 0;
            foreach (var assignment in assignments)
            {
                switch (assignment.Executor, assignment.Action)
                {
                    case ("opentofu", "preview"):
                        await agent.ReportAsync(assignment, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
                        break;
                    case ("ansible", "preview"):
                        var (outputs, artifact) = ConfigureRunTests.Preview();
                        await agent.ReportAsync(assignment, Outcome.Succeeded, outputs, artifacts: artifact);
                        break;
                    case ("ansible", "run"):
                        runs.Add(assignment);
                        var recap = ConfigureRunTests.Recap();
                        recap["reports"] = reports(Input(assignment)["host"]!["address"]!.GetValue<string>());
                        await agent.ReportAsync(assignment, Outcome.Succeeded, recap);
                        break;
                    default:
                        await agent.ReportAsync(assignment, Outcome.Succeeded);
                        break;
                }
            }
        }

        Assert.True(idle >= 2, "The work did not settle.");
        return runs;
    }

    private static async Task<(FakeAgent Agent, HttpClient Http)> StartAsync(ServerHarness server)
    {
        await ConfigureRunTests.SetKeysAsync(server);
        var http = await server.SignedInClientAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);
        return (agent, http);
    }

    private static async Task<JsonObject?> StoredAsync(ServerHarness server, string address) => await server.QueryAsync(async db =>
    {
        var resource = await db.Resources.AsNoTracking().SingleAsync(r => r.Address == address);
        var text = await db.GuestConfigurations.AsNoTracking().Where(c => c.ResourceId == resource.Id).Select(c => c.Reports).SingleAsync();
        return text is null ? null : JsonNode.Parse(text)!.AsObject();
    });

    [Fact]
    public async Task Members_get_each_others_reports_and_a_new_one_proposes_setup_once_until_nothing_changes()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Lab());
        var (agent, _) = await StartAsync(server);
        var runs = await WorkAsync(server, agent);

        // Only what is declared, matching its pattern whole, is kept: the second's role is not one.
        var first = (await StoredAsync(server, First))!;
        Assert.Equal(["certificate_sha256", "role"], first.Select(p => p.Key).Order());
        Assert.Equal(FirstPin, first["certificate_sha256"]!["value"]!.GetValue<string>());
        Assert.Equal("10.1.0.31", first["certificate_sha256"]!["address"]!.GetValue<string>());
        var second = (await StoredAsync(server, Second))!;
        Assert.Equal(["certificate_sha256"], second.Select(p => p.Key));

        // Each guest's last run got the other's report, over the address its setup connects to, and never its own.
        var last = runs.GroupBy(r => Input(r)["host"]!["address"]!.GetValue<string>()).ToDictionary(g => g.Key, g => Input(g.Last()));
        var toFirst = last["10.1.0.31"]["context"]!["peers"]!.AsObject();
        Assert.Equal(["krb-app-02"], toFirst.Select(p => p.Key));
        Assert.Equal("10.1.0.32", toFirst["krb-app-02"]!["address"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["certificate_sha256"] = SecondPin }, toFirst["krb-app-02"]!["reports"]));
        var toSecond = last["10.1.0.32"]["context"]!["peers"]!.AsObject();
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["certificate_sha256"] = FirstPin, ["role"] = "primary" }, toSecond["krb-app-01"]!["reports"]));
        // The approved document names exactly what the playbook gets.
        Assert.True(JsonNode.DeepEquals(toFirst, last["10.1.0.31"]["manifest"]!["peers"]));

        // The undeclared and the mismatched values are named on the step that reported them, never repeated.
        var details = await server.QueryAsync(db => db.Steps.AsNoTracking()
            .Where(s => s.Name == StepNames.Playbook && s.Detail != null).Select(s => s.Detail!).ToListAsync());
        Assert.Contains(details, d => d.Contains("a name the workflow does not declare", StringComparison.Ordinal));
        Assert.Contains(details, d => d.Contains("role", StringComparison.Ordinal) && !d.Contains("not a role", StringComparison.Ordinal));

        // A peer's report proposed the other's setup, saying whose; then nothing more: values that do not move start nothing.
        var peerRuns = await server.QueryAsync(db => db.Runs.AsNoTracking().Where(r => r.Reason == RunReasons.Peer).ToListAsync());
        Assert.NotEmpty(peerRuns);
        Assert.All(peerRuns, r => Assert.StartsWith("New from krb-app-0", r.ReasonDetail, StringComparison.Ordinal));
        var count = await server.QueryAsync(db => db.Runs.CountAsync(r => r.Operation == Operations.Configure));
        await server.Services.GetRequiredService<Planner>().ScheduleConfigurationsAsync(CancellationToken.None);
        Assert.Equal(count, await server.QueryAsync(db => db.Runs.CountAsync(r => r.Operation == Operations.Configure)));
        Assert.All(await server.QueryAsync(db => db.Runs.AsNoTracking().Where(r => r.Operation == Operations.Configure).ToListAsync()),
            r => Assert.Equal(RunStatuses.Succeeded, r.Status));
    }

    [Fact]
    public async Task A_new_value_from_a_peer_proposes_setup_once_and_a_failed_run_reports_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Lab());
        var (agent, _) = await StartAsync(server);
        await WorkAsync(server, agent);
        var firstResource = await server.ResourceAsync(First);
        var before = await server.QueryAsync(db => db.Runs.CountAsync(r => r.ResourceId == firstResource.Id));

        // The second is rebuilt and reports a new certificate: the first's setup is proposed with it, once.
        var rebuilt = new string('c', 64);
        await server.Services.GetRequiredService<Planner>().ConfigureGuestAsync((await server.ResourceAsync(Second)).Id, null, CancellationToken.None);
        var runs = await WorkAsync(server, agent, address => address == "10.1.0.32"
            ? new JsonObject { ["certificate_sha256"] = rebuilt }
            : ReportsOf(address));
        var toFirst = runs.Select(Input).Last(i => i["host"]!["address"]!.GetValue<string>() == "10.1.0.31");
        Assert.Equal(rebuilt, toFirst["context"]!["peers"]!["krb-app-02"]!["reports"]!["certificate_sha256"]!.GetValue<string>());
        var peerRun = await server.LatestRunAsync(First, Operations.Configure);
        Assert.Equal(RunReasons.Peer, peerRun.Reason);
        Assert.Equal("New from krb-app-02 (certificate_sha256).", peerRun.ReasonDetail);
        Assert.Equal(before + 1, await server.QueryAsync(db => db.Runs.CountAsync(r => r.ResourceId == firstResource.Id)));

        // A playbook that failed keeps nothing it said it would report.
        await server.Services.GetRequiredService<Planner>().ConfigureGuestAsync((await server.ResourceAsync(Second)).Id, null, CancellationToken.None);
        await server.TickAsync();
        await ConfigureRunTests.PreviewAsync(agent);
        var playbook = await agent.NextAsync("run");
        var recap = ConfigureRunTests.Recap();
        recap["reports"] = new JsonObject { ["certificate_sha256"] = new string('d', 64) };
        await agent.ReportAsync(playbook, Outcome.Failed, recap, "the task \"Install\" failed");
        await server.TickAsync();
        Assert.Equal(rebuilt, (await StoredAsync(server, Second))!["certificate_sha256"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_report_made_at_another_address_is_not_given_and_a_guest_of_another_workflow_is_no_peer()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Lab());
        var (agent, _) = await StartAsync(server);
        await WorkAsync(server, agent);

        // The second moves: what it reported at its old address says nothing about what answers at the new one.
        var test = server.Repository;
        test.Write(SecondFile, File.ReadAllText(Path.Combine(test.Root, SecondFile)).Replace("10.1.0.32", "10.1.0.42"));
        server.Commit("Move the second");
        await server.SyncAsync();
        var repository = LabRepositoryLoader.Load(test.Root);
        var peers = await server.QueryAsync(db => GuestReports.PeersAsync(db, repository, repository.Guest(First)!, CancellationToken.None));
        Assert.Equal("10.1.0.42", peers!["krb-app-02"]!["address"]!.GetValue<string>());
        Assert.Empty(peers["krb-app-02"]!["reports"]!.AsObject());

        // An id two installations of the lab both use names neither guest.
        test.Write("labs/home/proxmox/other/installation.yaml", File.ReadAllText(Path.Combine(test.Root, "labs/home/proxmox/korriban/installation.yaml"))
            .Replace("10.1.0.10", "10.1.0.11").Replace("proxmox/korriban/", "proxmox/other/"));
        test.Write("labs/home/proxmox/other/guests/krb-app-02/guest.yaml", File.ReadAllText(Path.Combine(test.Root, SecondFile))
            .Replace("vmid: 311", "vmid: 312").Replace("10.1.0.42", "10.1.0.52"));
        repository = LabRepositoryLoader.Load(test.Root);
        Assert.True(repository.IsValid("home/other/krb-app-02"), string.Join("\n", repository.Issues.Select(i => i.Message)));
        Assert.Empty(GuestReports.PeersOf(repository, repository.Guest(First)!));
        Directory.Delete(Path.Combine(test.Root, "labs/home/proxmox/other"), recursive: true);
        repository = LabRepositoryLoader.Load(test.Root);
        Assert.Single(GuestReports.PeersOf(repository, repository.Guest(First)!));

        // A guest of another workflow is never a peer, whatever its id and however alike the workflows are.
        foreach (var file in new[] { "workflow.yaml", "inputs.schema.json" })
            test.Write($"workflows/app-other/{file}", File.ReadAllText(Path.Combine(test.Root, "workflows/app", file)));
        test.Write(SecondFile, File.ReadAllText(Path.Combine(test.Root, SecondFile)).Replace("workflow: app@1", "workflow: app-other@1"));
        repository = LabRepositoryLoader.Load(test.Root);
        Assert.True(repository.IsValid(Second), string.Join("\n", repository.Issues.Select(i => i.Message)));
        Assert.Empty(GuestReports.PeersOf(repository, repository.Guest(First)!));
    }
}
