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
using Octopushy.Tests.Definitions;
using Repository = Octopushy.Tests.Definitions.GuestConfigurationTests;

namespace Octopushy.Tests.Server;

/// <summary>A guest whose SSH host key is Octopushy's: generated, delivered in a snippet, pinned, and renewed for a new machine.</summary>
[Collection(PostgresCollection.Name)]
public class ManagedHostKeyTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    private const string InstallationFile = "labs/home/proxmox/korriban/installation.yaml";
    private const string WorkflowFile = "workflows/app/workflow.yaml";
    private const string NodeKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIKiijI7PL8NeaSlRIfsOf1JSG69ZMULksy817NNUwsNZ root@korriban";
    private const string NodeKeyStore = "proxmox/korriban/ssh-host-key";

    /// <summary>
    /// The configure fixture as a guest Octopushy creates: its own login key and no pin of its own, on a workflow whose
    /// machine stage delivers one, and a host set up for it.
    /// </summary>
    internal static TestRepository Managed(bool declaredPin = false, TestRepository? start = null)
    {
        var test = start ?? Repository.Repository();
        var guest = File.ReadAllText(Path.Combine(test.Root, GuestFile)).Replace("  sshKey: { store: access/app/ssh-key }\n", "");
        test.Write(GuestFile, declaredPin ? guest : guest.Replace("  hostKey: { store: access/app/host-key }\n", ""));
        var workflow = File.ReadAllText(Path.Combine(test.Root, WorkflowFile));
        Assert.Contains("    root: ../../modules/opentofu/test-vm\n", workflow);
        test.Write(WorkflowFile, workflow.Replace("    root: ../../modules/opentofu/test-vm\n", "    root: ../../modules/opentofu/test-vm\n    hostKey: managed\n"));
        test.Write(InstallationFile, File.ReadAllText(Path.Combine(test.Root, InstallationFile)) + $"""
            snippets:
              hosts:
                korriban: {"{"} hostKey: {"{"} store: {NodeKeyStore} {"}"} {"}"}

            """);
        return test;
    }

    private static Task ScheduleAsync(ServerHarness server) =>
        server.Services.GetRequiredService<Planner>().ScheduleConfigurationsAsync(CancellationToken.None);

    private static Task<string> PrivateHalfAsync(ServerHarness server, Guid resource, int version = 1) => server.QueryAsync(async _ =>
    {
        await using var scope = server.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<VariableStore>().GetVersionInternalAsync(GuestHostCredentials.Key(resource), version, CancellationToken.None))!;
    });

    private static Task<List<GuestHostKey>> KeysAsync(ServerHarness server) =>
        server.QueryAsync(db => db.GuestHostKeys.AsNoTracking().OrderBy(k => k.Version).ToListAsync());

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

    private static async Task PlanAgainAsync(ServerHarness server)
    {
        var resource = await server.ResourceAsync(Guest);
        await server.Services.GetRequiredService<Planner>().PlanGuestAsync(resource.Id, CancellationToken.None);
    }

    /// <summary>A plan that replaces the guest's own VM, as the agent summarizes one.</summary>
    private static JsonObject Replaces() => new()
    {
        ["hasChanges"] = true,
        ["changes"] = new JsonArray(new JsonObject
        {
            ["address"] = $"{GuestVmPlan.VmResourceType}.guest",
            ["type"] = GuestVmPlan.VmResourceType,
            ["name"] = "guest",
            ["actions"] = new JsonArray("delete", "create"),
            ["actionReason"] = "replace_because_cannot_update",
            ["vmId"] = 310,
            ["node"] = "korriban",
        }),
        ["counts"] = new JsonObject { ["create"] = 0, ["update"] = 0, ["delete"] = 0, ["replace"] = 1 },
        ["toolVersion"] = "1.13.1",
    };

    private static string SnippetKeyOf(Assignment plan) =>
        JsonNode.Parse(plan.InputJson)!["variables"]!["octopushy_snippets"]!["hostPublicKey"]!.GetValue<string>();

    private static IEnumerable<string> Everything(Assignment assignment) => assignment.Secrets.Values.Append(assignment.InputJson);

    [Fact]
    public async Task The_plan_gets_the_private_host_key_and_the_apply_gets_the_upload_key_and_the_hosts_pin()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, snippets) = await ReadyAsync(server);
        using var _ = http;
        var (plan, apply, verify) = await ProvisionAsync(agent, server);
        var resource = await server.ResourceAsync(Guest);
        var hostKey = Assert.Single(await KeysAsync(server));
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
        // The apply's saved plan holds the private half, so the agent and the controller get it to mask, and only to mask:
        // no environment variable maps it, and the input does not hold it.
        Assert.Equal(privateHalf, apply.Secrets[SecretNames.HostKeyMask(1)]);
        Assert.DoesNotContain(applyInput["secretEnv"]!.AsObject(), p => p.Value!.GetValue<string>().StartsWith(SecretNames.HostKeyMaskPrefix, StringComparison.Ordinal));
        Assert.DoesNotContain(privateHalf, apply.InputJson);
        Assert.DoesNotContain(Everything(verify), v => v.Contains(privateHalf) || v.Contains(apply.Secrets[SecretNames.SnippetsKey]));
        Assert.Equal(snippets["publicKey"]!.GetValue<string>(),
            (await server.QueryAsync(db => db.InstallationSnippetKeys.AsNoTracking().SingleAsync())).PublicKey);

        // Each release is on record, and the key is pinned from now on, by the run that delivered it.
        var provision = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(provision.Id, (await KeysAsync(server)).Single().DeliveredRunId);
        var released = await server.QueryAsync(db => db.SecretResolutions.AsNoTracking().Select(r => new { r.Key, r.AttemptId }).ToListAsync());
        Assert.Contains(released, r => r.Key == GuestHostCredentials.Key(resource.Id) && r.AttemptId == Guid.Parse(plan.AttemptId));
        // Released to the plan to write, and to the apply to mask; nothing else holds it.
        Assert.Contains(released, r => r.Key == GuestHostCredentials.Key(resource.Id) && r.AttemptId == Guid.Parse(apply.AttemptId));
        Assert.DoesNotContain(released, r => r.Key == GuestHostCredentials.Key(resource.Id) && r.AttemptId != Guid.Parse(plan.AttemptId)
            && r.AttemptId != Guid.Parse(apply.AttemptId));
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
        var hostKey = Assert.Single(await KeysAsync(server));
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
        await server.WithDbAsync(db => db.GuestHostKeys.ExecuteUpdateAsync(u => u.SetProperty(k => k.DeliveredAt, (DateTimeOffset?)null)));

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
        var hostKey = Assert.Single(await KeysAsync(server));
        var privateHalf = await PrivateHalfAsync(server, resource.Id);

        var guestPage = JsonNode.Parse(await http.GetStringAsync($"/api/guests/{resource.Id}"))!["hostKey"]!;
        Assert.Equal(hostKey.PublicKey, guestPage["pinned"]!["publicKey"]!.GetValue<string>());
        Assert.Equal(hostKey.Fingerprint, guestPage["pinned"]!["fingerprint"]!.GetValue<string>());
        Assert.NotNull(guestPage["pinned"]!["deliveredAt"]);
        Assert.Null(guestPage["next"]);

        var installationPage = JsonNode.Parse(await http.GetStringAsync($"/api/installations/{installation.Id}"))!["snippets"]!;
        var publicKey = installationPage["publicKey"]!.GetValue<string>();
        Assert.Equal(snippets["publicKey"]!.GetValue<string>(), publicKey);
        Assert.EndsWith("octopushy-snippets@home/korriban", publicKey);
        Assert.True(installationPage["declared"]!.GetValue<bool>());
        Assert.Contains($"OCTOPUSHY_SNIPPETS_KEY='{publicKey}' OCTOPUSHY_SNIPPETS_USER='octopushy-snippets' OCTOPUSHY_SNIPPETS_DATASTORE='local' ",
            installationPage["command"]!.GetValue<string>());
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
        Assert.Equal("data: ***", logs.MaskDiagnostic(Guid.Parse(plan.AttemptId), $"data: {line}"));
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        var apply = await agent.NextAsync("apply");
        var upload = apply.Secrets[SecretNames.SnippetsKey].Split('\n')[1];
        Assert.Equal("key ***", logs.MaskDiagnostic(Guid.Parse(apply.AttemptId), $"key {upload}"));
    }

    [Fact]
    public async Task A_plan_that_keeps_the_machine_keeps_its_host_key()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, _) = await ReadyAsync(server);
        using var _ = http;
        await ProvisionAsync(agent, server);
        var before = Assert.Single(await KeysAsync(server));
        var resource = await server.ResourceAsync(Guest);

        await PlanAgainAsync(server);
        var plan = await agent.NextAsync("preview");
        Assert.Equal(before.PublicKey, SnippetKeyOf(plan));
        Assert.Equal(await PrivateHalfAsync(server, resource.Id), plan.Secrets[SecretNames.GuestHostKey]);
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(update: 1, machine: false), artifacts: FakeAgent.PlanArtifact("update"));
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded);
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
        Assert.Equal(before.PublicKey, Assert.Single(await KeysAsync(server)).PublicKey);
    }

    [Fact]
    public async Task A_new_machine_gets_a_new_host_key_and_the_pin_moves_only_when_its_apply_finishes()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, _) = await ReadyAsync(server);
        using var _ = http;
        await ProvisionAsync(agent, server);
        var resource = await server.ResourceAsync(Guest);
        var first = Assert.Single(await KeysAsync(server));

        // The plan replaces the VM, and it carried the key the old machine was given: it is made again with a new one.
        await PlanAgainAsync(server);
        var stale = await agent.NextAsync("preview");
        Assert.Equal(first.PublicKey, SnippetKeyOf(stale));
        await agent.ReportAsync(stale, Outcome.Succeeded, Replaces(), artifacts: FakeAgent.PlanArtifact("replace, old key"));
        var plan = await agent.NextAsync("preview");
        var keys = await KeysAsync(server);
        Assert.Equal([1, 2], keys.Select(k => k.Version));
        var second = keys[1];
        Assert.NotEqual(first.PublicKey, second.PublicKey);
        Assert.Null(second.DeliveredAt);
        Assert.Equal(second.PublicKey, SnippetKeyOf(plan));
        Assert.Equal(await PrivateHalfAsync(server, resource.Id, 2), plan.Secrets[SecretNames.GuestHostKey]);
        Assert.NotEqual(await PrivateHalfAsync(server, resource.Id, 1), plan.Secrets[SecretNames.GuestHostKey]);
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(2, RunContext.Parse(run.Context).SecretVersions[SecretNames.GuestHostKey]);

        // Waiting for a person, the old machine still answers with the old key, and the guest's page says what comes next.
        await agent.ReportAsync(plan, Outcome.Succeeded, Replaces(), artifacts: FakeAgent.PlanArtifact("replace, new key"));
        await server.TickAsync();
        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Contains("makes a new machine", run.Steps.Single(s => s.Name == StepNames.Plan).Detail);
        Assert.Equal(first.PublicKey, (await server.QueryAsync(db => GuestHostCredentials.PinnedAsync(db, resource.Id, CancellationToken.None)))!.PublicKey);
        var page = JsonNode.Parse(await http.GetStringAsync($"/api/guests/{resource.Id}"))!["hostKey"]!;
        Assert.Equal(first.Fingerprint, page["pinned"]!["fingerprint"]!.GetValue<string>());
        Assert.Equal(second.Fingerprint, page["next"]!["fingerprint"]!.GetValue<string>());

        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        await server.Engine.ApproveAsync(run.Id, approval.Id, approval.PlanSha256!, "ops", "new machine", CancellationToken.None);
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded,
            new JsonObject { ["absent"] = false, ["previous"] = true, ["protection"] = false });
        var apply = await agent.NextAsync("apply");
        Assert.Equal("* ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIKiijI7PL8NeaSlRIfsOf1JSG69ZMULksy817NNUwsNZ\n", JsonNode.Parse(apply.InputJson)!["knownHosts"]!.GetValue<string>());
        // Until that apply finishes, the pin is the old machine's.
        Assert.Null((await KeysAsync(server))[1].DeliveredAt);
        await agent.ReportAsync(apply, Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        keys = await KeysAsync(server);
        Assert.NotNull(keys[1].DeliveredAt);
        Assert.Equal(run.Id, keys[1].DeliveredRunId);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded);
        await server.TickAsync();

        // The new machine's setup pins the new key alone.
        await ScheduleAsync(server);
        var preview = await ConfigureRunTests.PreviewAsync(agent);
        Assert.Equal(second.PublicKey, JsonNode.Parse(preview.InputJson)!["host"]!["hostKey"]!.GetValue<string>());
        Assert.DoesNotContain(first.PublicKey.Split(' ')[1], preview.InputJson);
    }

    [Fact]
    public async Task An_apply_spends_its_key_before_it_is_handed_out_and_the_pin_waits_for_success()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, _) = await ReadyAsync(server);
        using var _ = http;
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        var apply = await agent.NextAsync("apply");
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        // Spent when it went out, before any result: delivered only once it succeeds.
        var key = Assert.Single(await KeysAsync(server));
        Assert.NotNull(key.SpentAt);
        Assert.Equal(run.Id, key.SpentRunId);
        Assert.Null(key.DeliveredAt);
        await agent.ReportAsync(apply, Outcome.Failed, error: "the VM did not come up");
        await server.TickAsync();
        key = Assert.Single(await KeysAsync(server));
        Assert.NotNull(key.SpentAt);
        Assert.Null(key.DeliveredAt);
        Assert.Null(await server.QueryAsync(db => GuestHostCredentials.PinnedAsync(db, key.ResourceId, CancellationToken.None)));
    }

    [Fact]
    public async Task An_approved_apply_whose_key_was_spent_elsewhere_since_is_refused()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, _) = await ReadyAsync(server);
        using var _ = http;
        await ProvisionAsync(agent, server);
        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, Replaces(), artifacts: FakeAgent.PlanArtifact("replace, old key"));
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, Replaces(), artifacts: FakeAgent.PlanArtifact("replace, new key"));
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);

        // Whatever spends the new key after the plan, the apply's own check is the last word before a machine gets it.
        await server.WithDbAsync(db => db.GuestHostKeys.Where(k => k.Version == 2)
            .ExecuteUpdateAsync(u => u.SetProperty(k => k.SpentAt, DateTimeOffset.UtcNow).SetProperty(k => k.SpentRunId, Guid.NewGuid())));
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        await server.Engine.ApproveAsync(run.Id, approval.Id, approval.PlanSha256!, "ops", "new machine", CancellationToken.None);
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded,
            new JsonObject { ["absent"] = false, ["previous"] = true, ["protection"] = false });
        await server.TickAsync();
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "apply");
        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Contains("no longer fits", run.Steps.Single(s => s.Name == StepNames.Apply).Detail);
    }

    [Fact]
    public async Task A_plan_after_a_renewal_masks_the_older_key_its_state_holds()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, _) = await ReadyAsync(server);
        using var _ = http;
        await ProvisionAsync(agent, server);
        var resource = await server.ResourceAsync(Guest);
        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, Replaces(), artifacts: FakeAgent.PlanArtifact("replace, old key"));
        var plan = await agent.NextAsync("preview");
        // It writes version 2, and its state still holds the snippet with version 1.
        Assert.Equal(await PrivateHalfAsync(server, resource.Id, 2), plan.Secrets[SecretNames.GuestHostKey]);
        Assert.Equal(await PrivateHalfAsync(server, resource.Id, 1), plan.Secrets[SecretNames.HostKeyMask(1)]);
        Assert.False(plan.Secrets.ContainsKey(SecretNames.HostKeyMask(2)));
        Assert.DoesNotContain(JsonNode.Parse(plan.InputJson)!["secretEnv"]!.AsObject(),
            p => p.Value!.GetValue<string>().StartsWith(SecretNames.HostKeyMaskPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void The_rule_follows_the_newest_key_an_apply_carried_and_never_guesses()
    {
        var run = Guid.NewGuid();
        var other = Guid.NewGuid();
        GuestHostKey Key(int version, bool spent = false, bool delivered = false, Guid? by = null) => new()
        {
            Version = version, PublicKey = $"k{version}", Fingerprint = $"f{version}",
            SpentAt = spent || delivered ? DateTimeOffset.UnixEpoch : null, SpentRunId = spent || delivered ? by ?? other : null,
            DeliveredAt = delivered ? DateTimeOffset.UnixEpoch : null, DeliveredRunId = delivered ? by ?? other : null,
        };
        HostKeyVerdict Keep(int carried, params GuestHostKey[] keys) => GuestHostCredentials.Fit(keys, carried, makesMachine: false, run);
        HostKeyVerdict Make(int carried, params GuestHostKey[] keys) => GuestHostCredentials.Fit(keys, carried, makesMachine: true, run);

        // A new machine: a key no other run's apply carried, or a new one.
        Assert.Equal(HostKeyFit.Fits, Make(1, Key(1)).Fit);
        Assert.Equal(HostKeyFit.Fits, Make(2, Key(1, delivered: true), Key(2, spent: true, by: run)).Fit);
        Assert.Equal(HostKeyFit.Renew, Make(1, Key(1, spent: true)).Fit);
        Assert.Equal(HostKeyFit.Renew, Make(1, Key(1, delivered: true)).Fit);

        // A machine that stays: the newest key an apply carried, delivered or not.
        Assert.Equal(HostKeyFit.Fits, Keep(1, Key(1)).Fit);
        Assert.Equal(HostKeyFit.Fits, Keep(1, Key(1, delivered: true)).Fit);
        Assert.Equal(HostKeyFit.Fits, Keep(2, Key(1, delivered: true), Key(2, spent: true)).Fit);
        Assert.Equal(new HostKeyVerdict(HostKeyFit.Rebase, 2), Keep(3, Key(1, delivered: true), Key(2, spent: true), Key(3)));
        Assert.Equal(new HostKeyVerdict(HostKeyFit.Rebase, 1), Keep(2, Key(1, spent: true), Key(2)));
        Assert.Equal(new HostKeyVerdict(HostKeyFit.Rebase, 1), Keep(2, Key(1, delivered: true), Key(2)));
        // An older key an apply carried, while a newer one may be on the machine: a person decides.
        Assert.Equal(new HostKeyVerdict(HostKeyFit.Reconcile, 2), Keep(1, Key(1, delivered: true), Key(2, spent: true)));
        Assert.Equal(new HostKeyVerdict(HostKeyFit.Reconcile, 3), Keep(2, Key(1, delivered: true), Key(2, spent: true), Key(3, spent: true)));
    }

    [Fact]
    public async Task A_restarted_controller_masks_an_outstanding_attempts_log_lines()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, _) = await ReadyAsync(server);
        using var _ = http;
        await ProvisionAsync(agent, server);
        var resource = await server.ResourceAsync(Guest);
        var line = (await PrivateHalfAsync(server, resource.Id)).Split('\n')[1];
        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(update: 1, machine: false), artifacts: FakeAgent.PlanArtifact("update"));
        var apply = await agent.NextAsync("apply");
        await server.RestartFixtureAsync();

        // What the agent's channel does with a log batch of an attempt this process never sent.
        var attemptId = Guid.Parse(apply.AttemptId);
        var logs = server.Services.GetRequiredService<AttemptLogStore>();
        await server.Services.GetRequiredService<AttemptMasks>().EnsureAsync(attemptId, CancellationToken.None);
        await logs.AppendAsync(attemptId, 0, [(0, "stderr", $"provider diagnostic {line}")], CancellationToken.None);
        var stored = Assert.Single(await logs.ReadAsync(attemptId, -1, 10, CancellationToken.None));
        Assert.Equal("provider diagnostic ***", stored.Text);
    }

    [Fact]
    public async Task Diagnostics_are_withheld_when_a_secret_cannot_be_read_back_to_mask_them()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, _) = await ReadyAsync(server);
        using var _ = http;
        await ProvisionAsync(agent, server);
        var resource = await server.ResourceAsync(Guest);
        var line = (await PrivateHalfAsync(server, resource.Id)).Split('\n')[1];
        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(update: 1, machine: false), artifacts: FakeAgent.PlanArtifact("update"));
        var apply = await agent.NextAsync("apply");
        await server.RestartFixtureAsync();
        // The version the attempt was given is gone from the store, so it cannot be read back to mask with.
        await server.WithDbAsync(db => db.StoreVersions.Where(v => v.Key == GuestHostCredentials.Key(resource.Id)).ExecuteDeleteAsync());

        await agent.ReportAsync(apply, Outcome.Failed, new JsonObject { ["outputs"] = new JsonObject { ["note"] = $"output {line}" } },
            error: $"provider diagnostic {line}");
        await server.TickAsync();
        var attempt = await server.QueryAsync(db => db.Attempts.AsNoTracking().SingleAsync(a => a.Id == Guid.Parse(apply.AttemptId)));
        Assert.Equal(AttemptLogStore.Withheld, attempt.Error);
        // Outputs that cannot be fully masked are not kept at all, so nothing shown is derived from them.
        Assert.Null(attempt.Outputs);
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        var step = run.Steps.Single(s => s.Name == StepNames.Apply);
        Assert.DoesNotContain(line, step.Detail ?? "");
        Assert.DoesNotContain(line, step.Outputs ?? "");
        Assert.DoesNotContain(line, run.Detail ?? "");
        using var resumed = server.CreateClient();
        (await resumed.PostAsJsonAsync("/api/auth/login", new { username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        Assert.DoesNotContain(line, await resumed.GetStringAsync($"/api/runs/{run.Id}"));
    }

    [Fact]
    public async Task A_plan_that_keeps_the_machine_after_a_renewal_carries_the_key_it_was_given()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, _) = await ReadyAsync(server);
        using var _ = http;
        await ProvisionAsync(agent, server);
        var first = Assert.Single(await KeysAsync(server));

        // A replacement nobody approves leaves a new key current, never delivered.
        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, Replaces(), artifacts: FakeAgent.PlanArtifact("replace, old key"));
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, Replaces(), artifacts: FakeAgent.PlanArtifact("replace, new key"));
        await server.TickAsync();
        var rejected = await server.LatestRunAsync(Guest, Operations.Provision);
        await server.Engine.RejectAsync(rejected.Id, rejected.Steps.Single(s => s.Name == StepNames.Approval).Id, "ops", "not now", CancellationToken.None);

        // The next plan keeps the machine, so it carries the key the machine has, not the one nothing delivered.
        await PlanAgainAsync(server);
        var stale = await agent.NextAsync("preview");
        Assert.NotEqual(first.PublicKey, SnippetKeyOf(stale));
        await agent.ReportAsync(stale, Outcome.Succeeded, FakeAgent.Plan(update: 1, machine: false), artifacts: FakeAgent.PlanArtifact("update, new key"));
        var plan = await agent.NextAsync("preview");
        Assert.Equal(first.PublicKey, SnippetKeyOf(plan));
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(1, RunContext.Parse(run.Context).SecretVersions[SecretNames.GuestHostKey]);
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(update: 1, machine: false), artifacts: FakeAgent.PlanArtifact("update, old key"));
        run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Contains("keeps the machine", run.Steps.Single(s => s.Name == StepNames.Plan).Detail);
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded);
        await server.TickAsync();
        Assert.Equal(first.Version, (await server.QueryAsync(db => GuestHostCredentials.PinnedAsync(db, first.ResourceId, CancellationToken.None)))!.Version);
    }

    [Fact]
    public async Task A_run_whose_plans_keep_changing_their_mind_stops()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed());
        var (agent, http, _) = await ReadyAsync(server);
        using var _ = http;
        await ProvisionAsync(agent, server);

        await PlanAgainAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, Replaces(), artifacts: FakeAgent.PlanArtifact("a"));
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(update: 1, machine: false), artifacts: FakeAgent.PlanArtifact("b"));
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, Replaces(), artifacts: FakeAgent.PlanArtifact("c"));
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("no host key fits it", run.Detail);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "apply");
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

        Assert.Equal(0, await server.QueryAsync(db => db.GuestHostKeys.CountAsync()));
        Assert.Equal(0, await server.QueryAsync(db => db.StoreEntries.CountAsync(e => e.Key == entry)));
        Assert.Equal(0, await server.QueryAsync(db => db.StoreVersions.CountAsync(v => v.Key == entry)));
        Assert.True(await server.QueryAsync(db => db.SecretResolutions.AnyAsync(r => r.Key == entry)));
        // The installation's key serves its other guests, so it stays.
        Assert.Equal(1, await server.QueryAsync(db => db.InstallationSnippetKeys.CountAsync()));
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
        Assert.Empty(await KeysAsync(server));

        await ScheduleAsync(server);
        var preview = await ConfigureRunTests.PreviewAsync(agent);
        Assert.Equal("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIL4yMxTo2ZC6CYBxvdtnHHtR5HBTjQeL+w9jI3rj+PyY",
            JsonNode.Parse(preview.InputJson)!["host"]!["hostKey"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_pipeline_stage_on_the_guest_pins_octopushys_key_too()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Managed(start: ServiceDefinitionTests.Repository()));
        await server.SetSecretAsync("web/password", "a-web-password");
        var (agent, http, _) = await ReadyAsync(server);
        using var _ = http;
        await ProvisionAsync(agent, server);
        var guest = await server.ResourceAsync(Guest);
        var hostKey = Assert.Single(await KeysAsync(server));
        var privateHalf = await PrivateHalfAsync(server, guest.Id);
        await ScheduleAsync(server);
        await ConfigureRunTests.PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();

        await server.Services.GetRequiredService<Planner>().SchedulePipelinesAsync(CancellationToken.None);
        var run = await server.LatestRunAsync(ServiceDefinitionTests.Address, Operations.Pipeline);
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
    public async Task The_command_sets_up_the_account_and_datastore_the_installation_names()
    {
        var test = Managed();
        test.Write(InstallationFile, File.ReadAllText(Path.Combine(test.Root, InstallationFile))
            .Replace("snippets:\n", "snippets:\n  datastore: snippets-dir\n  user: uploader\n"));
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (_, http, snippets) = await ReadyAsync(server);
        using var _ = http;
        Assert.Equal(("snippets-dir", "uploader"), (snippets["datastore"]!.GetValue<string>(), snippets["user"]!.GetValue<string>()));
        var command = snippets["command"]!.GetValue<string>();
        Assert.Contains("OCTOPUSHY_SNIPPETS_USER='uploader'", command);
        Assert.Contains("OCTOPUSHY_SNIPPETS_DATASTORE='snippets-dir'", command);
        // The account the uploads log in as is the one the command creates.
        var provision = await server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal("uploader", RunContext.Parse(provision.Context).OpenTofu!.Env[SecretNames.SnippetsUserEnv]);
    }

    [Fact]
    public void The_command_quotes_what_it_passes_so_a_shell_reads_it_back_exactly()
    {
        var key = "ssh-ed25519 AAAA it's $(not run) `nor this` \\ \"quoted\"";
        var command = Octopushy.Server.Api.Views.SnippetsCommand(key, "uploader", "snippets-dir");
        Assert.EndsWith("/deploy/proxmox/snippets.sh)\"", command);
        // Everything before the script's download, with the download swapped for printing what it would get.
        var assignments = command[..command.IndexOf("bash -c", StringComparison.Ordinal)];
        var start = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(assignments + "sh -c 'printf \"%s\\n%s\\n%s\" \"$OCTOPUSHY_SNIPPETS_KEY\" \"$OCTOPUSHY_SNIPPETS_USER\" \"$OCTOPUSHY_SNIPPETS_DATASTORE\"'");
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal($"{key}\nuploader\nsnippets-dir", output);
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

    [Fact]
    public void Only_a_provision_run_that_uploads_carries_the_snippet_roles()
    {
        var proxmox = new ProxmoxContext("https://10.1.0.10:8006/", false, null, "korriban", 310, "krb-app-01", null);
        var configure = new RunContext
        {
            Address = Guest,
            Proxmox = proxmox,
            ManagedHostKey = true,
            Secrets = new() { [SecretNames.AnsibleSshKey] = "k", [SecretNames.SnippetsKey] = "system/installation-snippets/home/korriban" },
            Ansible = new AnsibleContext("app@1.0", "setup", "r", "p", null, false, false, null, "i", "g", "d", "10.1.0.31", 22, "ops", [], [], [], [], null),
        };
        Assert.Contains(CredentialBindings.Validate(configure), p => p.Contains("carries no snippet upload credentials"));
        // A configure run of a guest whose host key is Octopushy's binds no pin of a store entry, and one that is not binds one.
        Assert.Contains(CredentialBindings.Validate(configure with { Secrets = new() { [SecretNames.AnsibleSshKey] = "k", [SecretNames.AnsibleHostKey] = "h" } }),
            p => p.Contains("needs an SSH key and a pinned host key"));
        Assert.Empty(CredentialBindings.Validate(configure with { Secrets = new() { [SecretNames.AnsibleSshKey] = "k" } }));

        var tofu = new OpenTofuContext("s", "r", "i", [], new() { ["PROXMOX_VE_ENDPOINT"] = proxmox.Endpoint, ["PROXMOX_VE_INSECURE"] = "true" },
            new() { ["PROXMOX_VE_API_TOKEN"] = SecretNames.ProxmoxToken, [SecretNames.GuestHostKeyEnv] = SecretNames.GuestHostKey });
        var provision = new RunContext
        {
            Address = Guest,
            Proxmox = proxmox,
            OpenTofu = tofu,
            Secrets = new() { [SecretNames.ProxmoxSharedToken] = "proxmox/korriban/api-token", [SecretNames.GuestHostKey] = "system/guest-host-key/x" },
        };
        // Without an upload, the guest's host key is one more secret an OpenTofu stage may not hold.
        var problems = CredentialBindings.Validate(provision);
        Assert.Contains(problems, p => p.Contains($"'{SecretNames.GuestHostKeyEnv}' is not allowed"));
        Assert.Contains(problems, p => p.Contains("belongs to a snippet upload, and this run makes none"));
    }
}
