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
using Repository = Octopushy.Tests.Definitions.ServiceDefinitionTests;

namespace Octopushy.Tests.Server;

/// <summary>A service is a configure run on a resource of its own: its order, turns, failures, approval, removal and trust.</summary>
[Collection(PostgresCollection.Name)]
public class ServiceRunTests(PostgresFixture postgres)
{
    private const string Guest = Repository.Guest;
    private const string Alpha = $"{Guest}/alpha";
    private const string Beta = $"{Guest}/beta";
    private const string ServicesFolder = "labs/home/proxmox/korriban/guests/krb-svc-01/services";
    private const string SshKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXk\n-----END OPENSSH PRIVATE KEY-----\n";
    private const string HostKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIL4yMxTo2ZC6CYBxvdtnHHtR5HBTjQeL+w9jI3rj+PyY";
    private const string Removal = "remove: { playbook: remove.yml, destructive: true, removal: true }";
    private const string WithSetup = "\nsetup: { recipe: bare }";
    private const string Stateful = "\nstateful: true";

    private static TestRepository Lab(string guest = "", string actions = "", string labPolicy = "")
    {
        var test = Repository.Repository(Repository.GuestDefinition + guest, actions.Length == 0 ? "" : "  " + actions);
        test.Write("labs/home/lab.yaml", $"schemaVersion: 1\nexecution: {{ agentLabels: [home] }}\n{labPolicy}\n");
        test.Write("recipes/bare/recipe.yaml", """
            schemaVersion: 1
            operation: configure
            executor: ansible
            root: ../../modules/ansible/svc
            actions:
              configure: { playbook: deploy.yml, destructive: false, onChange: true }
            """);
        return test;
    }

    private static async Task<(FakeAgent Agent, HttpClient Http)> ProvisionAsync(ServerHarness server)
    {
        await server.SetSecretAsync("svc/ssh-key", SshKey);
        await server.SetPlainAsync("svc/host-key", HostKey);
        await server.SetSecretAsync("svc/password", "a-service-password");
        var http = server.CreateClient();
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await http.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded);
        await server.TickAsync();
        return (agent, http);
    }

    private static async Task ScheduleAsync(ServerHarness server)
    {
        var planner = server.Services.GetRequiredService<Planner>();
        await planner.ScheduleConfigurationsAsync(CancellationToken.None);
        await planner.ScheduleServicesAsync(CancellationToken.None);
    }

    private static async Task<Assignment> PreviewAsync(FakeAgent agent)
    {
        var content = Encoding.UTF8.GetBytes(new JsonObject { ["schemaVersion"] = 1 }.ToJsonString());
        var outputs = new JsonObject
        {
            ["action"] = "deploy",
            ["playbook"] = "deploy.yml",
            ["tasks"] = new JsonArray("Install"),
            ["hostKeyFingerprints"] = new JsonArray("ssh-ed25519 SHA256:abc"),
            ["manifest"] = new JsonObject { ["commit"] = "c", ["destructive"] = false },
        };
        var assignment = await agent.NextKeepingOthersAsync("preview");
        Assert.Equal("ansible", assignment.Executor);
        await agent.ReportAsync(assignment, Outcome.Succeeded, outputs,
            artifacts: new Artifact { Name = AssignmentBuilder.PreviewArtifact, Content = ByteString.CopyFrom(content), Sha256 = ArtifactStore.Sha256(content) });
        return assignment;
    }

    private static JsonObject Recap() => new() { ["exitCode"] = 0, ["tasks"] = 3, ["recap"] = new JsonObject { ["ok"] = 2, ["changed"] = 1 } };

    private static async Task CompleteAsync(FakeAgent agent)
    {
        await PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap());
    }

    private static async Task DeployAllAsync(ServerHarness server, FakeAgent agent)
    {
        await ScheduleAsync(server);
        await PreviewAsync(agent);
        await PreviewAsync(agent);
        await server.TickAsync();
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap());
        await server.TickAsync();
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap());
        await server.TickAsync();
    }

    private static Task<int> ConfigureRunsAsync(ServerHarness server, string address) =>
        server.QueryAsync(db => db.Runs.CountAsync(r => r.Operation == Operations.Configure && db.Resources.Any(x => x.Id == r.ResourceId && x.Address == address)));

    [Fact]
    public async Task Services_wait_for_setup_and_then_take_turns_on_their_guest()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Lab(WithSetup));
        var (agent, http) = await ProvisionAsync(server);
        using var _ = http;

        await ScheduleAsync(server);
        Assert.Equal(1, await ConfigureRunsAsync(server, Guest));
        Assert.Equal(0, await ConfigureRunsAsync(server, Alpha));
        await CompleteAsync(agent);
        await server.TickAsync();

        await ScheduleAsync(server);
        Assert.Equal(1, await ConfigureRunsAsync(server, Alpha));
        Assert.Equal(1, await ConfigureRunsAsync(server, Beta));
        await PreviewAsync(agent);
        await PreviewAsync(agent);
        await server.TickAsync();

        var first = await agent.NextAsync("run");
        await server.TickAsync();
        var runs = new[] { await server.LatestRunAsync(Alpha, Operations.Configure), await server.LatestRunAsync(Beta, Operations.Configure) };
        var waiting = Assert.Single(runs, r => r.Steps.Single(s => s.Name == StepNames.Playbook).Status == StepStatuses.Pending);
        Assert.Contains("which is changing the same guest", waiting.Detail);

        await agent.ReportAsync(first, Outcome.Succeeded, Recap());
        await server.TickAsync();
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap());
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Alpha, Operations.Configure)).Status);
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Beta, Operations.Configure)).Status);
    }

    [Fact]
    public async Task A_service_run_carries_its_ports_and_no_proxmox_credential()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Lab());
        var (agent, http) = await ProvisionAsync(server);
        using var _ = http;

        await ScheduleAsync(server);
        var preview = await PreviewAsync(agent);
        var input = JsonNode.Parse(preview.InputJson)!;
        var address = input["context"]!["address"]!.GetValue<string>();
        Assert.StartsWith(Guest + "/", address);
        Assert.Equal(address.Split('/')[^1], input["context"]!["service"]!.GetValue<string>());
        Assert.NotNull(input["context"]!["ports"]![0]!["port"]);
        Assert.EndsWith($"/services/{address.Split('/')[^1]}", input["guestDirectory"]!.GetValue<string>());
        Assert.NotNull(input["manifest"]!["ports"]);
        Assert.Empty(preview.Secrets);

        await PreviewAsync(agent);
        await server.TickAsync();
        var playbook = await agent.NextAsync("run");
        Assert.Equal(new[] { SecretNames.AnsibleSshKey, SecretNames.AnsibleVar("app_password") }, playbook.Secrets.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("a-service-password", playbook.Secrets[SecretNames.AnsibleVar("app_password")]);
    }

    [Fact]
    public async Task A_failed_service_marks_neither_its_guest_nor_its_sibling()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Lab());
        var (agent, http) = await ProvisionAsync(server);
        using var _ = http;

        await ScheduleAsync(server);
        await PreviewAsync(agent);
        await PreviewAsync(agent);
        await server.TickAsync();
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Failed, error: "a task failed");
        await server.TickAsync();
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap());
        await server.TickAsync();

        var statuses = new[] { await StatusAsync(http, Alpha), await StatusAsync(http, Beta) };
        Assert.Contains("failed", statuses);
        Assert.Contains("in-sync", statuses);
        var guest = await http.GetFromJsonAsync<JsonObject>($"/api/guests/{(await server.ResourceAsync(Guest)).Id}");
        Assert.Equal("in-sync", guest!["guest"]!["status"]!.GetValue<string>());

        var inventory = (await http.GetFromJsonAsync<JsonObject>("/api/inventory"))!.ToJsonString();
        Assert.Contains(Alpha, inventory);
    }

    private static async Task<string> StatusAsync(HttpClient http, string address)
    {
        var inventory = await http.GetFromJsonAsync<JsonObject>("/api/inventory");
        var guest = inventory!["labs"]!.AsArray().SelectMany(l => l!["installations"]!.AsArray()).SelectMany(i => i!["guests"]!.AsArray()).Single(g => g!["address"]!.GetValue<string>() == Guest);
        return guest!["services"]!.AsArray().Single(s => s!["address"]!.GetValue<string>() == address)!["status"]!.GetValue<string>();
    }

    [Fact]
    public async Task A_service_waiting_for_approval_holds_back_no_other()
    {
        var lab = Lab();
        lab.Write(Repository.AlphaPath, Repository.Service("policy: { approval: manual }"));
        await using var server = await ServerHarness.StartAsync(postgres, lab);
        var (agent, http) = await ProvisionAsync(server);
        using var _ = http;

        await ScheduleAsync(server);
        await PreviewAsync(agent);
        await PreviewAsync(agent);
        await server.TickAsync();
        Assert.Equal(RunStatuses.AwaitingApproval, (await server.LatestRunAsync(Alpha, Operations.Configure)).Status);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap());
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Beta, Operations.Configure)).Status);
    }

    [Fact]
    public async Task Approving_a_guest_never_approves_its_service()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Lab(WithSetup, labPolicy: "policy: { approval: manual }"));
        await server.SetSecretAsync("svc/ssh-key", SshKey);
        await server.SetPlainAsync("svc/host-key", HostKey);
        await server.SetSecretAsync("svc/password", "a-service-password");
        var http = server.CreateClient();
        using var _ = http;
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await http.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        var agent = await server.ConnectAgentAsync("home");
        await server.SyncAndTrustAsync(http);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();
        await ApproveAsync(server, http, Guest, Operations.Provision, StepNames.Approval);
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded);
        await server.TickAsync();

        await ScheduleAsync(server);
        await PreviewAsync(agent);
        await server.TickAsync();
        await ApproveAsync(server, http, Guest, Operations.Configure, StepNames.Approval);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap());
        await server.TickAsync();

        await ScheduleAsync(server);
        await PreviewAsync(agent);
        await PreviewAsync(agent);
        await server.TickAsync();
        Assert.Equal(RunStatuses.AwaitingApproval, (await server.LatestRunAsync(Alpha, Operations.Configure)).Status);
        Assert.Equal(RunStatuses.AwaitingApproval, (await server.LatestRunAsync(Beta, Operations.Configure)).Status);
    }

    private static async Task ApproveAsync(ServerHarness server, HttpClient http, string address, string operation, string name)
    {
        var run = await server.LatestRunAsync(address, operation);
        var step = run.Steps.Single(s => s.Name == name);
        (await http.PostAsJsonAsync($"/api/runs/{run.Id}/steps/{step.Id}/approve", new { planSha256 = step.PlanSha256, note = (string?)null })).EnsureSuccessStatusCode();
        await server.TickAsync();
    }

    [Fact]
    public async Task Each_input_hash_gets_one_automatic_run()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Lab());
        var (agent, http) = await ProvisionAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PreviewAsync(agent);
        await PreviewAsync(agent);
        await server.TickAsync();
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Failed, error: "failed");
        await server.TickAsync();
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap());
        await server.TickAsync();

        await ScheduleAsync(server);
        Assert.Equal(1, await ConfigureRunsAsync(server, Alpha));
        Assert.Equal(1, await ConfigureRunsAsync(server, Beta));

        server.Repository.Write(Repository.AlphaPath, Repository.Service().Replace("greeting: hello", "greeting: again"));
        server.Commit("Change alpha");
        await server.SyncAndTrustAsync(http);
        await ScheduleAsync(server);
        Assert.Equal(2, await ConfigureRunsAsync(server, Alpha));
        Assert.Equal(1, await ConfigureRunsAsync(server, Beta));
    }

    [Fact]
    public async Task A_service_is_not_run_by_hand_on_a_guest_that_is_not_ready()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Lab(WithSetup));
        var (_, http) = await ProvisionAsync(server);
        using var _ = http;
        var response = await http.PostAsJsonAsync($"/api/services/{(await server.ResourceAsync(Alpha)).Id}/run", new { action = (string?)null });
        Assert.False(response.IsSuccessStatusCode);
        Assert.Contains("guest's setup", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_deleted_folder_with_no_removal_action_changes_nothing_until_someone_forgets_it()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Lab());
        var (agent, http) = await ProvisionAsync(server);
        using var _ = http;
        await DeployAllAsync(server, agent);

        server.Repository.Delete($"{ServicesFolder}/alpha");
        server.Commit("Remove alpha");
        await server.SyncAsync();
        await ScheduleAsync(server);
        Assert.Equal(1, await ConfigureRunsAsync(server, Alpha));
        Assert.NotNull((await server.ResourceAsync(Alpha)).MissingSince);
        Assert.Null((await server.ResourceAsync(Alpha)).RetiredAt);
        var id = (await server.ResourceAsync(Alpha)).Id;
        Assert.Equal("missing", (await http.GetFromJsonAsync<JsonObject>($"/api/services/{id}"))!["service"]!["status"]!.GetValue<string>());

        (await http.PostAsync($"/api/services/{id}/forget", null)).EnsureSuccessStatusCode();
        Assert.NotNull(await server.QueryAsync(db => db.Resources.Where(r => r.Id == id).Select(r => r.RetiredAt).FirstAsync()));
        Assert.Equal(1, await ConfigureRunsAsync(server, Alpha));
    }

    [Fact]
    public async Task Removal_waits_for_a_person_even_when_everything_is_automatic()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Lab(actions: Removal));
        var (agent, http) = await ProvisionAsync(server);
        using var _ = http;
        await DeployAllAsync(server, agent);
        var applied = (await server.LatestRunAsync(Alpha, Operations.Configure)).Commit;

        server.Repository.Delete($"{ServicesFolder}/alpha");
        server.Commit("Remove alpha");
        await server.SyncAsync();
        await PreviewAsync(agent);
        await server.TickAsync();
        var run = await server.LatestRunAsync(Alpha, Operations.Configure);
        Assert.Equal(applied, run.Commit);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Contains("unless destructive is set to automatic explicitly", run.Detail);

        await ApproveAsync(server, http, Alpha, Operations.Configure, StepNames.Approval);
        var playbook = await agent.NextAsync("run");
        Assert.Equal("remove.yml", JsonNode.Parse(playbook.InputJson)!["playbook"]!.GetValue<string>());
        await agent.ReportAsync(playbook, Outcome.Succeeded, Recap());
        await server.TickAsync();
        Assert.NotNull(await server.QueryAsync(db => db.Resources.Where(r => r.Address == Alpha).Select(r => r.RetiredAt).FirstAsync()));
        Assert.NotEmpty((await server.LatestRunAsync(Alpha, Operations.Configure)).Steps);
    }

    [Fact]
    public async Task An_explicit_destructive_automatic_lets_a_removal_go()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Lab(actions: Removal, labPolicy: "policy: { destructive: automatic }"));
        var (agent, http) = await ProvisionAsync(server);
        using var _ = http;
        await DeployAllAsync(server, agent);

        server.Repository.Delete($"{ServicesFolder}/alpha");
        server.Commit("Remove alpha");
        await server.SyncAsync();
        await PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, Recap());
        await server.TickAsync();
        Assert.NotNull(await server.QueryAsync(db => db.Resources.Where(r => r.Address == Alpha).Select(r => r.RetiredAt).FirstAsync()));
    }

    [Fact]
    public async Task Restoring_the_folder_cancels_a_removal_that_has_not_started()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Lab(actions: Removal));
        var (agent, http) = await ProvisionAsync(server);
        using var _ = http;
        await DeployAllAsync(server, agent);
        var alpha = await File.ReadAllTextAsync(Path.Combine(server.Repository.Root, Repository.AlphaPath));

        server.Repository.Delete($"{ServicesFolder}/alpha");
        server.Commit("Remove alpha");
        await server.SyncAsync();
        await PreviewAsync(agent);
        await server.TickAsync();

        server.Repository.Write(Repository.AlphaPath, alpha.TrimEnd('\n'));
        server.Commit("Keep alpha");
        await server.SyncAndTrustAsync(http);
        await server.TickAsync();
        Assert.Equal(RunStatuses.Cancelled, (await server.LatestRunAsync(Alpha, Operations.Configure)).Status);
        var resource = await server.ResourceAsync(Alpha);
        Assert.Null(resource.MissingSince);
        Assert.Null(resource.RetiredAt);
    }

    [Fact]
    public async Task A_later_commit_keeps_a_pending_removal_and_a_rejected_one_stays_rejected()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Lab(actions: Removal));
        var (agent, http) = await ProvisionAsync(server);
        using var _ = http;
        await DeployAllAsync(server, agent);

        server.Repository.Delete($"{ServicesFolder}/alpha");
        server.Commit("Remove alpha");
        await server.SyncAsync();
        await PreviewAsync(agent);
        await server.TickAsync();
        var removal = await server.LatestRunAsync(Alpha, Operations.Configure);

        server.Repository.Write("labs/home/proxmox/korriban/notes.txt", "another commit");
        server.Commit("Something else");
        await server.SyncAsync();
        await server.TickAsync();
        Assert.Equal(RunStatuses.AwaitingApproval, (await server.QueryAsync(db => db.Runs.FirstAsync(r => r.Id == removal.Id))).Status);

        var step = removal.Steps.Single(s => s.Name == StepNames.Approval);
        (await http.PostAsJsonAsync($"/api/runs/{removal.Id}/steps/{step.Id}/reject", new { note = "not yet" })).EnsureSuccessStatusCode();
        await server.TickAsync();
        var count = await ConfigureRunsAsync(server, Alpha);
        server.Repository.Write("labs/home/proxmox/korriban/notes.txt", "one more commit");
        server.Commit("Something else again");
        await server.SyncAsync();
        await server.TickAsync();
        Assert.Equal(count, await ConfigureRunsAsync(server, Alpha));

        // A person can still ask for it.
        var id = (await server.ResourceAsync(Alpha)).Id;
        Assert.True((await http.GetFromJsonAsync<JsonObject>($"/api/services/{id}"))!["hasRemoval"]!.GetValue<bool>());
        (await http.PostAsJsonAsync($"/api/services/{id}/run", new { action = (string?)null })).EnsureSuccessStatusCode();
        Assert.Equal(count + 1, await ConfigureRunsAsync(server, Alpha));
    }

    [Fact]
    public async Task A_restored_folder_does_not_deploy_what_is_already_there()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Lab(actions: Removal));
        var (agent, http) = await ProvisionAsync(server);
        using var _ = http;
        await DeployAllAsync(server, agent);
        var alpha = await File.ReadAllTextAsync(Path.Combine(server.Repository.Root, Repository.AlphaPath));

        server.Repository.Delete($"{ServicesFolder}/alpha");
        server.Commit("Remove alpha");
        await server.SyncAsync();
        await PreviewAsync(agent);
        server.Repository.Write(Repository.AlphaPath, alpha.TrimEnd('\n'));
        server.Commit("Keep alpha");
        await server.SyncAndTrustAsync(http);
        await server.TickAsync();

        var count = await ConfigureRunsAsync(server, Alpha);
        await ScheduleAsync(server);
        Assert.Equal(count, await ConfigureRunsAsync(server, Alpha));
    }

    [Fact]
    public async Task Deleting_a_guest_proposes_no_removal_for_its_services()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Lab(actions: Removal));
        var (agent, http) = await ProvisionAsync(server);
        using var _ = http;
        await DeployAllAsync(server, agent);
        var runs = await ConfigureRunsAsync(server, Alpha);

        server.Repository.Delete("labs/home/proxmox/korriban/guests/krb-svc-01");
        server.Commit("Remove the guest");
        await server.SyncAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(delete: 1), artifacts: FakeAgent.PlanArtifact("destroy"));
        await server.TickAsync();
        Assert.Equal(runs, await ConfigureRunsAsync(server, Alpha));
        var retire = await server.LatestRunAsync(Guest, Operations.Retire);
        Assert.Equal(RunStatuses.AwaitingApproval, retire.Status);
        Assert.Contains(Alpha, retire.Detail);
    }

    [Fact]
    public async Task A_renamed_guest_takes_its_services_with_it()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Lab());
        var (agent, http) = await ProvisionAsync(server);
        using var _ = http;
        await DeployAllAsync(server, agent);
        var id = (await server.ResourceAsync(Alpha)).Id;

        var folder = Path.Combine(server.Repository.Root, "labs/home/proxmox/korriban/guests");
        Directory.Move(Path.Combine(folder, "krb-svc-01"), Path.Combine(folder, "krb-svc-02"));
        var file = Path.Combine(folder, "krb-svc-02/guest.yaml");
        await File.WriteAllTextAsync(file, "renamedFrom: krb-svc-01\n" + await File.ReadAllTextAsync(file));
        server.Commit("Rename the guest");
        await server.SyncAndTrustAsync(http);

        var renamed = await server.ResourceAsync("home/korriban/krb-svc-02/alpha");
        Assert.Equal((id, "home/korriban/krb-svc-02", (DateTimeOffset?)null), (renamed.Id, renamed.ParentAddress, renamed.MissingSince));
    }

    [Fact]
    public async Task A_new_machine_is_set_up_again_and_its_services_after_that()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Lab(WithSetup));
        var (agent, http) = await ProvisionAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await CompleteAsync(agent);
        await server.TickAsync();
        await DeployAllAsync(server, agent);

        var resource = await server.ResourceAsync(Guest);
        await server.Services.GetRequiredService<Planner>().PlanGuestAsync(resource.Id, CancellationToken.None);
        var replace = new JsonObject
        {
            ["hasChanges"] = true,
            ["changes"] = new JsonArray(new JsonObject
            {
                ["address"] = $"{GuestVmPlan.ContainerResourceType}.guest",
                ["type"] = GuestVmPlan.ContainerResourceType,
                ["name"] = "guest",
                ["actions"] = new JsonArray("delete", "create"),
                ["vmId"] = 312,
                ["node"] = "korriban",
            }),
            ["counts"] = new JsonObject { ["create"] = 0, ["update"] = 0, ["delete"] = 0, ["replace"] = 1 },
            ["toolVersion"] = "1.13.1",
        };
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, replace, artifacts: FakeAgent.PlanArtifact("replace"));
        await server.TickAsync();
        await ApproveAsync(server, http, Guest, Operations.Provision, StepNames.Approval);
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded, new JsonObject { ["previous"] = true, ["protection"] = false, ["absent"] = false });
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded);
        await server.TickAsync();

        var alpha = await ConfigureRunsAsync(server, Alpha);
        await ScheduleAsync(server);
        Assert.Equal(alpha, await ConfigureRunsAsync(server, Alpha));
        Assert.Equal("waiting", await StatusAsync(http, Alpha));
        await CompleteAsync(agent);
        await server.TickAsync();
        await ScheduleAsync(server);
        Assert.Equal(alpha + 1, await ConfigureRunsAsync(server, Alpha));
    }

    [Fact]
    public async Task Retiring_a_guest_waits_for_a_person_and_takes_its_services_with_it()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Lab(labPolicy: "policy: { destructive: automatic }"));
        var (agent, http) = await ProvisionAsync(server);
        using var _ = http;
        await DeployAllAsync(server, agent);

        var text = await File.ReadAllTextAsync(Path.Combine(server.Repository.Root, Repository.GuestPath));
        server.Repository.Write(Repository.GuestPath, ("lifecycle: retired\n" + text).TrimEnd('\n'));
        server.Commit("Retire the guest");
        await server.SyncAndTrustAsync(http);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(delete: 1), artifacts: FakeAgent.PlanArtifact("destroy"));
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest, Operations.Retire);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Contains(Alpha, run.Detail);

        await ApproveAsync(server, http, Guest, Operations.Retire, StepNames.Approval);
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded, new JsonObject { ["previous"] = true, ["protection"] = false, ["absent"] = false });
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded);
        await agent.ReportAsync(await agent.NextAsync("verify-absent"), Outcome.Succeeded, new JsonObject { ["present"] = false });
        await server.TickAsync();
        Assert.NotNull(await server.QueryAsync(db => db.Resources.Where(r => r.Address == Beta).Select(r => r.RetiredAt).FirstAsync()));
    }

    [Fact]
    public async Task A_protected_secret_waits_for_trust_and_goes_only_to_the_code_trusted()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Lab());
        var (agent, http) = await ProvisionAsync(server);
        using var _ = http;
        await server.WithDbAsync(async db =>
        {
            (await db.StoreEntries.FirstAsync(e => e.Key == "svc/password")).Protected = true;
            await db.SaveChangesAsync();
        });

        await ScheduleAsync(server);
        await server.TickAsync();
        var alpha = await server.LatestRunAsync(Alpha, Operations.Configure);
        Assert.Equal(RunStatuses.Blocked, alpha.Status);
        Assert.Contains("Waiting for trust", alpha.Detail);

        var id = (await server.ResourceAsync(Alpha)).Id;
        var detail = await http.GetFromJsonAsync<JsonObject>($"/api/services/{id}");
        Assert.Equal("svc/password", detail!["trust"]!["keys"]![0]!.GetValue<string>());
        var digest = detail["trust"]!["codeDigest"]!.GetValue<string>();
        Assert.False((await http.PostAsJsonAsync($"/api/resources/{id}/trust-configure", new { codeDigest = "not-what-was-shown" })).IsSuccessStatusCode);
        (await http.PostAsJsonAsync($"/api/resources/{id}/trust-configure", new { codeDigest = digest })).EnsureSuccessStatusCode();
        await server.TickAsync();

        // The sibling binds the same key under its own digest and has no grant of its own.
        Assert.Equal(RunStatuses.Blocked, (await server.LatestRunAsync(Beta, Operations.Configure)).Status);
        await PreviewAsync(agent);
        await server.TickAsync();
        var playbook = await agent.NextAsync("run");
        Assert.Equal("a-service-password", playbook.Secrets[SecretNames.AnsibleVar("app_password")]);
    }

    [Fact]
    public async Task A_stateful_guest_is_guarded_once_a_playbook_has_worked_and_not_before()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Lab(WithSetup + Stateful));
        var (agent, http) = await ProvisionAsync(server);
        using var _ = http;

        await ScheduleAsync(server);
        await PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Failed, error: "failed");
        await server.TickAsync();
        Assert.False(await ArmedAsync(server));

        var setup = (await server.ResourceAsync(Guest)).Id;
        (await http.PostAsJsonAsync($"/api/guests/{setup}/configure", new { action = "configure" })).EnsureSuccessStatusCode();
        await CompleteAsync(agent);
        await server.TickAsync();
        Assert.True(await ArmedAsync(server));
    }

    private static async Task<bool> ArmedAsync(ServerHarness server)
    {
        var resource = await server.ResourceAsync(Guest);
        return await server.QueryAsync(db => StatefulGuard.ArmedAsync(db, resource, CancellationToken.None));
    }
}
