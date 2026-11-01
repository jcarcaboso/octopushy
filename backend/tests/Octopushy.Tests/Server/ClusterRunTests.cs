using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;
using Octopushy.Tests.Definitions;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class ClusterRunTests(PostgresFixture postgres)
{
    private const string Cluster = ClusterDefinitionTests.Cluster;
    private const string ClusterFile = ClusterDefinitionTests.ClusterFile;
    private const string Password = "a-shared-dns-admin-password";

    private static JsonObject Outputs => new() { ["outputs"] = new JsonObject(), ["sensitiveOutputs"] = new JsonArray() };

    private static JsonObject Inspection(string state = "joined") => new()
    {
        ["formed"] = true,
        ["members"] = new JsonArray(new JsonObject { ["guest"] = ClusterDefinitionTests.Secondary, ["state"] = state }),
        ["undeclared"] = new JsonObject { ["blocked"] = new JsonArray(), ["allowed"] = new JsonArray() },
    };

    /// <summary>A plan that changes these resources, with the summary the agent reports.</summary>
    private static JsonObject PlanOf(params (string Address, string[] Actions)[] changes)
    {
        int Count(Func<string[], bool> match) => changes.Count(c => match(c.Actions));
        return new JsonObject
        {
            ["hasChanges"] = changes.Length > 0,
            ["changes"] = new JsonArray(changes.Select(c => (JsonNode?)new JsonObject
            {
                ["address"] = c.Address,
                ["type"] = "terraform_data",
                ["name"] = "x",
                ["actions"] = new JsonArray(c.Actions.Select(a => (JsonNode?)a).ToArray()),
            }).ToArray()),
            ["counts"] = new JsonObject
            {
                ["create"] = Count(a => a is ["create"]),
                ["update"] = Count(a => a is ["update"]),
                ["delete"] = Count(a => a is ["delete"]),
                ["replace"] = Count(a => a.Length == 2),
            },
            ["toolVersion"] = "1.13.1",
        };
    }

    /// <summary>What an apply of the cluster's built-in state returns.</summary>
    private static Artifact State()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("""{"version":4,"serial":1,"lineage":"cluster","outputs":{},"resources":[]}""");
        return new Artifact { Name = TofuStateStore.ArtifactName, Content = ByteString.CopyFrom(bytes), Sha256 = ArtifactStore.Sha256(bytes) };
    }

    private static Task ApplyAsync(FakeAgent agent, Assignment apply) =>
        agent.ReportAsync(apply, Outcome.Succeeded, Outputs, artifacts: State());

    private static string Secondary(string guest = ClusterDefinitionTests.Secondary) => $"terraform_data.secondary[\"{guest}\"]";

    private static readonly (string, string[]) Forming = ("technitium_cluster.this", ["create"]);

    /// <summary>Starts a server for the cluster's lab, with both guests applied and verified.</summary>
    private async Task<(ServerHarness Server, FakeAgent Agent, HttpClient Client)> ReadyAsync(TestRepository repository, bool trust = true)
    {
        var server = await ServerHarness.StartAsync(postgres, repository);
        await server.SetSecretAsync("dns/admin", Password);
        var agent = await server.ConnectAgentAsync("home");
        var client = await InstallationTrustTests.AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        await FinishGuestsAsync(server, agent);
        if (trust)
            await TrustAsync(server, client);
        return (server, agent, client);
    }

    /// <summary>
    /// Answers guest work until the guests are verified. Both run at once, so this answers
    /// whatever arrives. <paramref name="failing"/> names a guest whose plan fails.
    /// </summary>
    private static async Task FinishGuestsAsync(ServerHarness server, FakeAgent agent, int guests = 2, string? failing = null)
    {
        var verified = 0;
        for (var i = 0; i < 40 && verified < guests; i++)
        {
            await server.TickAsync();
            foreach (var assignment in agent.TakeAssignments())
            {
                switch (assignment.Action)
                {
                    case "observe":
                        await agent.ReportAsync(assignment, Outcome.Succeeded, new JsonObject());
                        break;
                    case "preview" when failing is not null && assignment.InputJson.Contains(failing):
                        await agent.ReportAsync(assignment, Outcome.Failed, error: "the host is off");
                        break;
                    case "preview":
                        await agent.ReportAsync(assignment, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
                        break;
                    case "apply":
                        await agent.ReportAsync(assignment, Outcome.Succeeded, Outputs);
                        break;
                    case "verify-guest":
                        await agent.ReportAsync(assignment, Outcome.Succeeded, new JsonObject { ["present"] = true });
                        verified++;
                        break;
                }
            }
        }

        Assert.Equal(guests, verified);
        await server.TickAsync();
    }

    private static async Task<JsonObject> ClusterSummaryAsync(HttpClient client)
    {
        var inventory = (await client.GetFromJsonAsync<JsonObject>("/api/inventory"))!;
        return inventory["labs"]![0]!["clusters"]![0]!.AsObject();
    }

    private static async Task<HttpResponseMessage> PostTrustAsync(HttpClient client, JsonObject cluster) =>
        await client.PostAsJsonAsync($"/api/clusters/{cluster["id"]}/trust", new
        {
            endpoints = cluster["trust"]!["endpoints"],
            codeDigest = cluster["trust"]!["codeDigest"]!.GetValue<string>(),
        });

    private static async Task TrustAsync(ServerHarness server, HttpClient client)
    {
        (await PostTrustAsync(client, await ClusterSummaryAsync(client))).EnsureSuccessStatusCode();
        await server.TickAsync();
    }

    /// <summary>Takes a run through inspect and a plan of these changes.</summary>
    private static async Task<Assignment> PlanAsync(FakeAgent agent, JsonObject plan, JsonObject? inspection = null)
    {
        await agent.ReportAsync(await agent.NextAsync("inspect"), Outcome.Succeeded, inspection ?? Inspection());
        var preview = await agent.NextAsync("preview");
        if (plan["hasChanges"]!.GetValue<bool>())
            await agent.ReportAsync(preview, Outcome.Succeeded, plan, artifacts: FakeAgent.PlanArtifact());
        else
            await agent.ReportAsync(preview, Outcome.Succeeded, plan);
        return preview;
    }

    [Fact]
    public async Task A_new_cluster_waits_for_trust_and_receives_no_password()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository, trust: false);
        await using var _ = server;
        await server.TickAsync();

        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains(RepositoryTrust.WaitingForCluster, run.Detail);
        Assert.Contains("https://10.1.0.42:53443", run.Detail);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Executor == "technitium");
        Assert.Equal(0, await server.QueryAsync(db => db.SecretResolutions.CountAsync(r => r.Key == "dns/admin" && db.Attempts.Any(a => a.Id == r.AttemptId && a.Executor == "technitium"))));

        var cluster = await ClusterSummaryAsync(client);
        Assert.Equal(2, cluster["trust"]!["untrusted"]!.AsArray().Count);
        Assert.Equal(HttpStatusCode.OK, (await PostTrustAsync(client, cluster)).StatusCode);
        await server.TickAsync();
        await agent.NextAsync("inspect");
    }

    [Fact]
    public async Task A_commit_that_moves_a_member_waits_before_the_password_goes_there()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await agent.ReportAsync(await agent.NextAsync("inspect"), Outcome.Succeeded, Inspection("absent"));
        agent.TakeAssignments();
        await server.TickAsync();

        repository.Write(ClusterDefinitionTests.SecondaryFile, ClusterDefinitionTests.Guest("moraband", 420, "10.1.0.99"));
        server.Commit("Point the member somewhere else");
        await server.SyncAsync();
        await server.TickAsync();

        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("https://10.1.0.99:53443 is a new member endpoint", run.Detail);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Executor == "technitium" || a.InputJson.Contains("10.1.0.99") && a.Secrets.ContainsKey(SecretNames.ClusterPassword));

        // What the operator saw is what gets trusted.
        var shown = await ClusterSummaryAsync(client);
        shown["trust"]!["endpoints"] = new JsonArray("https://10.1.0.32:53443", "https://10.1.0.77:53443");
        Assert.Equal(HttpStatusCode.Conflict, (await PostTrustAsync(client, shown)).StatusCode);
        Assert.Equal(RunStatuses.Blocked, (await server.LatestRunAsync(Cluster, Operations.Cluster)).Status);

        await TrustAsync(server, client);
        Assert.True((await ClusterSummaryAsync(client))["trust"]!["untrusted"]!.AsArray().Count == 0);
    }

    [Fact]
    public async Task Changed_recipe_code_waits_for_trust_because_it_runs_with_the_password()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await PlanAsync(agent, PlanOf());
        await agent.ReportAsync(await agent.NextAsync("verify"), Outcome.Succeeded, new JsonObject { ["ok"] = true });
        await server.TickAsync();

        repository.Write("modules/opentofu/cluster/main.tf", "data \"external\" \"leak\" {}\n");
        server.Commit("Change the root");
        await server.SyncAsync();
        await server.TickAsync();

        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("recipe code", run.Detail);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Secrets.ContainsKey(SecretNames.ClusterPassword));
        Assert.True((await ClusterSummaryAsync(client))["trust"]!["codeChanged"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_cluster_run_waits_for_its_members_and_names_only_those()
    {
        using var repository = ClusterDefinitionTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        await server.SetSecretAsync("dns/admin", Password);
        var agent = await server.ConnectAgentAsync("home");
        using var client = await InstallationTrustTests.AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        await TrustAsync(server, client);
        await server.TickAsync();

        var waiting = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal(RunStatuses.Blocked, waiting.Status);
        Assert.StartsWith("Waiting for member guests:", waiting.Detail);
        Assert.Contains("krb-dns-01", waiting.Detail);
        Assert.Contains("mrb-dns-01", waiting.Detail);

        // One member's failure holds back its cluster, as blocked work naming that member.
        await FinishGuestsAsync(server, agent, guests: 1, failing: "mrb-dns-01");
        await server.TickAsync();
        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("mrb-dns-01 is not applied from its current definition", run.Detail);
        Assert.DoesNotContain("krb-dns-01", run.Detail);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Executor == "technitium");
    }

    [Fact]
    public async Task A_first_run_forms_joins_refreshes_and_verifies_with_the_password_only_in_secrets()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, _) = await ReadyAsync(repository);
        await using var __ = server;

        var inspect = await agent.NextAsync("inspect");
        Assert.Equal("technitium", inspect.Executor);
        Assert.Equal(Password, inspect.Secrets[SecretNames.ClusterPassword]);
        var inspectInput = JsonNode.Parse(inspect.InputJson)!;
        Assert.False(inspectInput["formed"]!.GetValue<bool>());
        Assert.Equal("https://10.1.0.42:53443", inspectInput["members"]![1]!["endpoint"]!.GetValue<string>());
        Assert.Equal(new JsonArray("bad.example.com").ToJsonString(), inspectInput["blocked"]!.ToJsonString());
        await agent.ReportAsync(inspect, Outcome.Succeeded, Inspection("absent"));

        var preview = await agent.NextAsync("preview");
        var input = JsonNode.Parse(preview.InputJson)!;
        Assert.Equal("home.clusters.technitium", input["stack"]!.GetValue<string>());
        Assert.Equal("octopushy", input["backend"]!["type"]!.GetValue<string>());
        Assert.Equal("technitium.password", input["secretEnv"]!["TECHNITIUM_PASSWORD"]!.GetValue<string>());
        Assert.Equal(Password, preview.Secrets[SecretNames.ClusterPassword]);
        Assert.False(preview.Secrets.ContainsKey(SecretNames.ProxmoxToken));
        Assert.DoesNotContain(Password, preview.InputJson);
        Assert.Equal(Secondary(), input["replace"]![0]!.GetValue<string>());
        await agent.ReportAsync(preview, Outcome.Succeeded,
            PlanOf(Forming, (Secondary(), ["create"]), ("terraform_data.block_lists", ["create"])), artifacts: FakeAgent.PlanArtifact());

        var apply = await agent.NextAsync("apply");
        Assert.Contains(apply.Artifacts, a => a.Name == "plan.tar.gz");
        await ApplyAsync(agent, apply);

        var reconcile = await agent.NextAsync("reconcile");
        var reconcileInput = JsonNode.Parse(reconcile.InputJson)!;
        Assert.Equal(ClusterDefinitionTests.Secondary, reconcileInput["join"]![0]!.GetValue<string>());
        Assert.Empty(reconcileInput["remove"]!.AsArray());
        Assert.False(reconcileInput["pruneEntries"]!.GetValue<bool>());
        await agent.ReportAsync(reconcile, Outcome.Succeeded, new JsonObject());

        await agent.ReportAsync(await agent.NextAsync("refresh-block-lists"), Outcome.Succeeded, new JsonObject { ["refreshed"] = true });
        await agent.ReportAsync(await agent.NextAsync("verify"), Outcome.Succeeded, new JsonObject { ["ok"] = true, ["members"] = new JsonArray() });
        await server.TickAsync();

        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.DoesNotContain(Password, run.Context);
        var resource = await server.QueryAsync(db => db.Resources.SingleAsync(r => r.Address == Cluster));
        Assert.Equal(ClusterDefinitionTests.Primary, resource.BoundPrimary);
        Assert.Equal("cluster.home.arpa", resource.BoundClusterDomain);
        Assert.Equal(resource.DesiredInputsHash, resource.SyncedInputsHash);
        Assert.NotNull(resource.VerifiedAt);
        Assert.Equal(1, await server.QueryAsync(db => db.TofuStates.CountAsync(s => s.ResourceId == resource.Id)));
    }

    [Fact]
    public async Task A_plan_with_no_changes_still_verifies_every_member()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await PlanAsync(agent, PlanOf());

        var verify = await agent.NextAsync("verify");
        Assert.Equal(2, JsonNode.Parse(verify.InputJson)!["members"]!.AsArray().Count);
        await agent.ReportAsync(verify, Outcome.Failed, new JsonObject { ["ok"] = false, ["members"] = new JsonArray() },
            "home/moraband/mrb-dns-01 (10.1.0.42): it reports clusterInitialized=false.");
        await server.TickAsync();

        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.StartsWith("Verification failed: home/moraband/mrb-dns-01", run.Detail);
        Assert.All(run.Steps.Where(s => s.Name is StepNames.Apply or StepNames.ReconcileCluster or StepNames.RefreshBlockLists),
            s => Assert.Equal(StepStatuses.Skipped, s.Status));
        var cluster = await ClusterSummaryAsync(client);
        Assert.Equal("verification-failed", cluster["status"]!.GetValue<string>());
        // The member guests themselves stay as they were.
        var inventory = (await client.GetFromJsonAsync<JsonObject>("/api/inventory"))!;
        foreach (var installation in inventory["labs"]![0]!["installations"]!.AsArray())
            Assert.Equal("in-sync", installation!["guests"]![0]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_member_out_of_the_cluster_is_in_the_preview_and_follows_approval()
    {
        using var repository = ClusterDefinitionTests.Repository(ClusterDefinitionTests.Definition + "\npolicy: { approval: manual }\n");
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        var preview = await PlanAsync(agent, PlanOf((Secondary(), ["delete", "create"])), Inspection("absent"));
        Assert.Equal(Secondary(), JsonNode.Parse(preview.InputJson)!["replace"]![0]!.GetValue<string>());
        await server.TickAsync();

        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        var detail = (await client.GetFromJsonAsync<JsonObject>($"/api/runs/{run.Id}"))!;
        var approval = detail["steps"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == "approval")!;
        (await client.PostAsJsonAsync($"/api/runs/{run.Id}/steps/{approval["id"]}/approve",
            new { planSha256 = approval["approval"]!["planSha256"]!.GetValue<string>() })).EnsureSuccessStatusCode();

        await ApplyAsync(agent, await agent.NextAsync("apply"));
        var reconcile = JsonNode.Parse((await agent.NextAsync("reconcile")).InputJson)!;
        Assert.Equal(ClusterDefinitionTests.Secondary, reconcile["join"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task A_stale_member_is_not_replaced_so_nothing_rejoins_it_behind_the_operators_back()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, _) = await ReadyAsync(repository);
        await using var __ = server;
        var preview = await PlanAsync(agent, PlanOf(), Inspection("stale"));
        Assert.Empty(JsonNode.Parse(preview.InputJson)!["replace"]!.AsArray());
        await agent.NextAsync("verify");
    }

    [Fact]
    public async Task Undeclared_entries_are_replaced_in_the_plan_and_pruned_after_it()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, _) = await ReadyAsync(repository);
        await using var __ = server;
        var inspection = Inspection();
        inspection["undeclared"]!["blocked"] = new JsonArray("hand.example.com");
        var preview = await PlanAsync(agent, PlanOf(("terraform_data.undeclared_entries", ["delete", "create"])), inspection);
        Assert.Equal("terraform_data.undeclared_entries", JsonNode.Parse(preview.InputJson)!["replace"]![0]!.GetValue<string>());
        await ApplyAsync(agent, await agent.NextAsync("apply"));
        var reconcile = await agent.NextAsync("reconcile");
        Assert.True(JsonNode.Parse(reconcile.InputJson)!["pruneEntries"]!.GetValue<bool>());
        await agent.ReportAsync(reconcile, Outcome.Succeeded, new JsonObject());
        await agent.ReportAsync(await agent.NextAsync("verify"), Outcome.Succeeded, new JsonObject { ["ok"] = true });
        await server.TickAsync();
        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal(StepStatuses.Skipped, run.Steps.Single(s => s.Name == StepNames.RefreshBlockLists).Status);
    }

    [Fact]
    public async Task Removing_a_member_waits_under_the_destructive_policy_even_when_changes_are_automatic()
    {
        using var repository = ClusterDefinitionTests.Repository(ClusterDefinitionTests.Definition + "\npolicy: { approval: automatic, destructive: manual }\n");
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await PlanAsync(agent, PlanOf((Secondary(), ["delete"])));
        await server.TickAsync();

        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Contains("destructive is manual", run.Detail);
        var detail = (await client.GetFromJsonAsync<JsonObject>($"/api/runs/{run.Id}"))!;
        var plan = detail["steps"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == "plan")!["plan"]!;
        Assert.Equal(1, plan["counts"]!["delete"]!.GetValue<int>());
        Assert.Equal(Secondary(), plan["changes"]![0]!["address"]!.GetValue<string>());
        var approval = detail["steps"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == "approval")!;
        (await client.PostAsJsonAsync($"/api/runs/{run.Id}/steps/{approval["id"]}/approve",
            new { planSha256 = approval["approval"]!["planSha256"]!.GetValue<string>() })).EnsureSuccessStatusCode();
        await ApplyAsync(agent, await agent.NextAsync("apply"));
        var reconcile = JsonNode.Parse((await agent.NextAsync("reconcile")).InputJson)!;
        Assert.Equal(ClusterDefinitionTests.Secondary, reconcile["remove"]![0]!.GetValue<string>());
        Assert.Empty(reconcile["join"]!.AsArray());
    }

    [Fact]
    public async Task A_primary_or_domain_change_on_a_formed_cluster_is_refused_before_planning()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await PlanAsync(agent, PlanOf(Forming));
        await ApplyAsync(agent, await agent.NextAsync("apply"));
        await agent.ReportAsync(await agent.NextAsync("verify"), Outcome.Succeeded, new JsonObject { ["ok"] = true });
        await server.TickAsync();

        repository.Write(ClusterFile, ClusterDefinitionTests.Definition
            .Replace("korriban/krb-dns-01, role: primary", "korriban/krb-dns-01, role: secondary")
            .Replace("moraband/mrb-dns-01, role: secondary", "moraband/mrb-dns-01, role: primary"));
        server.Commit("Promote");
        await server.SyncAsync();
        var cluster = await ClusterSummaryAsync(client);
        Assert.False(cluster["valid"]!.GetValue<bool>());
        var detail = (await client.GetFromJsonAsync<JsonObject>($"/api/clusters/{cluster["id"]}"))!;
        Assert.Contains(detail["issues"]!.AsArray(), i => i!["message"]!.GetValue<string>().Contains("promotion"));

        repository.Write(ClusterFile, ClusterDefinitionTests.Definition.Replace("clusterDomain: cluster.home.arpa", "clusterDomain: other.home.arpa"));
        server.Commit("Rename");
        await server.SyncAsync();
        detail = (await client.GetFromJsonAsync<JsonObject>($"/api/clusters/{cluster["id"]}"))!;
        Assert.Contains(detail["issues"]!.AsArray(), i => i!["message"]!.GetValue<string>().Contains("cannot change"));
        Assert.Equal(1, await server.QueryAsync(db => db.Runs.CountAsync(r => r.Operation == Operations.Cluster)));
    }

    [Fact]
    public async Task Deleting_the_definition_reports_it_missing_and_destroys_nothing()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await PlanAsync(agent, PlanOf());
        await agent.ReportAsync(await agent.NextAsync("verify"), Outcome.Succeeded, new JsonObject { ["ok"] = true });
        await server.TickAsync();

        repository.Delete("labs/home/clusters/technitium");
        server.Commit("Remove the cluster");
        await server.SyncAsync();
        await server.TickAsync();

        var resource = await server.QueryAsync(db => db.Resources.SingleAsync(r => r.Address == Cluster));
        Assert.NotNull(resource.MissingSince);
        Assert.Equal(1, await server.QueryAsync(db => db.Runs.CountAsync(r => r.Operation == Operations.Cluster)));
        Assert.Equal(0, await server.QueryAsync(db => db.Runs.CountAsync(r => r.Operation == Operations.Retire)));
        var cluster = await ClusterSummaryAsync(client);
        Assert.Equal("missing", cluster["status"]!.GetValue<string>());
        Assert.Contains("nothing was removed", cluster["statusDetail"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/clusters/{resource.Id}/plan", null)).StatusCode);

        // Restoring it picks the cluster up again.
        repository.Write(ClusterFile, ClusterDefinitionTests.Definition);
        server.Commit("Restore the cluster");
        await server.SyncAsync();
        Assert.Null((await server.QueryAsync(db => db.Resources.SingleAsync(r => r.Address == Cluster))).MissingSince);
    }

    [Fact]
    public async Task A_guest_listed_in_a_cluster_is_not_removed_when_its_folder_goes()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, _, client) = await ReadyAsync(repository);
        await using var __ = server;

        repository.Delete("labs/home/proxmox/moraband/guests/mrb-dns-01");
        server.Commit("Delete a member's folder");
        await server.SyncAsync();

        Assert.Equal(0, await server.QueryAsync(db => db.Runs.CountAsync(r => r.Operation == Operations.Retire)));
        var guest = await server.QueryAsync(db => db.Resources.SingleAsync(r => r.Address == ClusterDefinitionTests.Secondary));
        Assert.Contains("still listed in home/clusters/technitium", guest.Issues);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/guests/{guest.Id}/plan", null)).StatusCode);
    }

    [Fact]
    public async Task A_member_guest_that_applied_a_change_gets_its_cluster_checked()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, _) = await ReadyAsync(repository);
        await using var __ = server;
        await PlanAsync(agent, PlanOf(Forming));
        await ApplyAsync(agent, await agent.NextAsync("apply"));
        await agent.ReportAsync(await agent.NextAsync("verify"), Outcome.Succeeded, new JsonObject { ["ok"] = true });
        await server.TickAsync();

        var planner = server.Services.GetRequiredService<Planner>();
        await planner.ScheduleClusterChecksAsync(CancellationToken.None);
        Assert.Equal(1, await server.QueryAsync(db => db.Runs.CountAsync(r => r.Operation == Operations.Cluster)));

        // A guest's plan that changes something, as replacing a container does.
        var guestId = await server.QueryAsync(db => db.Resources.Where(r => r.Address == ClusterDefinitionTests.Secondary).Select(r => r.Id).SingleAsync());
        await planner.PlanGuestAsync(guestId, CancellationToken.None);
        await FinishGuestsAsync(server, agent, guests: 1);
        await planner.ScheduleClusterChecksAsync(CancellationToken.None);
        await server.TickAsync();

        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal($"member {ClusterDefinitionTests.Secondary} changed", run.Reason);
        await PlanAsync(agent, PlanOf());
        await agent.NextAsync("verify");

        // And once: one change does not keep proposing runs.
        await planner.ScheduleClusterChecksAsync(CancellationToken.None);
        Assert.Equal(2, await server.QueryAsync(db => db.Runs.CountAsync(r => r.Operation == Operations.Cluster)));
    }

    [Fact]
    public async Task A_formed_cluster_is_observed_in_the_background_and_a_failure_surfaces()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await PlanAsync(agent, PlanOf(Forming));
        await ApplyAsync(agent, await agent.NextAsync("apply"));
        await agent.ReportAsync(await agent.NextAsync("verify"), Outcome.Succeeded, new JsonObject { ["ok"] = true });
        await server.TickAsync();

        await server.Services.GetRequiredService<Planner>().ScheduleObservesAsync(CancellationToken.None);
        await server.TickAsync();
        var check = await agent.NextAsync("verify");
        Assert.Equal(Password, check.Secrets[SecretNames.ClusterPassword]);
        await agent.ReportAsync(check, Outcome.Failed, new JsonObject { ["ok"] = false }, "home/moraband/mrb-dns-01 (10.1.0.42): it reports clusterInitialized=false.");
        await server.TickAsync();

        var overview = (await client.GetFromJsonAsync<JsonObject>("/api/overview"))!;
        var error = overview["observeErrors"]!.AsArray().Single(e => e!["kind"]!.GetValue<string>() == "cluster")!;
        Assert.Equal(Cluster, error["address"]!.GetValue<string>());
        Assert.Contains("mrb-dns-01", error["error"]!.GetValue<string>());
        Assert.Equal(1, await server.QueryAsync(db => db.Runs.CountAsync(r => r.Operation == Operations.Cluster)));
    }

    [Fact]
    public async Task Planning_by_hand_verifies_again_and_the_detail_shows_members()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await PlanAsync(agent, PlanOf());
        await agent.ReportAsync(await agent.NextAsync("verify"), Outcome.Succeeded, new JsonObject
        {
            ["ok"] = true,
            ["members"] = new JsonArray(new JsonObject { ["guest"] = ClusterDefinitionTests.Primary, ["ok"] = true }),
        });
        await server.TickAsync();

        var id = (await ClusterSummaryAsync(client))["id"]!.GetValue<Guid>();
        var detail = (await client.GetFromJsonAsync<JsonObject>($"/api/clusters/{id}"))!;
        Assert.Equal(ClusterDefinitionTests.Primary, detail["cluster"]!["members"]![0]!["guest"]!.GetValue<string>());
        Assert.Equal("10.1.0.32", detail["cluster"]!["members"]![0]!["address"]!.GetValue<string>());
        Assert.Equal("in-sync", detail["cluster"]!["members"]![0]!["guestStatus"]!.GetValue<string>());
        Assert.True(detail["verification"]!["ok"]!.GetValue<bool>());
        Assert.Equal("manual", (await client.PostAsync($"/api/clusters/{id}/plan", null)).IsSuccessStatusCode ? "manual" : "refused");
        await server.TickAsync();
        Assert.Equal("manual", (await server.LatestRunAsync(Cluster, Operations.Cluster)).Reason);
    }
}
