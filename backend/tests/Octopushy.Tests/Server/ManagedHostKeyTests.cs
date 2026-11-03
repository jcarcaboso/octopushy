using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;
using Repository = Octopushy.Tests.Definitions.GuestConfigurationTests;

namespace Octopushy.Tests.Server;

/// <summary>A guest whose SSH host key is Octopushy's: generated, delivered in a snippet, pinned.</summary>
[Collection(PostgresCollection.Name)]
public class ManagedHostKeyTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    private const string InstallationFile = "labs/home/proxmox/korriban/installation.yaml";
    private const string NodeKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIKiijI7PL8NeaSlRIfsOf1JSG69ZMULksy817NNUwsNZ root@korriban";
    private const string NodeKeyStore = "proxmox/korriban/ssh-host-key";

    /// <summary>
    /// The configure fixture as a guest Octopushy creates: its own login key and no pin of its own,
    /// with a recipe that delivers one and a host set up for it.
    /// </summary>
    internal static TestRepository Managed(bool declaredPin = false, TestRepository? start = null)
    {
        var test = start ?? Repository.Repository();
        var guest = File.ReadAllText(Path.Combine(test.Root, GuestFile)).Replace("  sshKey: { store: access/app/ssh-key }\n", "");
        test.Write(GuestFile, declaredPin ? guest : guest.Replace("  hostKey: { store: access/app/host-key }\n", ""));
        test.Write("recipes/test-vm/recipe.yaml", File.ReadAllText(Path.Combine(test.Root, "recipes/test-vm/recipe.yaml")) + "hostKey: managed\n");
        test.Write(InstallationFile, File.ReadAllText(Path.Combine(test.Root, InstallationFile)) + $"""
            snippets:
              hosts:
                korriban: {"{"} hostKey: {"{"} store: {NodeKeyStore} {"}"} {"}"}

            """);
        return test;
    }

    private static Task ScheduleAsync(ServerHarness server) =>
        server.Services.GetRequiredService<Planner>().ScheduleConfigurationsAsync(CancellationToken.None);

    private static async Task<T> QueryAsync<T>(ServerHarness server, Func<OctopushyDb, Task<T>> read)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<OctopushyDb>());
    }

    private static Task<string> PrivateHalfAsync(ServerHarness server, Guid resource) => QueryAsync(server, async db =>
    {
        await using var scope = server.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<VariableStore>().GetVersionInternalAsync(GuestHostCredentials.Key(resource), 1, CancellationToken.None))!;
    });

    /// <summary>Signed in, synced and trusted, with the host's pin and the installation's upload key in place.</summary>
    private static async Task<(FakeAgent Agent, HttpClient Http, JsonObject Snippets)> ReadyAsync(ServerHarness server, bool uploadKey = true, bool pinIsSecret = false)
    {
        await server.SetSecretAsync("access/app/ssh-key", "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXk\n-----END OPENSSH PRIVATE KEY-----\n");
        await server.SetPlainAsync("access/app/host-key", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIL4yMxTo2ZC6CYBxvdtnHHtR5HBTjQeL+w9jI3rj+PyY");
        await server.SetSecretAsync("app/tailscale", "tskey-secret-value");
        await server.SetSecretAsync("app/git-key", "git-key-value");
        if (pinIsSecret)
            await server.SetSecretAsync(NodeKeyStore, NodeKey);
        else
            await server.SetPlainAsync(NodeKeyStore, NodeKey);
        var http = server.CreateClient();
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await http.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);
        JsonObject snippets = [];
        if (uploadKey)
        {
            var installation = await server.ResourceAsync("home/korriban");
            var created = await http.PostAsync($"/api/installations/{installation.Id}/snippets-key", null);
            created.EnsureSuccessStatusCode();
            snippets = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
            await server.TickAsync();
        }

        return (agent, http, snippets);
    }

    private static async Task<(Assignment Plan, Assignment Apply, Assignment Verify)> ProvisionAsync(FakeAgent agent, ServerHarness server)
    {
        var plan = await agent.NextAsync("preview");
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        var apply = await agent.NextAsync("apply");
        await agent.ReportAsync(apply, Outcome.Succeeded);
        var verify = await agent.NextAsync("verify-guest");
        await agent.ReportAsync(verify, Outcome.Succeeded);
        await server.TickAsync();
        return (plan, apply, verify);
    }

    private static IEnumerable<string> Everything(Assignment assignment) => assignment.Secrets.Values.Append(assignment.InputJson);

    [Fact]
    public async Task The_plan_gets_the_private_host_key_and_the_apply_gets_the_upload_key_and_the_hosts_pin()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, snippets) = await ReadyAsync(server);
        using var _ = http;
        var (plan, apply, verify) = await ProvisionAsync(agent, server);
        var resource = await server.ResourceAsync(Guest);
        var hostKey = await QueryAsync(server, db => db.GuestHostKeys.AsNoTracking().SingleAsync());
        var privateHalf = await PrivateHalfAsync(server, resource.Id);
        Assert.StartsWith("ssh-ed25519 ", hostKey.PublicKey);
        Assert.EndsWith("root@krb-app-01", hostKey.PublicKey);
        Assert.Contains("OPENSSH PRIVATE KEY", privateHalf);

        // The plan writes the snippet: the private half as a sensitive variable, the rest as plain ones.
        var input = JsonNode.Parse(plan.InputJson)!;
        Assert.Equal(privateHalf, plan.Secrets[SecretNames.GuestHostKey]);
        Assert.Equal(SecretNames.GuestHostKey, input["secretEnv"]![SecretNames.GuestHostKeyEnv]!.GetValue<string>());
        Assert.DoesNotContain(privateHalf, plan.InputJson);
        Assert.False(plan.Secrets.ContainsKey(SecretNames.SnippetsKey));
        Assert.Null(input["secretEnv"]![SecretNames.SnippetsKeyEnv]);
        Assert.Null(input["knownHosts"]);
        var variables = input["variables"]!["octopushy_snippets"]!;
        Assert.Equal("local", variables["datastore"]!.GetValue<string>());
        Assert.Equal(hostKey.PublicKey, variables["hostPublicKey"]!.GetValue<string>());
        Assert.Equal("korriban", variables["node"]!["name"]!.GetValue<string>());
        Assert.Equal("10.1.0.10", variables["node"]!["address"]!.GetValue<string>());
        Assert.Equal(22, variables["node"]!["port"]!.GetValue<int>());
        Assert.Equal("octopushy-snippets", input["env"]![SecretNames.SnippetsUserEnv]!.GetValue<string>());

        // The apply uploads it: the installation's key, and the only host key it may connect to.
        var applyInput = JsonNode.Parse(apply.InputJson)!;
        Assert.False(apply.Secrets.ContainsKey(SecretNames.GuestHostKey));
        Assert.Null(applyInput["secretEnv"]![SecretNames.GuestHostKeyEnv]);
        Assert.Contains("OPENSSH PRIVATE KEY", apply.Secrets[SecretNames.SnippetsKey]);
        Assert.Equal(SecretNames.SnippetsKey, applyInput["secretEnv"]![SecretNames.SnippetsKeyEnv]!.GetValue<string>());
        Assert.Equal("* ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIKiijI7PL8NeaSlRIfsOf1JSG69ZMULksy817NNUwsNZ\n", applyInput["knownHosts"]!.GetValue<string>());
        Assert.False(apply.Secrets.ContainsKey(SecretNames.SnippetsHostKey));
        Assert.DoesNotContain(Everything(apply), v => v.Contains(privateHalf));
        Assert.DoesNotContain(Everything(verify), v => v.Contains(privateHalf) || v.Contains(apply.Secrets[SecretNames.SnippetsKey]));
        Assert.Equal(snippets["publicKey"]!.GetValue<string>(),
            (await QueryAsync(server, db => db.InstallationSnippetKeys.AsNoTracking().SingleAsync())).PublicKey);

        // Each release is on record, and the key is pinned from now on.
        Assert.NotNull((await QueryAsync(server, db => db.GuestHostKeys.AsNoTracking().SingleAsync())).ProvisionedAt);
        var released = await QueryAsync(server, db => db.SecretResolutions.AsNoTracking().Select(r => new { r.Key, r.AttemptId }).ToListAsync());
        Assert.Contains(released, r => r.Key == GuestHostCredentials.Key(resource.Id) && r.AttemptId == Guid.Parse(plan.AttemptId));
        Assert.DoesNotContain(released, r => r.Key == GuestHostCredentials.Key(resource.Id) && r.AttemptId != Guid.Parse(plan.AttemptId));
        Assert.Contains(released, r => r.Key == InstallationSnippetCredentials.Key("home/korriban") && r.AttemptId == Guid.Parse(apply.AttemptId));
        Assert.DoesNotContain(released, r => r.Key == InstallationSnippetCredentials.Key("home/korriban") && r.AttemptId != Guid.Parse(apply.AttemptId));
    }

    [Fact]
    public async Task A_configure_run_pins_octopushys_key_and_never_holds_its_private_half()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, _) = await ReadyAsync(server);
        using var _ = http;
        await ProvisionAsync(agent, server);
        var resource = await server.ResourceAsync(Guest);
        var hostKey = await QueryAsync(server, db => db.GuestHostKeys.AsNoTracking().SingleAsync());
        var privateHalf = await PrivateHalfAsync(server, resource.Id);

        await ScheduleAsync(server);
        var preview = await ConfigureRunTests.PreviewAsync(agent);
        Assert.Equal(hostKey.PublicKey, JsonNode.Parse(preview.InputJson)!["host"]!["hostKey"]!.GetValue<string>());
        Assert.Empty(preview.Secrets);
        var playbook = await agent.NextAsync("run");
        Assert.Equal(hostKey.PublicKey, JsonNode.Parse(playbook.InputJson)!["host"]!["hostKey"]!.GetValue<string>());
        foreach (var assignment in new[] { preview, playbook })
        {
            Assert.DoesNotContain(Everything(assignment), v => v.Contains(privateHalf));
            Assert.False(assignment.Secrets.ContainsKey(SecretNames.SnippetsKey));
            Assert.False(assignment.Secrets.ContainsKey(SecretNames.GuestHostKey));
        }
        await agent.ReportAsync(playbook, Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.DoesNotContain(SecretNames.AnsibleHostKey, RunContext.Parse(run.Context).Secrets.Keys);

        // The rotation of the login key pins the same key.
        (await http.PostAsync($"/api/guests/{resource.Id}/access/rotate", null)).EnsureSuccessStatusCode();
        await server.TickAsync();
        var rotationPreview = await agent.NextAsync("preview");
        Assert.Equal(hostKey.PublicKey, JsonNode.Parse(rotationPreview.InputJson)!["host"]!["hostKey"]!.GetValue<string>());
        Assert.DoesNotContain(Everything(rotationPreview), v => v.Contains(privateHalf));
    }

    [Fact]
    public async Task Until_an_apply_delivers_the_key_no_configure_run_connects()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, _) = await ReadyAsync(server);
        using var _ = http;
        await ProvisionAsync(agent, server);
        await server.WithDbAsync(db => db.GuestHostKeys.ExecuteUpdateAsync(u => u.SetProperty(k => k.ProvisionedAt, (DateTimeOffset?)null)));

        await ScheduleAsync(server);
        await server.TickAsync();
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Executor is "opentofu" or "ansible");
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Contains("no provision run has delivered it yet", run.Steps.Single(s => s.Name == StepNames.Preview).Attempts.Single().WaitingReason
            ?? run.Detail ?? "");
    }

    [Fact]
    public async Task Without_the_installations_upload_key_the_plan_waits_and_says_what_to_do()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, _) = await ReadyAsync(server, uploadKey: false);
        using var _ = http;
        await server.TickAsync();
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Executor is "opentofu" or "ansible");
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("Create the snippet upload key on the installation's page", run.Detail);
        Assert.Contains("deploy/proxmox/snippets.sh", run.Detail);
    }

    [Fact]
    public async Task A_secret_cannot_pose_as_the_hosts_pin()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, _) = await ReadyAsync(server, pinIsSecret: true);
        using var _ = http;
        await server.TickAsync();
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Executor is "opentofu" or "ansible");
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains($"{NodeKeyStore}: a host key is public; store it as plain", run.Detail);
    }

    [Fact]
    public async Task The_pages_show_public_halves_and_no_response_holds_a_private_one()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, snippets) = await ReadyAsync(server);
        using var _ = http;
        var (_, apply, _) = await ProvisionAsync(agent, server);
        var resource = await server.ResourceAsync(Guest);
        var installation = await server.ResourceAsync("home/korriban");
        var hostKey = await QueryAsync(server, db => db.GuestHostKeys.AsNoTracking().SingleAsync());
        var privateHalf = await PrivateHalfAsync(server, resource.Id);

        var guestPage = JsonNode.Parse(await http.GetStringAsync($"/api/guests/{resource.Id}"))!;
        Assert.Equal(hostKey.PublicKey, guestPage["hostKey"]!["publicKey"]!.GetValue<string>());
        Assert.Equal(hostKey.Fingerprint, guestPage["hostKey"]!["fingerprint"]!.GetValue<string>());
        Assert.NotNull(guestPage["hostKey"]!["provisionedAt"]);

        var installationPage = JsonNode.Parse(await http.GetStringAsync($"/api/installations/{installation.Id}"))!["snippets"]!;
        var publicKey = installationPage["publicKey"]!.GetValue<string>();
        Assert.Equal(snippets["publicKey"]!.GetValue<string>(), publicKey);
        Assert.EndsWith("octopushy-snippets@home/korriban", publicKey);
        Assert.True(installationPage["declared"]!.GetValue<bool>());
        Assert.Contains($"OCTOPUSHY_SNIPPETS_KEY='{publicKey}'", installationPage["command"]!.GetValue<string>());
        Assert.Contains("/deploy/proxmox/snippets.sh", installationPage["command"]!.GetValue<string>());
        var host = installationPage["hosts"]!.AsArray().Single()!;
        Assert.Equal("korriban", host["host"]!.GetValue<string>());
        Assert.Equal("10.1.0.10", host["address"]!.GetValue<string>());
        Assert.True(host["hostKeySet"]!.GetValue<bool>());

        // Asking again returns the same key.
        var again = await http.PostAsync($"/api/installations/{installation.Id}/snippets-key", null);
        Assert.Equal(publicKey, (await again.Content.ReadFromJsonAsync<JsonObject>())!["publicKey"]!.GetValue<string>());

        var provision = await server.LatestRunAsync(Guest, Operations.Provision);
        foreach (var path in new[] { $"/api/guests/{resource.Id}", $"/api/installations/{installation.Id}", $"/api/runs/{provision.Id}", "/api/variables" })
        {
            var text = await http.GetStringAsync(path);
            Assert.DoesNotContain(privateHalf.Split('\n')[1], text);
            Assert.DoesNotContain(apply.Secrets[SecretNames.SnippetsKey].Split('\n')[1], text);
            Assert.DoesNotContain("OPENSSH PRIVATE KEY", text);
        }
    }

    [Fact]
    public async Task The_controller_masks_the_keys_in_the_steps_that_hold_them()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, _) = await ReadyAsync(server);
        using var _ = http;
        var plan = await agent.NextAsync("preview");
        var logs = server.Services.GetRequiredService<AttemptLogStore>();
        var line = plan.Secrets[SecretNames.GuestHostKey].Split('\n')[1];
        Assert.Equal("data: ***", logs.Mask(Guid.Parse(plan.AttemptId), $"data: {line}"));
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        var apply = await agent.NextAsync("apply");
        var upload = apply.Secrets[SecretNames.SnippetsKey].Split('\n')[1];
        Assert.Equal("key ***", logs.Mask(Guid.Parse(apply.AttemptId), $"key {upload}"));
    }

    [Fact]
    public async Task A_rebuild_keeps_the_host_key()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, _) = await ReadyAsync(server);
        using var _ = http;
        await ProvisionAsync(agent, server);
        var before = await QueryAsync(server, db => db.GuestHostKeys.AsNoTracking().SingleAsync());
        var resource = await server.ResourceAsync(Guest);

        await server.Services.GetRequiredService<Planner>().PlanGuestAsync(resource.Id, CancellationToken.None);
        var plan = await agent.NextAsync("preview");
        Assert.Equal(before.PublicKey, JsonNode.Parse(plan.InputJson)!["variables"]!["octopushy_snippets"]!["hostPublicKey"]!.GetValue<string>());
        Assert.Equal(await PrivateHalfAsync(server, resource.Id), plan.Secrets[SecretNames.GuestHostKey]);
        Assert.Equal(1, await QueryAsync(server, db => db.GuestHostKeys.CountAsync()));
    }

    [Fact]
    public async Task Retiring_the_guest_removes_its_host_key_and_needs_no_upload()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, _) = await ReadyAsync(server);
        using var _ = http;
        await ProvisionAsync(agent, server);
        var resource = await server.ResourceAsync(Guest);
        var entry = GuestHostCredentials.Key(resource.Id);

        server.Repository.Delete("labs/home/proxmox/korriban/guests/krb-app-01");
        server.Commit("Remove the guest");
        await server.SyncAsync();
        var plan = await agent.NextAsync("preview");
        Assert.Null(JsonNode.Parse(plan.InputJson)!["variables"]!["octopushy_snippets"]);
        Assert.DoesNotContain(plan.Secrets.Keys, k => k is SecretNames.GuestHostKey or SecretNames.SnippetsKey);
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(delete: 1), artifacts: FakeAgent.PlanArtifact("destroy"));
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded,
            new JsonObject { ["previous"] = true, ["protection"] = false, ["absent"] = false });
        var apply = await agent.NextAsync("apply");
        Assert.DoesNotContain(apply.Secrets.Keys, k => k is SecretNames.GuestHostKey or SecretNames.SnippetsKey);
        Assert.Null(JsonNode.Parse(apply.InputJson)!["knownHosts"]);
        await agent.ReportAsync(apply, Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-absent"), Outcome.Succeeded, new JsonObject { ["present"] = false });
        await server.TickAsync();

        Assert.Equal(0, await QueryAsync(server, db => db.GuestHostKeys.CountAsync()));
        Assert.Equal(0, await QueryAsync(server, db => db.StoreEntries.CountAsync(e => e.Key == entry)));
        Assert.Equal(0, await QueryAsync(server, db => db.StoreVersions.CountAsync(v => v.Key == entry)));
        Assert.True(await QueryAsync(server, db => db.SecretResolutions.AnyAsync(r => r.Key == entry)));
        // The installation's key serves its other guests, so it stays.
        Assert.Equal(1, await QueryAsync(server, db => db.InstallationSnippetKeys.CountAsync()));
    }

    [Fact]
    public async Task A_declared_pin_wins_and_makes_no_key_of_octopushys()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed(declaredPin: true));
        var (agent, http, _) = await ReadyAsync(server);
        using var _ = http;
        var (plan, apply, _) = await ProvisionAsync(agent, server);
        Assert.Null(JsonNode.Parse(plan.InputJson)!["variables"]!["octopushy_snippets"]);
        Assert.DoesNotContain(plan.Secrets.Keys.Concat(apply.Secrets.Keys), k => k is SecretNames.GuestHostKey or SecretNames.SnippetsKey);
        Assert.Null(JsonNode.Parse(apply.InputJson)!["knownHosts"]);
        Assert.Equal(0, await QueryAsync(server, db => db.GuestHostKeys.CountAsync()));

        await ScheduleAsync(server);
        var preview = await ConfigureRunTests.PreviewAsync(agent);
        Assert.Equal("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIL4yMxTo2ZC6CYBxvdtnHHtR5HBTjQeL+w9jI3rj+PyY",
            JsonNode.Parse(preview.InputJson)!["host"]!["hostKey"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_pipeline_stage_on_the_guest_pins_octopushys_key_too()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed(start: Definitions.PipelineDefinitionTests.Repository()));
        await server.SetSecretAsync("web/password", "a-web-password");
        var (agent, http, _) = await ReadyAsync(server);
        using var _ = http;
        await ProvisionAsync(agent, server);
        var guest = await server.ResourceAsync(Guest);
        var hostKey = await QueryAsync(server, db => db.GuestHostKeys.AsNoTracking().SingleAsync());
        var privateHalf = await PrivateHalfAsync(server, guest.Id);
        await ScheduleAsync(server);
        await ConfigureRunTests.PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();

        await server.Services.GetRequiredService<Planner>().SchedulePipelinesAsync(CancellationToken.None);
        var run = await server.LatestRunAsync(Definitions.PipelineDefinitionTests.Address, Operations.Pipeline);
        Assert.DoesNotContain(RunContext.Parse(run.Context).Secrets.Keys, k => RunContext.BareRole(k) == SecretNames.AnsibleHostKey);
        var plan = await agent.NextAsync("preview");
        Assert.Equal("opentofu", plan.Executor);
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        var preview = await agent.NextAsync("preview");
        Assert.Equal("ansible", preview.Executor);
        Assert.Equal(hostKey.PublicKey, JsonNode.Parse(preview.InputJson)!["host"]!["hostKey"]!.GetValue<string>());
        var (outputs, artifact) = ConfigureRunTests.Preview();
        await agent.ReportAsync(preview, Outcome.Succeeded, outputs, artifacts: artifact);
        var state = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { version = 4, serial = 1, lineage = "l", resources = new[] { new { name = "r" } } });
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject(), artifacts: new Artifact
        {
            Name = TofuStateStore.ArtifactName,
            Content = Google.Protobuf.ByteString.CopyFrom(state),
            Sha256 = ArtifactStore.Sha256(state),
        });
        var playbook = await agent.NextAsync("run");
        Assert.Equal(hostKey.PublicKey, JsonNode.Parse(playbook.InputJson)!["host"]!["hostKey"]!.GetValue<string>());
        foreach (var assignment in new[] { plan, preview, playbook })
            Assert.DoesNotContain(Everything(assignment), v => v.Contains(privateHalf));
    }

    [Fact]
    public void Known_hosts_take_only_key_lines_and_pin_them_for_any_name()
    {
        var uploads = new SnippetsContext("home/korriban", "octopushy-snippets", "10.1.0.10", 22);
        Assert.Equal("* ssh-ed25519 AAAA\n", AssignmentBuilder.KnownHosts("ssh-ed25519 AAAA root@korriban\n", uploads));
        Assert.Equal("* ssh-ed25519 AAAA\n* ecdsa-sha2-nistp256 BBBB\n", AssignmentBuilder.KnownHosts("ssh-ed25519 AAAA\necdsa-sha2-nistp256 BBBB c", uploads));
        foreach (var bad in new[] { "", "\n", "AAAA", "ssh-ed25519 AA$A", "@revoked ssh-ed25519 AAAA", "* ssh-ed25519 AAAA" })
            Assert.Throws<CredentialUnavailableException>(() => AssignmentBuilder.KnownHosts(bad, uploads));
    }
}
