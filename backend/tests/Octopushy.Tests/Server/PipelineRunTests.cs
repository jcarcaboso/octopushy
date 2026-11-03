using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;
using Octopushy.Tests.Definitions;
using Repository = Octopushy.Tests.Definitions.PipelineDefinitionTests;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class PipelineRunTests(PostgresFixture postgres)
{
    private const string Guest = PipelineDefinitionTests.Guest;
    private const string Service = PipelineDefinitionTests.Address;
    private const string PipelineFile = PipelineDefinitionTests.PipelineFile;
    private const string Password = "a-web-password";

    private static Task ScheduleAsync(ServerHarness server) => server.Services.GetRequiredService<Planner>().SchedulePipelinesAsync(CancellationToken.None);

    private static Task ScheduleConfigurationsAsync(ServerHarness server) => server.Services.GetRequiredService<Planner>().ScheduleConfigurationsAsync(CancellationToken.None);

    private static Task<Guid> DeployAsync(ServerHarness server, Guid id) => server.Services.GetRequiredService<Planner>().DeployServiceAsync(id, CancellationToken.None);

    private static Artifact State(long serial, string lineage = "lineage")
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 4, serial, lineage, resources = new[] { new { name = "r" } } });
        return new Artifact { Name = TofuStateStore.ArtifactName, Content = ByteString.CopyFrom(bytes), Sha256 = ArtifactStore.Sha256(bytes) };
    }

    private static (JsonObject Outputs, Artifact Artifact) Preview(string content = "a preview")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return (new JsonObject { ["action"] = "deploy", ["playbook"] = "deploy.yml", ["tasks"] = new JsonArray() },
            new Artifact { Name = AssignmentBuilder.PreviewArtifact, Content = ByteString.CopyFrom(bytes), Sha256 = ArtifactStore.Sha256(bytes) });
    }

    /// <summary>A guest provisioned and set up, ready for its services, with an agent and a client.</summary>
    private static async Task<(FakeAgent Agent, HttpClient Http)> ReadyAsync(ServerHarness server)
    {
        await server.SetSecretAsync("web/password", Password);
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        await SetUpAsync(server, agent);
        return (agent, http);
    }

    private static async Task SetUpAsync(ServerHarness server, FakeAgent agent)
    {
        await ScheduleConfigurationsAsync(server);
        await ConfigureRunTests.PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
    }

    /// <summary>Plans the first stage and previews the second.</summary>
    private static async Task<(Assignment Plan, Assignment Preview)> PlanStagesAsync(FakeAgent agent, JsonObject? plan = null)
    {
        var planned = await agent.NextAsync("preview");
        Assert.Equal("opentofu", planned.Executor);
        await agent.ReportAsync(planned, Outcome.Succeeded, plan ?? FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        var previewed = await agent.NextAsync("preview");
        Assert.Equal("ansible", previewed.Executor);
        var (outputs, artifact) = Preview();
        await agent.ReportAsync(previewed, Outcome.Succeeded, outputs, artifacts: artifact);
        return (planned, previewed);
    }

    private static async Task ApplyStagesAsync(ServerHarness server, FakeAgent agent)
    {
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject(), artifacts: State(1));
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
    }

    private static Task<GuestConfiguration> StateOfAsync(ServerHarness server, Guid resource) =>
        server.QueryAsync(db => db.GuestConfigurations.AsNoTracking().SingleAsync(c => c.ResourceId == resource));

    private static async Task<JsonObject> ServiceViewAsync(HttpClient http, Guid id) => (await http.GetFromJsonAsync<JsonObject>($"/api/services/{id}"))!;

    private static string Status(JsonObject view) => view["service"]!["status"]!.GetValue<string>();

    private static Task<int> RunCountAsync(ServerHarness server, Guid resource) => server.QueryAsync(db => db.Runs.CountAsync(r => r.ResourceId == resource));

    private static async Task<Step> ApprovalOfAsync(ServerHarness server, string address = Service) =>
        (await server.LatestRunAsync(address, Operations.Pipeline)).Steps.Single(s => s.Name == StepNames.Approval);

    [Fact]
    public async Task Every_stage_is_planned_approved_once_then_applied_in_order()
    {
        var test = Repository.Repository();
        test.Write(PipelineFile, Repository.Pipeline.Replace("stages:", "policy: { approval: manual }\nstages:"));
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        var run = await server.LatestRunAsync(Service, Operations.Pipeline);
        Assert.Equal("revision", run.Reason);
        Assert.Equal(
            new (string?, string)[] { ("expose", "plan"), ("deploy", "preview"), (null, "approval"), ("expose", "apply"), ("deploy", "playbook") },
            run.Steps.OrderBy(s => s.Ordinal).Select(s => (s.Stage, s.Name)));

        var (plan, preview) = await PlanStagesAsync(agent);
        // Before the approval: nothing that changes anything, and nothing secret.
        Assert.Empty(plan.Secrets);
        Assert.Empty(preview.Secrets);
        Assert.DoesNotContain(Password, preview.InputJson);
        var tofu = JsonNode.Parse(plan.InputJson)!;
        Assert.Equal("octopushy", tofu["backend"]!["type"]!.GetValue<string>());
        Assert.Equal(8080, tofu["variables"]!["port"]!.GetValue<int>());
        Assert.Empty(tofu["secretEnv"]!.AsObject());
        var ansible = JsonNode.Parse(preview.InputJson)!;
        Assert.Equal(Service, ansible["context"]!["address"]!.GetValue<string>());
        Assert.Equal("labs/home/proxmox/korriban/guests/krb-app-01/services/web", ansible["guestDirectory"]!.GetValue<string>());
        Assert.Equal(9000, ansible["vars"]!["port"]!.GetValue<int>());

        await server.TickAsync();
        Assert.Empty(agent.TakeAssignments());
        run = await server.LatestRunAsync(Service, Operations.Pipeline);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.DoesNotContain(run.Steps.SelectMany(s => s.Attempts), a => a.Mutating);

        // The approval names one document, covering each stage's saved plan and preview.
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        Assert.Equal(PipelineDocument.Digest(approval.Document!), approval.PlanSha256);
        var document = JsonNode.Parse(approval.Document!)!;
        Assert.Equal(run.InputsHash, document["inputsHash"]!.GetValue<string>());
        Assert.Equal(FakeAgent.PlanArtifact().Sha256, document["stages"]![0]!["artifact"]!.GetValue<string>());
        Assert.Equal(Preview().Artifact.Sha256, document["stages"]![1]!["artifact"]!.GetValue<string>());

        var wrong = await http.PostAsJsonAsync($"/api/runs/{run.Id}/steps/{approval.Id}/approve", new { planSha256 = FakeAgent.PlanArtifact().Sha256 });
        Assert.NotEqual(HttpStatusCode.NoContent, wrong.StatusCode);
        (await http.PostAsJsonAsync($"/api/runs/{run.Id}/steps/{approval.Id}/approve", new { planSha256 = approval.PlanSha256 })).EnsureSuccessStatusCode();

        var apply = await agent.NextAsync("apply");
        Assert.Equal("opentofu", apply.Executor);
        Assert.Empty(apply.Secrets);
        Assert.Contains(apply.Artifacts, a => a.Name == AssignmentBuilder.PlanArtifact);
        await agent.ReportAsync(apply, Outcome.Succeeded, new JsonObject(), artifacts: State(1));
        var playbook = await agent.NextAsync("run");
        Assert.Equal(Password, playbook.Secrets[SecretNames.AnsibleVar("web_password")]);
        Assert.Contains(SecretNames.AnsibleSshKey, playbook.Secrets.Keys);
        Assert.Contains(playbook.Artifacts, a => a.Name == AssignmentBuilder.PreviewArtifact);
        await agent.ReportAsync(playbook, Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();

        run = await server.LatestRunAsync(Service, Operations.Pipeline);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        var resource = await server.ResourceAsync(Service);
        Assert.Equal(run.InputsHash, (await StateOfAsync(server, resource.Id)).AppliedHash);
        Assert.Equal("in-sync", Status(await ServiceViewAsync(http, resource.Id)));

        var view = (await http.GetFromJsonAsync<JsonObject>($"/api/runs/{run.Id}"))!;
        Assert.DoesNotContain(Password, view.ToJsonString());
        Assert.Equal("expose", view["steps"]![0]!["stage"]!.GetValue<string>());
    }

    [Fact]
    public async Task With_no_policy_anywhere_a_service_deploys_by_itself_once_per_input_hash()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PlanStagesAsync(agent);
        await ApplyStagesAsync(server, agent);
        var run = await server.LatestRunAsync(Service, Operations.Pipeline);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        Assert.Equal("policy", approval.DecidedBy);
        Assert.False(approval.ApprovalRequired);
        Assert.NotNull(approval.PlanSha256);

        var resource = await server.ResourceAsync(Service);
        await ScheduleAsync(server);
        await ScheduleAsync(server);
        Assert.Equal(1, await RunCountAsync(server, resource.Id));
    }

    [Fact]
    public async Task A_failure_is_not_repeated_by_itself()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Failed, error: "the plan failed");
        await server.TickAsync();
        Assert.Equal(RunStatuses.Failed, (await server.LatestRunAsync(Service, Operations.Pipeline)).Status);
        await ScheduleAsync(server);
        Assert.Equal(1, await RunCountAsync(server, (await server.ResourceAsync(Service)).Id));
    }

    [Fact]
    public async Task A_service_policy_overrides_what_its_guest_resolves_to()
    {
        var test = Repository.Repository();
        test.Write(PipelineFile, Repository.Pipeline.Replace("stages:", "policy: { approval: manual }\nstages:"));
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PlanStagesAsync(agent);
        await server.TickAsync();
        var run = await server.LatestRunAsync(Service, Operations.Pipeline);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Contains(Service, run.Detail);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public async Task A_plan_that_deletes_or_replaces_waits_under_destructive_manual(int delete, int replace)
    {
        var test = Repository.Repository();
        test.Write(PipelineFile, Repository.Pipeline.Replace("stages:", "policy: { destructive: manual }\nstages:"));
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PlanStagesAsync(agent, FakeAgent.Plan(delete: delete, replace: replace));
        await server.TickAsync();
        var run = await server.LatestRunAsync(Service, Operations.Pipeline);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Contains("deletes or replaces 1 resource(s)", run.Detail);
    }

    [Fact]
    public async Task A_plan_that_only_adds_goes_ahead_under_destructive_manual()
    {
        var test = Repository.Repository();
        test.Write(PipelineFile, Repository.Pipeline.Replace("stages:", "policy: { destructive: manual }\nstages:"));
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PlanStagesAsync(agent);
        await ApplyStagesAsync(server, agent);
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Service, Operations.Pipeline)).Status);
    }

    [Fact]
    public async Task A_recipe_that_declares_its_action_destructive_waits_under_destructive_manual()
    {
        var test = Repository.Repository();
        test.Write(PipelineFile, Repository.Pipeline.Replace("stages:", "policy: { destructive: manual }\nstages:"));
        test.Write("recipes/web/recipe.yaml", File.ReadAllText(Path.Combine(test.Root, "recipes/web/recipe.yaml")).Replace("destructive: false", "destructive: true"));
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PlanStagesAsync(agent);
        await server.TickAsync();
        var run = await server.LatestRunAsync(Service, Operations.Pipeline);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Contains("declares deploy destructive", run.Detail);
    }

    [Fact]
    public async Task A_stage_with_nothing_to_change_skips_its_apply_and_the_rest_go_on()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        var planned = await agent.NextAsync("preview");
        await agent.ReportAsync(planned, Outcome.Succeeded, FakeAgent.Plan());
        var previewed = await agent.NextAsync("preview");
        var (outputs, artifact) = Preview();
        await agent.ReportAsync(previewed, Outcome.Succeeded, outputs, artifacts: artifact);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        var run = await server.LatestRunAsync(Service, Operations.Pipeline);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal(StepStatuses.Skipped, run.Steps.Single(s => s.Name == StepNames.Apply).Status);
    }

    [Fact]
    public async Task A_document_changed_after_approval_is_refused_before_anything_is_sent()
    {
        var test = Repository.Repository();
        test.Write(PipelineFile, Repository.Pipeline.Replace("stages:", "policy: { approval: manual }\nstages:"));
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PlanStagesAsync(agent);
        await server.TickAsync();
        var approval = await ApprovalOfAsync(server);
        (await http.PostAsJsonAsync($"/api/runs/{approval.RunId}/steps/{approval.Id}/approve", new { planSha256 = approval.PlanSha256 })).EnsureSuccessStatusCode();
        await server.WithDbAsync(async db =>
        {
            var step = await db.Steps.SingleAsync(s => s.Id == approval.Id);
            step.Document = step.Document!.Replace("web-rules", "other");
            await db.SaveChangesAsync();
        });
        await server.TickAsync();
        await server.TickAsync();
        Assert.Empty(agent.TakeAssignments());
        var run = await server.LatestRunAsync(Service, Operations.Pipeline);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("not the one that was approved", run.Detail);
    }

    [Fact]
    public async Task A_service_waits_for_its_guests_provisioning_and_says_so()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        await server.SetSecretAsync("web/password", Password);
        var http = server.CreateClient();
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await http.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        await server.SyncAndTrustAsync(http);

        var resource = await server.ResourceAsync(Service);
        await ScheduleAsync(server);
        Assert.Equal(0, await RunCountAsync(server, resource.Id));
        var view = await ServiceViewAsync(http, resource.Id);
        Assert.Equal("waiting", Status(view));
        Assert.Contains("provisioning", view["service"]!["statusDetail"]!.GetValue<string>());
        var refused = await http.PostAsync($"/api/services/{resource.Id}/deploy", null);
        Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("provisioning", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_service_waits_for_its_guests_setup_and_goes_once_it_is_applied()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        await server.SetSecretAsync("web/password", Password);
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        var resource = await server.ResourceAsync(Service);
        await ScheduleAsync(server);
        Assert.Equal(0, await RunCountAsync(server, resource.Id));
        var view = await ServiceViewAsync(http, resource.Id);
        Assert.Equal("waiting", Status(view));
        Assert.Contains("setup", view["service"]!["statusDetail"]!.GetValue<string>());

        await SetUpAsync(server, agent);
        await ScheduleAsync(server);
        Assert.Equal(1, await RunCountAsync(server, resource.Id));
    }

    [Fact]
    public async Task An_approved_run_stops_for_setup_that_changed_while_it_waited_and_then_goes_on()
    {
        var test = Repository.Repository();
        test.Write(PipelineFile, Repository.Pipeline.Replace("stages:", "policy: { approval: manual }\nstages:"));
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PlanStagesAsync(agent);
        await server.TickAsync();
        var run = (await server.LatestRunAsync(Service, Operations.Pipeline));
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);

        // The guest's setup changes while the service waits for a person.
        var guestFile = Path.Combine(test.Root, PipelineDefinitionTests.GuestFile);
        test.Write(PipelineDefinitionTests.GuestFile, File.ReadAllText(guestFile).Replace("greeting: hello", "greeting: hi"));
        server.Commit("Change the guest's setup");
        await server.SyncAsync();

        var approval = await ApprovalOfAsync(server);
        (await http.PostAsJsonAsync($"/api/runs/{run.Id}/steps/{approval.Id}/approve", new { planSha256 = approval.PlanSha256 })).EnsureSuccessStatusCode();
        await server.TickAsync();
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "apply");
        run = await server.LatestRunAsync(Service, Operations.Pipeline);
        Assert.Equal(RunStatuses.Running, run.Status);
        Assert.Contains("setup", run.Detail);
        Assert.Contains("setup", (await ServiceViewAsync(http, run.ResourceId))["service"]!["statusDetail"]!.GetValue<string>());

        await SetUpAsync(server, agent);
        await ApplyStagesAsync(server, agent);
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Service, Operations.Pipeline)).Status);
    }

    [Fact]
    public async Task A_queued_change_goes_back_to_its_run_when_the_guest_stops_being_ready_and_goes_out_later()
    {
        var test = Repository.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PlanStagesAsync(agent);
        await server.WithDbAsync(async db =>
        {
            await db.Agents.ExecuteUpdateAsync(u => u.SetProperty(a => a.Disabled, true));
        });
        await server.TickAsync();
        Assert.Equal(StepStatuses.Queued, (await server.LatestRunAsync(Service, Operations.Pipeline)).Steps.Single(s => s.Name == StepNames.Apply).Status);

        // It is queued, and only dispatch can stop it now.
        await server.WithDbAsync(async db =>
        {
            await db.GuestConfigurations.Where(c => db.Resources.Any(r => r.Id == c.ResourceId && r.Address == Guest))
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.AppliedHash, (string?)null).SetProperty(c => c.ProposedHash, (string?)null));
            await db.Agents.ExecuteUpdateAsync(u => u.SetProperty(a => a.Disabled, false));
        });
        await server.TickAsync();
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "apply");
        var run = await server.LatestRunAsync(Service, Operations.Pipeline);
        var apply = run.Steps.Single(s => s.Name == StepNames.Apply);
        Assert.Equal(RunStatuses.Running, run.Status);
        Assert.Equal(StepStatuses.Pending, apply.Status);
        Assert.Contains("setup", run.Detail);
        Assert.Equal(AttemptStatuses.Cancelled, apply.Attempts.Single().Status);
        Assert.DoesNotContain(await server.QueryAsync(db => db.Attempts.Where(a => a.Action == "apply").Select(a => a.Status).ToListAsync()), s => s == AttemptStatuses.Queued);

        await SetUpAsync(server, agent);
        await ApplyStagesAsync(server, agent);
        run = await server.LatestRunAsync(Service, Operations.Pipeline);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal(2, run.Steps.Single(s => s.Name == StepNames.Apply).Attempts.Count);
    }

    [Fact]
    public async Task Changes_held_for_their_guests_do_not_starve_the_setup_they_wait_for()
    {
        const int Count = 200;
        const string SingleStage = "schemaVersion: 1\nstages:\n  - { name: expose, recipe: web-rules }\ninputs: { port: 8080 }\n";
        var test = Repository.Repository(SingleStage);
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        var guestText = File.ReadAllText(Path.Combine(test.Root, PipelineDefinitionTests.GuestFile));
        for (var i = 1; i < Count; i++)
        {
            var directory = $"labs/home/proxmox/korriban/guests/many-{i:D3}";
            test.Write(directory + "/guest.yaml", guestText.Replace("vmid: 310", $"vmid: {310 + i}").Replace("10.1.0.31/24", $"10.1.{i / 250}.{31 + i % 200}/24"));
            test.Write(directory + "/services/web/pipeline.yaml", SingleStage);
        }

        server.Commit("Add many guests");
        await server.SyncAsync();
        // The ends of provisioning and setup, and of each read-only plan, are seeded: no guest exists.
        await server.WithDbAsync(async db =>
        {
            foreach (var run in await db.Runs.Include(r => r.Steps).Where(r => r.Operation == Operations.Provision && r.Status != RunStatuses.Succeeded).ToListAsync())
            {
                run.Status = RunStatuses.Succeeded;
                run.Steps.ForEach(s => s.Status = StepStatuses.Succeeded);
            }

            foreach (var guest in await db.Resources.Where(r => r.Kind == ResourceKinds.Guest).ToListAsync())
            {
                guest.BoundVmid = JsonNode.Parse(guest.Definition!)!["vmid"]!.GetValue<int>();
                guest.BoundInstallation = guest.ParentAddress;
                guest.SyncedInputsHash = guest.DesiredInputsHash;
                var state = await db.GuestConfigurations.SingleAsync(c => c.ResourceId == guest.Id);
                state.AppliedHash = state.ProposedHash = state.DesiredHash;
            }

            await db.Agents.ExecuteUpdateAsync(u => u.SetProperty(a => a.Disabled, true));
            await db.SaveChangesAsync();
        });
        await ScheduleAsync(server);
        var artifact = FakeAgent.PlanArtifact();
        await server.WithDbAsync(async db =>
        {
            var runs = await db.Runs.Include(r => r.Steps).Where(r => r.Operation == Operations.Pipeline).ToListAsync();
            Assert.Equal(Count, runs.Count);
            foreach (var run in runs)
            {
                var plan = run.Steps.Single(s => s.Name == StepNames.Plan);
                plan.Status = StepStatuses.Succeeded;
                var outputs = FakeAgent.Plan(create: 1);
                outputs["planSha256"] = artifact.Sha256;
                plan.Outputs = outputs.ToJsonString();
                var attempt = new Attempt
                {
                    Id = Guid.CreateVersion7(), RunId = run.Id, StepId = plan.Id, Number = 1, Executor = "opentofu", Action = "preview",
                    Status = AttemptStatuses.Succeeded, StateVersion = 0, AgentId = agent.Id, CreatedAt = DateTimeOffset.UtcNow,
                };
                db.Attempts.Add(attempt);
                db.Artifacts.Add(new ArtifactRecord
                {
                    Id = Guid.CreateVersion7(), AttemptId = attempt.Id, Name = artifact.Name, Sha256 = artifact.Sha256,
                    Size = artifact.Content.Length, CreatedAt = DateTimeOffset.UtcNow,
                });
            }

            await db.SaveChangesAsync();
        });
        await server.TickAsync();
        Assert.Equal(Count, await server.QueryAsync(db => db.Attempts.CountAsync(a => a.Action == "apply" && a.Status == AttemptStatuses.Queued)));

        const string SetupFile = "modules/ansible/app/site.yml";
        test.Write(SetupFile, File.ReadAllText(Path.Combine(test.Root, SetupFile)) + "\n# every guest's setup changed\n");
        server.Commit("Change every guest's setup");
        await server.SyncAsync();
        await ScheduleConfigurationsAsync(server);
        await server.WithDbAsync(db => db.Agents.ExecuteUpdateAsync(u => u.SetProperty(a => a.Disabled, false)));
        for (var pass = 0; pass < 3; pass++)
        {
            await server.TickAsync();
        }

        Assert.Equal(Count, await server.QueryAsync(db => db.Attempts.CountAsync(a => a.Executor == "ansible" && a.Action == "preview"
            && (a.Status == AttemptStatuses.Queued || a.Status == AttemptStatuses.Assigned))));
        Assert.Equal(0, await server.QueryAsync(db => db.Attempts.CountAsync(a => a.Action == "apply" && a.Status == AttemptStatuses.Queued)));
        Assert.NotEmpty(agent.TakeAssignments());
    }

    [Fact]
    public async Task Another_source_with_the_same_inputs_gives_an_unstarted_run_a_replacement()
    {
        var test = Repository.Repository();
        test.Write(PipelineFile, Repository.Pipeline.Replace("stages:", "policy: { approval: manual }\nstages:"));
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PlanStagesAsync(agent);
        await server.TickAsync();
        var stale = await server.LatestRunAsync(Service, Operations.Pipeline);
        Assert.Equal(RunStatuses.AwaitingApproval, stale.Status);

        await InstallationTrustTests.UpdateSourceAsync(http, test.Root + "/.");
        await server.SyncAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded);
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);

        await ScheduleAsync(server);
        await server.TickAsync();
        var current = await server.LatestRunAsync(Service, Operations.Pipeline);
        Assert.NotEqual(stale.Id, current.Id);
        Assert.Equal(RunStatuses.Cancelled, (await server.QueryAsync(db => db.Runs.AsNoTracking().SingleAsync(r => r.Id == stale.Id))).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_guest_that_is_gone_ends_its_services_runs(bool firstStageApplied)
    {
        var test = Repository.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PlanStagesAsync(agent);
        if (firstStageApplied)
            await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject(), artifacts: State(1));

        // The change that is next is queued, and no agent takes it.
        await server.WithDbAsync(db => db.Agents.ExecuteUpdateAsync(u => u.SetProperty(a => a.Disabled, true)));
        await server.TickAsync();
        test.Delete("labs/home/proxmox/korriban/guests/krb-app-01");
        server.Commit("Delete the guest");
        await server.SyncAsync();
        await server.WithDbAsync(db => db.Agents.ExecuteUpdateAsync(u => u.SetProperty(a => a.Disabled, false)));

        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(delete: 1), artifacts: FakeAgent.PlanArtifact("destroy"));
        await server.TickAsync();
        var removal = await server.LatestRunAsync(Guest, Operations.Retire);
        var gate = removal.Steps.Single(s => s.Name == StepNames.Approval);
        if (gate.Status == StepStatuses.AwaitingApproval)
            (await http.PostAsJsonAsync($"/api/runs/{removal.Id}/steps/{gate.Id}/approve", new { planSha256 = gate.PlanSha256 })).EnsureSuccessStatusCode();
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded, new JsonObject { ["previous"] = false });
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject());
        await agent.ReportAsync(await agent.NextAsync("verify-absent"), Outcome.Succeeded);
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Retire)).Status);
        Assert.NotNull((await server.ResourceAsync(Service)).RetiredAt);
        Assert.Equal(RunStatuses.Cancelled, (await server.LatestRunAsync(Service, Operations.Pipeline)).Status);
    }

    [Fact]
    public async Task One_change_at_a_time_on_a_guest_and_a_service_nobody_approved_holds_nobody_back()
    {
        var test = Repository.Repository();
        test.Write("labs/home/proxmox/korriban/guests/krb-app-01/services/other/pipeline.yaml", """
            schemaVersion: 1
            stages:
              - { name: expose, recipe: web-rules }
            inputs: { port: 1 }
            """);
        test.Write("labs/home/proxmox/korriban/guests/krb-app-01/services/parked/pipeline.yaml", """
            schemaVersion: 1
            policy: { approval: manual }
            stages:
              - { name: expose, recipe: web-rules }
            inputs: { port: 2 }
            """);
        test.Delete(PipelineFile);
        test.Write(PipelineFile, """
            schemaVersion: 1
            stages:
              - { name: expose, recipe: web-rules }
            inputs: { port: 3 }
            """);
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        var plans = new List<Assignment>();
        for (var i = 0; i < 6 && plans.Count < 3; i++)
        {
            await server.TickAsync();
            plans.AddRange(agent.TakeAssignments().Where(a => a.Action == "preview"));
        }

        Assert.Equal(3, plans.Count);
        foreach (var plan in plans)
        {
            await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        }

        await server.TickAsync();
        var applies = agent.TakeAssignments().Where(a => a.Action == "apply").ToList();
        // Two services want to change the guest, the third waits for a person: exactly one goes.
        Assert.Single(applies);
        Assert.Equal(RunStatuses.AwaitingApproval, (await server.LatestRunAsync(Guest + "/parked", Operations.Pipeline)).Status);
        await server.Engine.OnStartedAsync(agent.Id, Guid.Parse(applies[0].AttemptId), CancellationToken.None);
        await server.TickAsync();
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "apply");

        var waiting = await server.QueryAsync(db => db.Runs.AsNoTracking().Where(r => r.Status == RunStatuses.Running && r.Detail != null).ToListAsync());
        Assert.Contains(waiting, r => r.Detail!.Contains("changing the same guest"));

        await agent.ReportAsync(applies[0], Outcome.Succeeded, new JsonObject(), artifacts: State(1));
        Assert.Single((await TakeWhenAsync(server, agent, "apply")));
        Assert.Equal(RunStatuses.AwaitingApproval, (await server.LatestRunAsync(Guest + "/parked", Operations.Pipeline)).Status);
    }

    private static async Task<List<Assignment>> TakeWhenAsync(ServerHarness server, FakeAgent agent, string action)
    {
        for (var i = 0; i < 5; i++)
        {
            await server.TickAsync();
            var found = agent.TakeAssignments().Where(a => a.Action == action).ToList();
            if (found.Count > 0)
                return found;
        }

        return [];
    }

    [Fact]
    public async Task A_guests_own_run_waits_for_a_service_changing_it_and_does_not_wait_for_one_that_only_queued()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PlanStagesAsync(agent);
        var apply = await agent.NextAsync("apply");
        await server.Engine.OnStartedAsync(agent.Id, Guid.Parse(apply.AttemptId), CancellationToken.None);

        var guest = await server.ResourceAsync(Guest);
        var configure = await server.Services.GetRequiredService<Planner>().ConfigureGuestAsync(guest.Id, null, CancellationToken.None);
        await ConfigureRunTests.PreviewAsync(agent);
        await server.TickAsync();
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "run");
        var held = await server.QueryAsync(db => db.Runs.AsNoTracking().SingleAsync(r => r.Id == configure));
        Assert.Contains("changing the same guest", held.Detail);

        await agent.ReportAsync(apply, Outcome.Succeeded, new JsonObject(), artifacts: State(1));
        Assert.Contains(await TakeWhenAsync(server, agent, "run"), a => a.Executor == "ansible");
    }

    [Fact]
    public async Task A_failed_stage_stops_the_pipeline_marks_only_its_service_and_retry_continues_from_it_without_a_new_approval()
    {
        var test = Repository.Repository();
        test.Write("labs/home/proxmox/korriban/guests/krb-app-01/services/other/pipeline.yaml", "schemaVersion: 1\nstages:\n  - { name: expose, recipe: web-rules }\ninputs: { port: 1 }\n");
        test.Write(PipelineFile, Repository.Pipeline.Replace("stages:", "policy: { approval: manual }\nstages:"));
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        var sibling = await server.ResourceAsync(Guest + "/other");
        await DeployAsync(server, sibling.Id);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject(), artifacts: State(1));
        await server.TickAsync();
        await ScheduleAsync(server);
        await PlanStagesAsync(agent);
        await server.TickAsync();
        var approval = await ApprovalOfAsync(server);
        (await http.PostAsJsonAsync($"/api/runs/{approval.RunId}/steps/{approval.Id}/approve", new { planSha256 = approval.PlanSha256 })).EnsureSuccessStatusCode();
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject(), artifacts: State(1));

        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Failed, error: "a task failed");
        await server.TickAsync();
        var run = await server.LatestRunAsync(Service, Operations.Pipeline);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("Stage deploy failed", run.Detail);

        var resource = await server.ResourceAsync(Service);
        var guest = await server.ResourceAsync(Guest);
        Assert.Equal("failed", Status(await ServiceViewAsync(http, resource.Id)));
        Assert.Equal("in-sync", Status(await ServiceViewAsync(http, sibling.Id)));
        var guestView = (await http.GetFromJsonAsync<JsonObject>($"/api/guests/{guest.Id}"))!;
        Assert.Equal("in-sync", guestView["guest"]!["status"]!.GetValue<string>());
        Assert.Equal(new[] { "other", "web" }, guestView["services"]!.AsArray().Select(s => s!["name"]!.GetValue<string>()));

        var decided = (await ApprovalOfAsync(server)).DecidedAt;
        (await http.PostAsync($"/api/runs/{run.Id}/retry-stage", null)).EnsureSuccessStatusCode();
        var again = await agent.NextAsync("run");
        Assert.Equal(Password, again.Secrets[SecretNames.AnsibleVar("web_password")]);
        await agent.ReportAsync(again, Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        run = await server.LatestRunAsync(Service, Operations.Pipeline);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        // The same approval, and the first stage was not repeated.
        Assert.Equal(decided, run.Steps.Single(s => s.Name == StepNames.Approval).DecidedAt);
        Assert.Single(run.Steps.Single(s => s.Name == StepNames.Apply).Attempts);
        Assert.Equal(2, run.Steps.Single(s => s.Name == StepNames.Playbook).Attempts.Count);
        Assert.Equal("in-sync", Status(await ServiceViewAsync(http, resource.Id)));
    }

    [Fact]
    public async Task Retry_is_refused_once_the_inputs_changed_and_a_new_commit_gets_its_own_run()
    {
        var test = Repository.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PlanStagesAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject(), artifacts: State(1));
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Failed, error: "a task failed");
        await server.TickAsync();
        var failed = await server.LatestRunAsync(Service, Operations.Pipeline);
        Assert.Equal(RunStatuses.Failed, failed.Status);

        test.Write(PipelineFile, Repository.Pipeline.Replace("port: 9000", "port: 9001"));
        server.Commit("Change the port");
        await server.SyncAsync();
        var retry = await http.PostAsync($"/api/runs/{failed.Id}/retry-stage", null);
        Assert.NotEqual(HttpStatusCode.NoContent, retry.StatusCode);
        Assert.Contains("inputs changed", await retry.Content.ReadAsStringAsync());

        await ScheduleAsync(server);
        var next = await server.LatestRunAsync(Service, Operations.Pipeline);
        Assert.NotEqual(failed.Id, next.Id);
        Assert.Equal(2, await RunCountAsync(server, next.ResourceId));
    }

    [Fact]
    public async Task A_failed_apply_that_moved_its_state_cannot_reuse_its_plan_and_one_that_did_not_can()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PlanStagesAsync(agent);
        var apply = await agent.NextAsync("apply");
        await agent.ReportAsync(apply, Outcome.Failed, error: "provider error", artifacts: State(0, Guid.Empty.ToString()));
        await server.TickAsync();
        var run = await server.LatestRunAsync(Service, Operations.Pipeline);
        Assert.Equal(RunStatuses.Failed, run.Status);

        var refused = await http.PostAsync($"/api/runs/{run.Id}/retry-stage", null);
        Assert.Contains("changed this stage's state", await refused.Content.ReadAsStringAsync());

        await server.WithDbAsync(async db =>
        {
            await db.TofuStateVersions.Where(v => v.ResourceId == run.ResourceId).ExecuteDeleteAsync();
            await db.TofuStates.Where(s => s.ResourceId == run.ResourceId).ExecuteUpdateAsync(u => u.SetProperty(s => s.HeadVersion, 0));
        });
        (await http.PostAsync($"/api/runs/{run.Id}/retry-stage", null)).EnsureSuccessStatusCode();
        var again = await agent.NextAsync("apply");
        Assert.Contains(again.Artifacts, a => a.Name == AssignmentBuilder.PlanArtifact);
    }

    [Fact]
    public async Task A_change_that_may_have_been_lost_is_not_retried()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PlanStagesAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject(), artifacts: State(1));
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Uncertain, error: "The connection dropped.");
        await server.TickAsync();
        var run = await server.LatestRunAsync(Service, Operations.Pipeline);
        Assert.Equal(RunStatuses.Uncertain, run.Status);
        (await http.PostAsync($"/api/runs/{run.Id}/close", null)).EnsureSuccessStatusCode();
        await server.TickAsync();
        var refused = await http.PostAsync($"/api/runs/{run.Id}/retry-stage", null);
        Assert.Contains("uncertain", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Each_opentofu_stage_has_its_own_state_apart_from_its_guests()
    {
        var test = Repository.Repository("""
            schemaVersion: 1
            stages:
              - { name: one, recipe: web-rules }
              - { name: two, recipe: web-rules }
            inputs: { port: 1 }
            """);
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        var firstPlan = await agent.NextAsync("preview");
        var firstSeed = JsonNode.Parse(firstPlan.Artifacts.Single(a => a.Name == TofuStateStore.ArtifactName).Content.ToStringUtf8())!;
        await agent.ReportAsync(firstPlan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact("one"));
        var secondPlan = await agent.NextAsync("preview");
        var secondSeed = JsonNode.Parse(secondPlan.Artifacts.Single(a => a.Name == TofuStateStore.ArtifactName).Content.ToStringUtf8())!;
        await agent.ReportAsync(secondPlan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact("two"));
        Assert.NotEqual(firstSeed["lineage"]!.GetValue<string>(), secondSeed["lineage"]!.GetValue<string>());
        Assert.NotEqual(JsonNode.Parse(firstPlan.InputJson)!["stack"]!.GetValue<string>(), JsonNode.Parse(secondPlan.InputJson)!["stack"]!.GetValue<string>());

        var firstApply = await agent.NextAsync("apply");
        await agent.ReportAsync(firstApply, Outcome.Succeeded, new JsonObject(), artifacts: State(1, "first"));
        var secondApply = await agent.NextAsync("apply");
        // Stage two starts from its own seed, not from what stage one just wrote.
        Assert.DoesNotContain(secondApply.Artifacts, a => a.Name == TofuStateStore.ArtifactName && a.Content.ToStringUtf8().Contains("first"));
        Assert.Contains(secondApply.Artifacts, a => a.Name == AssignmentBuilder.PlanArtifact && a.Content.ToStringUtf8() == "two");
        await agent.ReportAsync(secondApply, Outcome.Succeeded, new JsonObject(), artifacts: State(1, "second"));
        await server.TickAsync();

        var service = await server.ResourceAsync(Service);
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Service, Operations.Pipeline)).Status);
        var states = await server.QueryAsync(db => db.TofuStateVersions.AsNoTracking().Where(v => v.ResourceId == service.Id).OrderBy(v => v.Stage).ToListAsync());
        Assert.Equal(new[] { ("one", "first"), ("two", "second") }, states.Select(v => (v.Stage, v.Lineage)));

        var state = (await http.GetFromJsonAsync<JsonObject>($"/api/guests/{service.Id}/state?stage=two"))!;
        Assert.Equal(1, state["headVersion"]!.GetValue<int>());
        Assert.Equal(0, (await http.GetFromJsonAsync<JsonObject>($"/api/guests/{service.Id}/state"))!["headVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task Two_stages_can_bind_one_extra_var_to_different_keys()
    {
        var test = Repository.Repository("""
            schemaVersion: 1
            stages:
              - { name: one, recipe: web, inputs: { port: 1 }, secrets: { web_password: { store: web/one } } }
              - { name: two, recipe: web, inputs: { port: 2 }, secrets: { web_password: { store: web/two } } }
            """);
        await using var server = await ServerHarness.StartAsync(postgres, test);
        await server.SetSecretAsync("web/one", "first-secret");
        await server.SetSecretAsync("web/two", "second-secret");
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        foreach (var content in new[] { "one", "two" })
        {
            var (outputs, artifact) = Preview(content);
            await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, outputs, artifacts: artifact);
        }

        var first = await agent.NextAsync("run");
        Assert.Equal("first-secret", first.Secrets[SecretNames.AnsibleVar("web_password")]);
        Assert.Equal("one", first.Artifacts.Single().Content.ToStringUtf8());
        await agent.ReportAsync(first, Outcome.Succeeded, ConfigureRunTests.Recap());
        var second = await agent.NextAsync("run");
        Assert.Equal("second-secret", second.Secrets[SecretNames.AnsibleVar("web_password")]);
        Assert.Equal("two", second.Artifacts.Single().Content.ToStringUtf8());
    }

    [Fact]
    public async Task A_stage_logs_in_with_its_guests_own_key()
    {
        var test = Repository.Repository();
        var guestFile = Path.Combine(test.Root, PipelineDefinitionTests.GuestFile);
        test.Write(PipelineDefinitionTests.GuestFile, File.ReadAllText(guestFile).Replace("  sshKey: { store: access/app/ssh-key }\n", ""));
        await using var server = await ServerHarness.StartAsync(postgres, test);
        await server.SetSecretAsync("web/password", Password);
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        await SetUpAsync(server, agent);
        await ScheduleAsync(server);
        await PlanStagesAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject(), artifacts: State(1));
        var playbook = await agent.NextAsync("run");
        Assert.Contains("OPENSSH PRIVATE KEY", playbook.Secrets[SecretNames.AnsibleSshKey]);
        await agent.ReportAsync(playbook, Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        var guest = await server.ResourceAsync(Guest);
        var key = await server.QueryAsync(db => db.GuestAccessKeys.AsNoTracking().SingleAsync(k => k.ResourceId == guest.Id));
        Assert.Equal((await server.LatestRunAsync(Service, Operations.Pipeline)).Id, key.LastUsedRunId);
    }

    [Fact]
    public async Task A_provisioned_guest_whose_only_ansible_is_a_service_stage_gets_octopushys_key()
    {
        var test = Repository.WithoutSetup();
        await using var server = await ServerHarness.StartAsync(postgres, test);
        await server.SetSecretAsync("web/password", Password);
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        var guest = await server.ResourceAsync(Guest);
        // The provision apply carried the public half to the machine, as it does for a guest with a setup recipe.
        var key = await server.QueryAsync(db => db.GuestAccessKeys.AsNoTracking().SingleAsync(k => k.ResourceId == guest.Id));
        Assert.NotNull(key.ProvisionedAt);

        await ScheduleAsync(server);
        var (_, preview) = await PlanStagesAsync(agent);
        Assert.Empty(preview.Secrets);
        Assert.Null(JsonNode.Parse(preview.InputJson)!["sshKey"]);
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject(), artifacts: State(1));
        var playbook = await agent.NextAsync("run");
        Assert.Contains("OPENSSH PRIVATE KEY", playbook.Secrets[SecretNames.AnsibleSshKey]);
        Assert.NotEqual(ConfigureRunTests.SshKey, playbook.Secrets[SecretNames.AnsibleSshKey]);
        // Only this guest's key, and only to the playbook.
        var released = await server.QueryAsync(db => db.SecretResolutions.AsNoTracking()
            .Where(r => r.Key.StartsWith("system/guest-access/")).Select(r => r.Key).Distinct().ToListAsync());
        Assert.Equal([GuestAccessCredentials.Key(guest.Id)], released);
        await agent.ReportAsync(playbook, Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Service, Operations.Pipeline)).Status);
    }

    [Fact]
    public async Task A_machine_octopushys_provisioning_never_put_its_key_on_is_refused_and_told_to_name_one()
    {
        // Provisioned when nothing ran on it, so Octopushy kept no key for it.
        var test = Repository.WithoutSetup(pipeline: "schemaVersion: 1\nstages:\n  - { name: expose, recipe: web-rules }\n");
        await using var server = await ServerHarness.StartAsync(postgres, test);
        await server.SetSecretAsync("web/password", Password);
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        var guest = await server.ResourceAsync(Guest);
        Assert.Equal(0, await server.QueryAsync(db => db.GuestAccessKeys.CountAsync(k => k.ResourceId == guest.Id)));
        await ScheduleAsync(server);
        var planned = await agent.NextAsync("preview");
        await agent.ReportAsync(planned, Outcome.Succeeded, FakeAgent.Plan(), artifacts: FakeAgent.PlanArtifact());
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Service, Operations.Pipeline)).Status);

        // A stage that runs on the guest arrives. The provisioning that should carry Octopushy's key plans no change,
        // as for a machine whose recipe does not put the key on it, so it catches up with no apply.
        test.Write(PipelineFile, Repository.Pipeline);
        server.Commit("Run something on the guest");
        await server.SyncAsync();
        await server.TickAsync();
        var provision = await agent.NextAsync("preview");
        Assert.Equal("opentofu", provision.Executor);
        await agent.ReportAsync(provision, Outcome.Succeeded, FakeAgent.Plan(), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded);
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Provision)).Status);
        guest = await server.ResourceAsync(Guest);
        Assert.Equal(guest.DesiredInputsHash, guest.SyncedInputsHash);
        Assert.Null(await server.QueryAsync(db => db.GuestAccessKeys.AsNoTracking().Where(k => k.ResourceId == guest.Id).Select(k => k.ProvisionedAt).SingleAsync()));

        var service = await server.ResourceAsync(Service);
        var runs = await RunCountAsync(server, service.Id);
        var refused = await Assert.ThrowsAsync<RunException>(() => DeployAsync(server, service.Id));
        Assert.Equal(GuestWork.OwnKeyNotDelivered, refused.Message);
        Assert.Contains("access.sshKey", refused.Message);
        await ScheduleAsync(server);
        Assert.Equal(runs, await RunCountAsync(server, service.Id));
        var view = await ServiceViewAsync(http, service.Id);
        Assert.Equal(Octopushy.Server.Api.GuestStatuses.Waiting, Status(view));
        Assert.Equal(GuestWork.OwnKeyNotDelivered, view["service"]!["statusDetail"]!.GetValue<string>());
        Assert.Equal(0, await server.QueryAsync(db => db.SecretResolutions.CountAsync(r => r.Key.StartsWith("system/guest-access/"))));
    }

    private const string TofuOnly = "schemaVersion: 1\nstages:\n  - { name: expose, recipe: web-rules }\n";

    /// <summary>A pipeline whose stage runs on the guest, invalid because the recipe needs a secret it does not give.</summary>
    private const string InvalidAnsible = "schemaVersion: 1\nstages:\n  - { name: deploy, recipe: web, inputs: { port: 1 } }\n";

    private static Task<int> ProvisionRunsAsync(ServerHarness server, Guid guest) =>
        server.QueryAsync(db => db.Runs.CountAsync(r => r.ResourceId == guest && r.Operation == Operations.Provision));

    [Fact]
    public async Task An_invalid_pipeline_does_not_give_its_guest_octopushys_key_or_provision_it_again()
    {
        var test = Repository.WithoutSetup(pipeline: TofuOnly);
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        var guest = await server.ResourceAsync(Guest);
        var runs = await ProvisionRunsAsync(server, guest.Id);

        test.Write(PipelineFile, InvalidAnsible);
        server.Commit("An invalid stage that would run on the guest");
        await server.SyncAsync();
        await server.TickAsync();

        Assert.False((await server.ResourceAsync(Service)).Valid);
        var after = await server.ResourceAsync(Guest);
        Assert.True(after.Valid);
        Assert.Equal(guest.DesiredInputsHash, after.DesiredInputsHash);
        Assert.Equal(runs, await ProvisionRunsAsync(server, guest.Id));
        Assert.Equal(0, await server.QueryAsync(db => db.GuestAccessKeys.CountAsync(k => k.ResourceId == guest.Id)));
    }

    [Fact]
    public async Task A_pipeline_turning_invalid_does_not_take_its_guests_key_away()
    {
        var test = Repository.WithoutSetup();
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        var guest = await server.ResourceAsync(Guest);
        Assert.NotNull(await server.QueryAsync(db => db.GuestAccessKeys.AsNoTracking().Where(k => k.ResourceId == guest.Id).Select(k => k.ProvisionedAt).SingleAsync()));
        var runs = await ProvisionRunsAsync(server, guest.Id);

        test.Write(PipelineFile, InvalidAnsible);
        server.Commit("Break the pipeline");
        await server.SyncAsync();
        await server.TickAsync();

        Assert.False((await server.ResourceAsync(Service)).Valid);
        Assert.Equal(guest.DesiredInputsHash, (await server.ResourceAsync(Guest)).DesiredInputsHash);
        Assert.Equal(runs, await ProvisionRunsAsync(server, guest.Id));
    }

    /// <summary>
    /// A guest provisioned when nothing ran on it, then given a setup recipe and no key of its own, whose provisioning
    /// catches up with a plan that changes nothing: no apply has carried Octopushy's key to the machine.
    /// </summary>
    private static async Task<(TestRepository Test, FakeAgent Agent, HttpClient Http)> SetupNeverGivenTheKeyAsync(ServerHarness server, TestRepository test)
    {
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var withSetup = Repository.Repository();
        test.Write(PipelineDefinitionTests.GuestFile, File.ReadAllText(Path.Combine(withSetup.Root, PipelineDefinitionTests.GuestFile))
            .Replace("  sshKey: { store: access/app/ssh-key }\n", ""));
        server.Commit("A setup recipe, and no key of its own");
        await server.SyncAsync();
        await server.TickAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded);
        await server.TickAsync();
        var guest = await server.ResourceAsync(Guest);
        Assert.Equal(guest.DesiredInputsHash, guest.SyncedInputsHash);
        Assert.False(await server.QueryAsync(db => GuestAccessCredentials.DeliveredAsync(db, guest.Id, CancellationToken.None)));
        return (test, agent, http);
    }

    [Fact]
    public async Task Setup_is_refused_too_where_no_apply_carried_octopushys_key_and_told_to_name_one()
    {
        var test = Repository.WithoutSetup(pipeline: TofuOnly);
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (_, agent, http) = await SetupNeverGivenTheKeyAsync(server, test);
        using var _ = http;
        var guest = await server.ResourceAsync(Guest);
        var planner = server.Services.GetRequiredService<Planner>();

        // Not automatically, not by hand, and not on a schedule.
        await ScheduleConfigurationsAsync(server);
        await server.TickAsync();
        Assert.Equal(0, await server.QueryAsync(db => db.Runs.CountAsync(r => r.ResourceId == guest.Id && r.Operation == Operations.Configure)));
        var refused = await Assert.ThrowsAsync<RunException>(() => planner.ConfigureGuestAsync(guest.Id, null, CancellationToken.None));
        Assert.Equal(GuestWork.OwnKeyNotDelivered, refused.Message);
        Assert.Contains("access.sshKey", refused.Message);
        await Assert.ThrowsAsync<RunException>(() => planner.ConfigureGuestAsync(guest.Id, "configure", CancellationToken.None, RunReasons.Schedule, DateTimeOffset.UtcNow));
        var page = (await http.GetFromJsonAsync<JsonObject>($"/api/guests/{guest.Id}"))!;
        Assert.Equal(GuestWork.OwnKeyNotDelivered, page["configuration"]!["refused"]!.GetValue<string>());

        // Nor when a value its setup follows changes.
        await FollowedSecretTests.PromoteAsync(http, "app/git-key", await FollowedSecretTests.StageAsync(http, "app/git-key", "rotated"), 1);
        await ScheduleConfigurationsAsync(server);
        Assert.Equal(0, await server.QueryAsync(db => db.Runs.CountAsync(r => r.ResourceId == guest.Id && r.Operation == Operations.Configure)));
        Assert.Empty(agent.TakeAssignments());
        Assert.Equal(0, await server.QueryAsync(db => db.SecretResolutions.CountAsync(r => r.Key.StartsWith("system/guest-access/"))));

        // Naming a key is the way on, and that key is what the playbook gets.
        test.Write(PipelineDefinitionTests.GuestFile, File.ReadAllText(Path.Combine(test.Root, PipelineDefinitionTests.GuestFile))
            .Replace("  hostKey: { store: access/app/host-key }\n", "  sshKey: { store: access/app/ssh-key }\n  hostKey: { store: access/app/host-key }\n"));
        server.Commit("Name the key");
        await server.SyncAsync();
        await server.TickAsync();
        // The guest no longer wants Octopushy's key, which its provisioning hash says, so it is planned once more.
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded);
        await server.TickAsync();
        await ScheduleConfigurationsAsync(server);
        await ConfigureRunTests.PreviewAsync(agent);
        Assert.Equal(ConfigureRunTests.SshKey, (await agent.NextAsync("run")).Secrets[SecretNames.AnsibleSshKey]);
    }

    [Fact]
    public async Task A_followed_value_starts_nothing_without_the_delivery_record_and_stays_owed()
    {
        var test = Repository.Repository();
        test.Write(PipelineDefinitionTests.GuestFile, File.ReadAllText(Path.Combine(test.Root, PipelineDefinitionTests.GuestFile))
            .Replace("  sshKey: { store: access/app/ssh-key }\n", ""));
        test.Delete("labs/home/proxmox/korriban/guests/krb-app-01/services");
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        var guest = await server.ResourceAsync(Guest);
        await SetUpAsync(server, agent);
        var configured = await server.QueryAsync(db => db.Runs.CountAsync(r => r.ResourceId == guest.Id && r.Operation == Operations.Configure));
        Task<int> ClearAsync(DateTimeOffset? at) => server.QueryAsync(db => db.GuestAccessKeys.Where(k => k.ResourceId == guest.Id)
            .ExecuteUpdateAsync(k => k.SetProperty(x => x.ProvisionedAt, at)));

        await ClearAsync(null);
        await FollowedSecretTests.PromoteAsync(http, "app/git-key", await FollowedSecretTests.StageAsync(http, "app/git-key", "rotated"), 1);
        await ScheduleConfigurationsAsync(server);
        Assert.Equal(configured, await server.QueryAsync(db => db.Runs.CountAsync(r => r.ResourceId == guest.Id && r.Operation == Operations.Configure)));

        await ClearAsync(DateTimeOffset.UtcNow);
        await ScheduleConfigurationsAsync(server);
        Assert.Equal(RunReasons.Secret, (await server.LatestRunAsync(Guest, Operations.Configure)).Reason);
    }

    [Fact]
    public async Task The_key_is_not_released_for_a_run_started_before_its_delivery_record_went()
    {
        var test = Repository.Repository();
        test.Write(PipelineDefinitionTests.GuestFile, File.ReadAllText(Path.Combine(test.Root, PipelineDefinitionTests.GuestFile))
            .Replace("  sshKey: { store: access/app/ssh-key }\n", ""));
        await using var server = await ServerHarness.StartAsync(postgres, test);
        await server.SetSecretAsync("web/password", Password);
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        var guest = await server.ResourceAsync(Guest);
        await ScheduleConfigurationsAsync(server);
        var previewed = await agent.NextAsync("preview");

        // Whatever took the record away, the check at release is the last word.
        await server.QueryAsync(db => db.GuestAccessKeys.Where(k => k.ResourceId == guest.Id)
            .ExecuteUpdateAsync(k => k.SetProperty(x => x.ProvisionedAt, (DateTimeOffset?)null)));
        var (outputs, artifact) = ConfigureRunTests.Preview();
        await agent.ReportAsync(previewed, Outcome.Succeeded, outputs, artifacts: artifact);
        await server.TickAsync();

        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "run");
        var playbook = (await server.LatestRunAsync(Guest, Operations.Configure)).Steps.Single(s => s.Name == StepNames.Playbook);
        Assert.Equal(StepStatuses.Failed, playbook.Status);
        Assert.Equal(GuestWork.OwnKeyNotDelivered, playbook.Detail);
        Assert.Equal(0, await server.QueryAsync(db => db.SecretResolutions.CountAsync(r => r.Key.StartsWith("system/guest-access/"))));
    }

    [Fact]
    public async Task A_key_the_guest_names_wins_and_octopushy_keeps_none_for_it()
    {
        var test = Repository.WithoutSetup(declaredKey: true);
        await using var server = await ServerHarness.StartAsync(postgres, test);
        await server.SetSecretAsync("web/password", Password);
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        var guest = await server.ResourceAsync(Guest);
        Assert.Equal(0, await server.QueryAsync(db => db.GuestAccessKeys.CountAsync(k => k.ResourceId == guest.Id)));

        await ScheduleAsync(server);
        await PlanStagesAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject(), artifacts: State(1));
        var playbook = await agent.NextAsync("run");
        Assert.Equal(ConfigureRunTests.SshKey, playbook.Secrets[SecretNames.AnsibleSshKey]);
        Assert.Equal(0, await server.QueryAsync(db => db.SecretResolutions.CountAsync(r => r.Key.StartsWith("system/guest-access/"))));
    }

    [Fact]
    public async Task Deleting_a_folder_shows_the_service_as_missing_and_removes_nothing()
    {
        var test = Repository.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PlanStagesAsync(agent);
        await ApplyStagesAsync(server, agent);
        var resource = await server.ResourceAsync(Service);
        var runs = await RunCountAsync(server, resource.Id);

        test.Delete("labs/home/proxmox/korriban/guests/krb-app-01/services/web");
        server.Commit("Delete the service");
        await server.SyncAsync();
        await ScheduleAsync(server);
        await server.TickAsync();
        Assert.Equal(runs, await RunCountAsync(server, resource.Id));
        Assert.Empty(agent.TakeAssignments());
        var view = await ServiceViewAsync(http, resource.Id);
        Assert.Equal("missing", Status(view));
        Assert.Contains("Nothing was removed", view["service"]!["statusDetail"]!.GetValue<string>());
        var guest = await server.ResourceAsync(Guest);
        Assert.Contains((await http.GetFromJsonAsync<JsonObject>($"/api/guests/{guest.Id}"))!["services"]!.AsArray(), s => s!["status"]!.GetValue<string>() == "missing");
        Assert.NotEqual(HttpStatusCode.OK, (await http.PostAsync($"/api/services/{resource.Id}/deploy", null)).StatusCode);

        test.Write(PipelineFile, Repository.Pipeline);
        server.Commit("Restore the service");
        await server.SyncAsync();
        Assert.Null((await server.ResourceAsync(Service)).MissingSince);
    }

    [Fact]
    public async Task A_new_machine_gets_its_setup_again_and_then_its_services()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PlanStagesAsync(agent);
        await ApplyStagesAsync(server, agent);
        var resource = await server.ResourceAsync(Service);
        var guest = await server.ResourceAsync(Guest);

        await server.Services.GetRequiredService<Planner>().PlanGuestAsync(guest.Id, CancellationToken.None);
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(replace: 1), artifacts: FakeAgent.PlanArtifact("replace"));
        await server.TickAsync();
        var provision = await server.LatestRunAsync(Guest, Operations.Provision);
        var gate = provision.Steps.Single(s => s.Name == StepNames.Approval);
        if (gate.Status == StepStatuses.AwaitingApproval)
            (await http.PostAsJsonAsync($"/api/runs/{provision.Id}/steps/{gate.Id}/approve", new { planSha256 = gate.PlanSha256 })).EnsureSuccessStatusCode();
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded);
        await server.TickAsync();

        await ScheduleAsync(server);
        Assert.Equal(1, await RunCountAsync(server, resource.Id));
        Assert.Equal("waiting", Status(await ServiceViewAsync(http, resource.Id)));

        await SetUpAsync(server, agent);
        await ScheduleAsync(server);
        Assert.Equal(2, await RunCountAsync(server, resource.Id));
    }

    [Fact]
    public async Task A_guest_that_is_gone_takes_its_services_with_it()
    {
        var test = Repository.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        var resource = await server.ResourceAsync(Service);

        test.Delete("labs/home/proxmox/korriban/guests/krb-app-01");
        server.Commit("Delete the guest");
        await server.SyncAsync();
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(delete: 1), artifacts: FakeAgent.PlanArtifact("destroy"));
        await server.TickAsync();
        var removal = await server.LatestRunAsync(Guest, Operations.Retire);
        var gate = removal.Steps.Single(s => s.Name == StepNames.Approval);
        if (gate.Status == StepStatuses.AwaitingApproval)
            (await http.PostAsJsonAsync($"/api/runs/{removal.Id}/steps/{gate.Id}/approve", new { planSha256 = gate.PlanSha256 })).EnsureSuccessStatusCode();
        await agent.ReportAsync(await agent.NextAsync("set-protection"), Outcome.Succeeded, new JsonObject { ["previous"] = false });
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject());
        await agent.ReportAsync(await agent.NextAsync("verify-absent"), Outcome.Succeeded);
        await server.TickAsync();
        Assert.NotNull((await server.QueryAsync(db => db.Resources.AsNoTracking().SingleAsync(r => r.Id == resource.Id))).RetiredAt);
    }

    [Fact]
    public async Task A_renamed_guest_keeps_its_services_and_their_state()
    {
        var test = Repository.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (agent, http) = await ReadyAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        await PlanStagesAsync(agent);
        await ApplyStagesAsync(server, agent);
        var resource = await server.ResourceAsync(Service);

        var from = Path.Combine(test.Root, "labs/home/proxmox/korriban/guests/krb-app-01");
        var to = Path.Combine(test.Root, "labs/home/proxmox/korriban/guests/krb-web-01");
        Directory.Move(from, to);
        var guestFile = "labs/home/proxmox/korriban/guests/krb-web-01/guest.yaml";
        test.Write(guestFile, File.ReadAllText(Path.Combine(test.Root, guestFile)) + "renamedFrom: krb-app-01\n");
        server.Commit("Rename the guest");
        await server.SyncAsync();

        var moved = await server.ResourceAsync("home/korriban/krb-web-01/web");
        Assert.Equal(resource.Id, moved.Id);
        Assert.Equal("home/korriban/krb-web-01", moved.ParentAddress);
        Assert.Null(moved.MissingSince);
    }
}
