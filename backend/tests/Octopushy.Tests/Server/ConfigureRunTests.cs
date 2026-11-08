using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Repository = Octopushy.Tests.Definitions.GuestConfigurationTests;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class ConfigureRunTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    internal const string SshKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXk\n-----END OPENSSH PRIVATE KEY-----\n";
    private const string HostKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIL4yMxTo2ZC6CYBxvdtnHHtR5HBTjQeL+w9jI3rj+PyY";

    internal static async Task<(ServerHarness Server, FakeAgent Agent, HttpClient Http)> ProvisionedAsync(
        ServerHarness server, bool setKeys = true)
    {
        if (setKeys)
            await SetKeysAsync(server);
        var http = server.CreateClient();
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await http.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded);
        await server.TickAsync();
        return (server, agent, http);
    }

    private static async Task SetKeysAsync(ServerHarness server)
    {
        await server.SetSecretAsync("access/app/ssh-key", SshKey);
        await server.SetPlainAsync("access/app/host-key", HostKey);
        await server.SetSecretAsync("app/tailscale", "tskey-secret-value");
        await server.SetSecretAsync("app/git-key", "git-key-value");
    }

    /// <summary>Sets the guest's policy once it is provisioned: a manual guest would hold its provision run too.</summary>
    private static async Task SetPolicyAsync(ServerHarness server, string policy)
    {
        const string file = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
        server.Repository.Write(file, File.ReadAllText(Path.Combine(server.Repository.Root, file)) + policy);
        server.Commit("Set the policy");
        await server.SyncAsync();
        await server.TickAsync();
    }

    private static Task ScheduleAsync(ServerHarness server) =>
        server.Services.GetRequiredService<Planner>().ScheduleConfigurationsAsync(CancellationToken.None);

    internal static (JsonObject Outputs, Artifact Artifact) Preview(JsonObject? prepared = null)
    {
        var content = Encoding.UTF8.GetBytes(new JsonObject { ["schemaVersion"] = 1, ["prepared"] = prepared?.DeepClone() }.ToJsonString());
        var outputs = new JsonObject
        {
            ["action"] = "setup",
            ["playbook"] = "site.yml",
            ["tasks"] = new JsonArray("Install tools"),
            ["prepared"] = prepared?.DeepClone(),
            ["toolVersion"] = "ansible-playbook [core 2.21.5]",
            ["hostKeyFingerprints"] = new JsonArray("ssh-ed25519 SHA256:abc"),
            ["manifest"] = new JsonObject { ["commit"] = "c", ["destructive"] = false, ["host"] = "ops@10.1.0.31:22", ["secrets"] = new JsonObject { ["ansible.ssh-key"] = "access/app/ssh-key v1" } },
        };
        return (outputs, new Artifact { Name = AssignmentBuilder.PreviewArtifact, Content = ByteString.CopyFrom(content), Sha256 = ArtifactStore.Sha256(content) });
    }

    internal static async Task<Assignment> PreviewAsync(FakeAgent agent, JsonObject? prepared = null)
    {
        var (outputs, artifact) = Preview(prepared);
        var assignment = await agent.NextAsync("preview");
        Assert.Equal("ansible", assignment.Executor);
        await agent.ReportAsync(assignment, Outcome.Succeeded, outputs, artifacts: artifact);
        return assignment;
    }

    internal static JsonObject Recap(string? outcome = null, string? detail = null) => new()
    {
        ["exitCode"] = 0,
        ["tasks"] = 3,
        ["recap"] = new JsonObject { ["ok"] = 2, ["changed"] = 1 },
        ["outcome"] = outcome,
        ["detail"] = detail,
    };

    private static JsonObject Input(Assignment assignment) => JsonNode.Parse(assignment.InputJson)!.AsObject();

    [Fact]
    public async Task A_provisioned_guest_is_configured_once_with_its_secrets_only_after_the_preview()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;

        await ScheduleAsync(server);
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal("revision", run.Reason);
        Assert.Equal(new[] { "preview", "approval", "playbook" }, run.Steps.OrderBy(s => s.Ordinal).Select(s => s.Name));

        var previewed = await PreviewAsync(agent, new JsonObject { ["version"] = "1.2.3" });
        Assert.Empty(previewed.Secrets);
        var input = Input(previewed);
        Assert.Equal(HostKey, input["host"]!["hostKey"]!.GetValue<string>());
        Assert.Null(input["sshKey"]);
        Assert.Equal("ansible.var.tailscale_key", input["secretVars"]!["tailscale_key"]!.GetValue<string>());
        Assert.Equal("hello", input["vars"]!["greeting"]!.GetValue<string>());
        Assert.Equal("prepare.yml", input["prepare"]!.GetValue<string>());
        Assert.Contains("access/app/ssh-key v1", input["manifest"]!.ToJsonString());
        Assert.DoesNotContain("tskey-secret-value", input.ToJsonString());

        var playbook = await agent.NextAsync("run");
        Assert.Equal("ansible", playbook.Executor);
        Assert.Equal(new[] { SecretNames.AnsibleFile("git_key_src"), SecretNames.AnsibleSshKey, SecretNames.AnsibleVar("tailscale_key") },
            playbook.Secrets.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(SshKey, playbook.Secrets[SecretNames.AnsibleSshKey]);
        Assert.Equal("tskey-secret-value", playbook.Secrets[SecretNames.AnsibleVar("tailscale_key")]);
        Assert.Single(playbook.Artifacts);
        Assert.Equal(AssignmentBuilder.PreviewArtifact, playbook.Artifacts[0].Name);
        Assert.DoesNotContain("octopushy@pve", playbook.InputJson);
        await agent.ReportAsync(playbook, Outcome.Succeeded, Recap());
        await server.TickAsync();

        run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        var resource = await server.ResourceAsync(Guest);
        var configuration = await server.QueryAsync(db => db.GuestConfigurations.AsNoTracking().SingleAsync(c => c.ResourceId == resource.Id));
        Assert.Equal(run.InputsHash, configuration.AppliedHash);
        Assert.Equal("setup", configuration.AppliedAction);
        Assert.Contains("1.2.3", configuration.Snapshot);

        // One run per input hash.
        await ScheduleAsync(server);
        Assert.Equal(run.Id, (await server.LatestRunAsync(Guest, Operations.Configure)).Id);
        var guest = (await http.GetFromJsonAsync<JsonObject>($"/api/guests/{resource.Id}"))!;
        Assert.True(guest["configuration"]!["inSync"]!.GetValue<bool>());
        Assert.Equal(2, guest["configuration"]!["actions"]!.AsArray().Count);

        var view = (await http.GetFromJsonAsync<JsonObject>($"/api/runs/{run.Id}"))!;
        Assert.DoesNotContain("tskey-secret-value", view.ToJsonString());
        var preview = view["steps"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == "preview")!["configurePreview"]!;
        Assert.Equal("setup", preview["action"]!.GetValue<string>());
        Assert.Equal("Install tools", preview["tasks"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task A_guest_waits_for_its_provisioning_before_it_is_configured()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        await SetKeysAsync(server);
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync();
        await ScheduleAsync(server);
        Assert.Empty(await server.QueryAsync(db => db.Runs.Where(r => r.Operation == Operations.Configure).ToListAsync()));
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Failed, error: "no address");
        await server.TickAsync();
        await ScheduleAsync(server);
        Assert.Empty(await server.QueryAsync(db => db.Runs.Where(r => r.Operation == Operations.Configure).ToListAsync()));
    }

    [Fact]
    public async Task An_action_declared_destructive_waits_under_a_manual_destructive_policy()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository("policy: { destructive: manual }", destructive: "true"));
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PreviewAsync(agent);
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        Assert.Contains("destructive", approval.Detail);
        var digest = approval.PlanSha256!;
        Assert.Empty(agent.TakeAssignments());

        var wrong = await http.PostAsJsonAsync($"/api/runs/{run.Id}/steps/{approval.Id}/approve", new { planSha256 = new string('0', 64) });
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        (await http.PostAsJsonAsync($"/api/runs/{run.Id}/steps/{approval.Id}/approve", new { planSha256 = digest })).EnsureSuccessStatusCode();
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap());
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Configure)).Status);
    }

    [Fact]
    public async Task An_action_declared_safe_runs_unattended_under_a_manual_destructive_policy()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository("policy: { destructive: manual }"));
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap());
    }

    [Fact]
    public async Task An_action_with_its_own_automatic_approval_runs_unattended_on_a_manual_guest()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        await SetPolicyAsync(server, "policy:\n  approval: manual\n  actions:\n    setup: { approval: automatic }\n");
        await ScheduleAsync(server);
        await PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap());
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        Assert.Equal("policy", approval.DecidedBy);
        Assert.Contains($"set by {Guest}, action setup", approval.DecisionNote);
    }

    [Theory]
    // The override is of approval only: a destructive action still follows destructive, which follows the guest.
    [InlineData("policy:\n  approval: manual\n  actions:\n    setup: { approval: automatic }\n", "true", "destructive is manual")]
    [InlineData("policy:\n  actions:\n    setup: { approval: manual }\n", "false", $"Approval is manual (set by {Guest}, action setup)")]
    public async Task An_action_waits_when_its_own_approval_or_destructive_says_so(string policy, string destructive, string detail)
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository(destructive: destructive));
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        await SetPolicyAsync(server, policy);
        await ScheduleAsync(server);
        await PreviewAsync(agent);
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Contains(detail, run.Steps.Single(s => s.Name == StepNames.Approval).Detail);
        Assert.Empty(agent.TakeAssignments());
    }

    [Fact]
    public async Task Manual_approval_holds_every_run_of_a_guest()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        const string guest = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
        var text = File.ReadAllText(Path.Combine(server.Repository.Root, guest));
        server.Repository.Write(guest, text + "policy: { approval: manual }\n");
        server.Commit("Hold changes");
        await server.SyncAsync();
        await server.TickAsync();
        // Policy is not an input of a plan, so nothing else moved.
        await ScheduleAsync(server);
        await PreviewAsync(agent);
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Contains("Approval is manual", run.Detail);
    }

    [Fact]
    public async Task A_missing_pin_blocks_the_run_and_a_secret_cannot_pose_as_one()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, agent, http) = await ProvisionedAsync(server, setKeys: false);
        using var _ = http;
        await server.SetSecretAsync("access/app/ssh-key", SshKey);
        await server.SetSecretAsync("app/tailscale", "tskey-secret-value");
        await server.SetSecretAsync("app/git-key", "git-key-value");
        await ScheduleAsync(server);
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("access/app/host-key: missing", run.Detail);
        Assert.Empty(agent.TakeAssignments());

        await server.SetSecretAsync("access/app/host-key", "this secret must not reach a preview");
        await server.TickAsync();
        run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("a host key is public", run.Detail);
        Assert.Empty(agent.TakeAssignments());
    }

    [Fact]
    public async Task A_new_installation_gets_no_configure_run_until_it_is_trusted()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        await server.WithDbAsync(async db =>
        {
            db.InstallationTrusts.RemoveRange(await db.InstallationTrusts.ToListAsync());
            await db.SaveChangesAsync();
        });
        await ScheduleAsync(server);
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.StartsWith("Waiting for trust", run.Detail);
        Assert.Empty(agent.TakeAssignments());
    }

    [Fact]
    public async Task A_declared_outcome_waits_for_a_person_and_allows_one_approved_run()
    {
        const string outcomes = """
            outcomes:
              deferred:
                description: Work is active on the guest.
                approve:
                  vars: { allow_busy: true }
            """;
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository(extraWorkflow: outcomes));
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PreviewAsync(agent, new JsonObject { ["version"] = "1.2.3" });
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap("deferred", "Three threads."));
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        var gate = run.Steps.Single(s => s.Name == StepNames.OutcomeApproval);
        Assert.Contains("Three threads.", gate.Detail);
        Assert.Contains("Work is active", gate.Detail);
        Assert.Contains("allow_busy", gate.Detail);
        Assert.Null(await server.QueryAsync(db => db.GuestConfigurations.AsNoTracking().Select(c => c.Snapshot).SingleAsync()));

        (await http.PostAsJsonAsync($"/api/runs/{run.Id}/steps/{gate.Id}/approve", new { planSha256 = gate.PlanSha256 })).EnsureSuccessStatusCode();
        var again = await agent.NextAsync("run");
        Assert.True(Input(again)["approvedVars"]!["allow_busy"]!.GetValue<bool>());
        Assert.Contains(AssignmentBuilder.PreviewArtifact, again.Artifacts.Select(a => a.Name));
        await agent.ReportAsync(again, Outcome.Succeeded, Recap("deferred", "Still busy."));
        await server.TickAsync();

        run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("one approved attempt", run.Detail);
        Assert.Null(await server.QueryAsync(db => db.GuestConfigurations.AsNoTracking().Select(c => c.Snapshot).SingleAsync()));
    }

    [Fact]
    public async Task An_approved_rerun_that_succeeds_promotes_what_was_prepared()
    {
        const string outcomes = """
            outcomes:
              deferred: { approve: { vars: { allow_busy: true } } }
            """;
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository(extraWorkflow: outcomes));
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PreviewAsync(agent, new JsonObject { ["version"] = "1.2.3" });
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap("deferred"));
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        var gate = run.Steps.Single(s => s.Name == StepNames.OutcomeApproval);
        (await http.PostAsJsonAsync($"/api/runs/{run.Id}/steps/{gate.Id}/approve", new { planSha256 = gate.PlanSha256 })).EnsureSuccessStatusCode();
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap());
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Configure)).Status);
        Assert.Contains("1.2.3", await server.QueryAsync(db => db.GuestConfigurations.AsNoTracking().Select(c => c.Snapshot).SingleAsync()));
    }

    [Fact]
    public async Task A_rejected_outcome_cancels_the_run()
    {
        const string outcomes = """
            outcomes:
              deferred: { approve: { vars: { allow_busy: true } } }
            """;
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository(extraWorkflow: outcomes));
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap("deferred"));
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        var gate = run.Steps.Single(s => s.Name == StepNames.OutcomeApproval);
        (await http.PostAsJsonAsync($"/api/runs/{run.Id}/steps/{gate.Id}/reject", new { })).EnsureSuccessStatusCode();
        Assert.Equal(RunStatuses.Cancelled, (await server.LatestRunAsync(Guest, Operations.Configure)).Status);
    }

    [Theory]
    [InlineData("failed", RunStatuses.Failed)]
    [InlineData("succeeded", RunStatuses.Succeeded)]
    public async Task An_outcome_without_an_approval_ends_as_declared_and_says_so(string result, string status)
    {
        var outcomes = $$"""
            outcomes:
              pending-reboot: { result: {{result}} }
            """;
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository(extraWorkflow: outcomes));
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap("pending-reboot", "A reboot is due."));
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(status, run.Status);
        Assert.Contains("pending-reboot", run.Detail);
        Assert.Contains("A reboot is due.", run.Detail);
    }

    [Theory]
    [InlineData("failed", RunStatuses.Failed)]
    [InlineData("succeeded", RunStatuses.Succeeded)]
    public async Task What_the_server_writes_from_a_reported_outcome_is_masked_whole(string result, string status)
    {
        var outcomes = $$"""
            outcomes:
              pending-reboot: { result: {{result}} }
            """;
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository(extraWorkflow: outcomes));
        // A secret that neither reported value holds, but the text joining them does.
        const string Secret = "pending-reboot: A reboot is due";
        await server.SetSecretAsync("access/app/ssh-key", SshKey);
        await server.SetPlainAsync("access/app/host-key", HostKey);
        await server.SetSecretAsync("app/tailscale", Secret);
        await server.SetSecretAsync("app/git-key", "git-key-value");
        var (_, agent, http) = await ProvisionedAsync(server, setKeys: false);
        using var _ = http;
        await ScheduleAsync(server);
        await PreviewAsync(agent);
        var playbook = await agent.NextAsync("run");
        Assert.Contains(Secret, playbook.Secrets.Values);
        await agent.ReportAsync(playbook, Outcome.Succeeded, Recap("pending-reboot", "A reboot is due"));
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(status, run.Status);
        var attempt = run.Steps.SelectMany(s => s.Attempts).Single(a => a.Id == Guid.Parse(playbook.AttemptId));
        Assert.DoesNotContain(Secret, attempt.Outputs);
        Assert.DoesNotContain(Secret, run.Detail ?? "");
        Assert.All(run.Steps, s => Assert.DoesNotContain(Secret, s.Detail ?? ""));
        Assert.Contains("***", (run.Detail ?? "") + string.Concat(run.Steps.Select(s => s.Detail)));
        Assert.DoesNotContain(Secret, await http.GetStringAsync($"/api/runs/{run.Id}"));
    }

    [Fact]
    public async Task A_result_after_the_agent_could_only_say_uncertain_is_taken_once_and_its_replay_changes_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PreviewAsync(agent);
        var playbook = await agent.NextAsync("run");
        var id = Guid.Parse(playbook.AttemptId);
        Task<Attempt> AttemptAsync() => server.QueryAsync(db => db.Attempts.AsNoTracking().SingleAsync(a => a.Id == id));

        // As an agent sends when its stored result could not be read, and then, once it can, the result it stored.
        const string Lost = "the agent lost the attempt's stored result";
        Assert.True(await agent.ReportAsync(playbook, Outcome.Uncertain, new JsonObject(), Lost));
        var uncertain = await AttemptAsync();
        Assert.Equal((AttemptStatuses.Uncertain, Lost, false), (uncertain.Status, uncertain.Error, uncertain.OutputsWithheld));
        Assert.True(await agent.ReportAsync(playbook, Outcome.Uncertain, new JsonObject(), Lost));
        Assert.Equal(uncertain.FinishedAt, (await AttemptAsync()).FinishedAt);

        Assert.True(await agent.ReportAsync(playbook, Outcome.Succeeded, Recap()));
        var taken = await AttemptAsync();
        Assert.Equal((AttemptStatuses.Succeeded, (string?)null, true), (taken.Status, taken.Error, taken.LateResult));
        Assert.NotNull(taken.Outputs);
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Configure)).Status);

        // And the success, sent again, changes nothing either.
        Assert.True(await agent.ReportAsync(playbook, Outcome.Succeeded, Recap()));
        Assert.Equal(taken.FinishedAt, (await AttemptAsync()).FinishedAt);
    }

    [Fact]
    public async Task A_lost_playbook_is_uncertain_keeps_the_guest_and_is_not_retried()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PreviewAsync(agent);
        var playbook = await agent.NextAsync("run");
        await server.Engine.OnStartedAsync(agent.Id, Guid.Parse(playbook.AttemptId), CancellationToken.None);
        await server.WithDbAsync(db => db.Attempts.Where(a => a.Id == Guid.Parse(playbook.AttemptId))
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        await server.TickAsync();
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.Uncertain, run.Status);
        Assert.Equal(StepStatuses.Uncertain, run.Steps.Single(s => s.Name == StepNames.Playbook).Status);
        Assert.Single(run.Steps.Single(s => s.Name == StepNames.Playbook).Attempts);
        Assert.Empty(agent.TakeAssignments());
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsJsonAsync($"/api/runs/{run.Id}/cancel", new { })).StatusCode);

        // A person has to close it before anything else touches the guest.
        var resource = await server.ResourceAsync(Guest);
        var blocked = await http.PostAsJsonAsync($"/api/guests/{resource.Id}/configure", new { action = "setup" });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        (await http.PostAsync($"/api/runs/{run.Id}/close", null)).EnsureSuccessStatusCode();
        (await http.PostAsJsonAsync($"/api/guests/{resource.Id}/configure", new { action = "setup" })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_failure_waits_for_a_new_commit_or_a_person()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Failed, Recap(), "the task \"Install tools\" failed");
        await server.TickAsync();
        var failed = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.Failed, failed.Status);

        await ScheduleAsync(server);
        Assert.Equal(failed.Id, (await server.LatestRunAsync(Guest, Operations.Configure)).Id);

        server.Repository.Write("modules/ansible/app/site.yml", "- hosts: all\n# fixed\n");
        server.Commit("Fix the playbook");
        await server.SyncAsync();
        await ScheduleAsync(server);
        var next = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.NotEqual(failed.Id, next.Id);
        Assert.Equal("revision", next.Reason);
    }

    [Fact]
    public async Task A_new_commit_replaces_a_run_that_has_not_started_changing_anything()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository("policy: { destructive: manual }", destructive: "true"));
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PreviewAsync(agent);
        await server.TickAsync();
        var waiting = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.AwaitingApproval, waiting.Status);

        server.Repository.Write("modules/ansible/app/site.yml", "- hosts: all\n# changed\n");
        server.Commit("Change the playbook");
        await server.SyncAsync();
        await ScheduleAsync(server);

        var first = await server.QueryAsync(db => db.Runs.AsNoTracking().SingleAsync(r => r.Id == waiting.Id));
        Assert.Equal(RunStatuses.Cancelled, first.Status);
        Assert.NotEqual(waiting.Id, (await server.LatestRunAsync(Guest, Operations.Configure)).Id);
    }

    [Fact]
    public async Task A_snapshot_action_needs_one_and_reuses_it_without_preparing()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        var resource = await server.ResourceAsync(Guest);
        var refused = await http.PostAsJsonAsync($"/api/guests/{resource.Id}/configure", new { action = "update-system" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        await ScheduleAsync(server);
        await PreviewAsync(agent, new JsonObject { ["version"] = "1.2.3" });
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap());
        await server.TickAsync();

        (await http.PostAsJsonAsync($"/api/guests/{resource.Id}/configure", new { action = "update-system" })).EnsureSuccessStatusCode();
        await server.TickAsync();
        var preview = await agent.NextAsync("preview");
        var input = Input(preview);
        Assert.Equal("1.2.3", input["prepared"]!["version"]!.GetValue<string>());
        Assert.Null(input["prepare"]);
        Assert.Equal("update-system", input["action"]!.GetValue<string>());
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal("manual", run.Reason);
    }

    [Fact]
    public async Task A_workflows_automatic_action_runs_unattended_on_a_manual_guest()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.WithWorkflowApproval("", "automatic"));
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PreviewAsync(agent, new JsonObject { ["version"] = "1.2.3" });
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap());
        await server.TickAsync();
        await SetPolicyAsync(server, "policy: { approval: manual }\n");

        var resource = await server.ResourceAsync(Guest);
        (await http.PostAsJsonAsync($"/api/guests/{resource.Id}/configure", new { action = "update-system" })).EnsureSuccessStatusCode();
        await server.TickAsync();
        await PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap());
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Contains("set by workflow app@1.0, action update-system", run.Steps.Single(s => s.Name == StepNames.Approval).DecisionNote);
    }

    [Fact]
    public async Task An_unknown_action_is_refused()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, _, http) = await ProvisionedAsync(server);
        using var _ = http;
        var resource = await server.ResourceAsync(Guest);
        var response = await http.PostAsJsonAsync($"/api/guests/{resource.Id}/configure", new { action = "nothing" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_guest_that_was_replaced_is_configured_again()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap());
        await server.TickAsync();
        var first = await server.LatestRunAsync(Guest, Operations.Configure);

        const string guest = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
        var text = File.ReadAllText(Path.Combine(server.Repository.Root, guest));
        server.Repository.Write(guest, text.Replace("memory: 2048", "memory: 4096"));
        server.Commit("Replace the VM");
        await server.SyncAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(replace: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded);
        await server.TickAsync();
        await ScheduleAsync(server);
        Assert.NotEqual(first.Id, (await server.LatestRunAsync(Guest, Operations.Configure)).Id);
    }

    [Fact]
    public async Task A_rollback_configures_the_commit_the_branch_returned_to()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap());
        await server.TickAsync();
        var first = await server.LatestRunAsync(Guest, Operations.Configure);

        server.Repository.Write("modules/ansible/app/site.yml", "- hosts: all\n# a mistake\n");
        server.Commit("A change that is rolled back");
        await server.SyncAsync();
        await server.WithDbAsync(async db =>
        {
            // The branch moves back before the mistake runs.
            var settings = await db.RepositorySettings.SingleAsync();
            settings.HeadCommit = first.Commit;
            settings.HeadRevisionId = first.RevisionId;
            await db.SaveChangesAsync();
        });
        await ScheduleAsync(server);
        var latest = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(first.Commit, latest.Commit);
    }

    [Fact]
    public async Task A_guest_behind_its_definition_cannot_be_configured_by_hand()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap());
        await server.TickAsync();

        const string guest = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
        var text = File.ReadAllText(Path.Combine(server.Repository.Root, guest));
        server.Repository.Write(guest, text.Replace("memory: 2048", "memory: 4096"));
        server.Commit("Resize");
        await server.SyncAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Failed, error: "the host refused");
        await server.TickAsync();

        var resource = await server.ResourceAsync(Guest);
        var response = await http.PostAsJsonAsync($"/api/guests/{resource.Id}/configure", new { action = "setup" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_token_that_went_to_Proxmox_work_cannot_reach_a_playbook_after_its_installation_changes()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap());
        await server.TickAsync();

        // The definitions stop naming the old key, as removing its installation would.
        await server.SetSecretAsync("proxmox/korriban/next-read-only", "octopushy@pve!agent=next");
        await server.SetSecretAsync("proxmox/korriban/next-privileged", "octopushy@pve!writer=next");
        const string installation = "labs/home/proxmox/korriban/installation.yaml";
        const string guest = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
        var root = server.Repository.Root;
        server.Repository.Write(installation, File.ReadAllText(Path.Combine(root, installation))
            .Replace("proxmox/korriban/read-only-token", "proxmox/korriban/next-read-only")
            .Replace("proxmox/korriban/privileged-token", "proxmox/korriban/next-privileged"));
        server.Repository.Write(guest, File.ReadAllText(Path.Combine(root, guest))
            .Replace("store: app/tailscale", "store: proxmox/korriban/privileged-token"));
        server.Commit("Reuse a token that no definition names");
        await server.SyncAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded);
        await server.TickAsync();
        await ScheduleAsync(server);
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("a playbook cannot use it", run.Detail);
        Assert.Empty(agent.TakeAssignments());
    }

    [Fact]
    public async Task A_removed_installations_token_cannot_reach_a_playbook_even_if_it_was_never_used()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap());
        await server.TickAsync();

        await server.SetSecretAsync("proxmox/other/read-only", "octopushy@pve!other=read");
        await server.SetSecretAsync("proxmox/other/privileged", "octopushy@pve!other=write");
        const string other = "labs/home/proxmox/other/installation.yaml";
        server.Repository.Write(other, """
            schemaVersion: 1
            type: standalone
            connection:
              endpoint: https://10.1.0.11:8006/
              tls: { verify: false }
              readOnlyToken: { store: proxmox/other/read-only }
              privilegedToken: { store: proxmox/other/privileged }
            hosts:
              other:
                datastores: { images: local, disks: local-lvm }
                bridges: [vmbr0]
            """);
        server.Commit("Add an installation");
        await server.SyncAsync();
        await server.TickAsync();
        Assert.False(await server.QueryAsync(db => db.SecretResolutions.AnyAsync(r => r.Key == "proxmox/other/privileged")));

        server.Repository.Delete(other);
        const string guest = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
        server.Repository.Write(guest, File.ReadAllText(Path.Combine(server.Repository.Root, guest))
            .Replace("store: app/tailscale", "store: proxmox/other/privileged"));
        server.Commit("Reuse a token that no definition names");
        await server.SyncAsync();
        await ScheduleAsync(server);
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("a playbook cannot use it", run.Detail);
        Assert.Empty(agent.TakeAssignments());
    }

    [Fact]
    public async Task The_controller_masks_each_line_of_a_key_as_well()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PreviewAsync(agent);
        var playbook = await agent.NextAsync("run");
        var logs = server.Services.GetRequiredService<AttemptLogStore>();
        var attempt = Guid.Parse(playbook.AttemptId);
        Assert.Equal("line *** printed alone", logs.MaskDiagnostic(attempt, "line b3BlbnNzaC1rZXk printed alone"));
        logs.RegisterSecrets(attempt, ["ab12\ncd34"]);
        Assert.Equal("*** and ***", logs.MaskDiagnostic(attempt, "ab12 and cd34"));
    }

    [Fact]
    public async Task A_preview_that_changed_since_it_was_approved_is_refused()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository("policy: { destructive: manual }", destructive: "true"));
        var (_, agent, http) = await ProvisionedAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PreviewAsync(agent);
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        (await http.PostAsJsonAsync($"/api/runs/{run.Id}/steps/{approval.Id}/approve", new { planSha256 = approval.PlanSha256 })).EnsureSuccessStatusCode();
        await server.WithDbAsync(db => db.Steps.Where(s => s.Id == approval.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.PlanSha256, new string('0', 64))));
        await server.TickAsync();
        await server.TickAsync();
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "run");
        run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("not the one that was approved", run.Detail);
    }
}
