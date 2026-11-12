using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Definitions;
using Octopushy.Server.Agents;
using Octopushy.Server.Api;
using Octopushy.Server.Changes;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Octopushy.Tests.Definitions;

namespace Octopushy.Tests.Server;

/// <summary>
/// An agent is updated through the guest it runs on: a change that sets the guest's agent version to the server's, approved
/// at once, rolled out by the guest's setup run on another agent, with the agent paused until it is back.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AgentUpdateTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    private const string SecondFile = "labs/home/proxmox/korriban/guests/krb-app-02/guest.yaml";
    private const string ThirdFile = "labs/home/proxmox/korriban/guests/krb-app-03/guest.yaml";
    private static readonly IPAddress GuestAddress = IPAddress.Parse("10.1.0.31");
    private static readonly IPAddress SecondAddress = IPAddress.Parse("10.1.0.32");
    private static readonly string Target = AgentUpdates.TargetVersion;

    /// <summary>
    /// The configure fixture's guest as a worker: its inputs pin an old agent version and a digest, with a comment, under a
    /// manual approval policy. With <paramref name="second"/>, a second worker guest, krb-app-02, like it.
    /// </summary>
    private static TestRepository Lab(string version = "0.0.1", bool second = false, string policy = "policy: { approval: manual }")
    {
        var test = GuestConfigurationTests.Repository(policy);
        var text = File.ReadAllText(Path.Combine(test.Root, GuestFile));
        Assert.Contains("inputs: { greeting: hello }", text);
        text = text.Replace("inputs: { greeting: hello }", $"""
            inputs:
              greeting: hello
              # Pinned by hand.
              worker_agent_version: {version}
              worker_agent_digest: sha256:0123456789abcdef
            """);
        test.Write(GuestFile, text);
        if (second)
            test.Write(SecondFile, text.Replace("vmid: 310", "vmid: 311").Replace("10.1.0.31", "10.1.0.32"));
        return test;
    }

    private sealed class Setup : IAsyncDisposable
    {
        public required ServerHarness Server { get; init; }
        public required HttpClient Client { get; init; }
        public required TestRepository Remote { get; init; }

        public string RemoteFile(string path) => TestWorkflows.Run(Remote.Root, "show", $"main:{path}");

        public AgentUpdates Updates => Server.Services.GetRequiredService<AgentUpdates>();

        public async Task<JsonObject> AgentAsync(Guid id) =>
            (await Client.GetFromJsonAsync<JsonArray>("/api/agents"))!.OfType<JsonObject>().Single(a => a["id"]!.GetValue<Guid>() == id);

        public async Task<string> StateAsync(Guid id) => (await AgentAsync(id))["update"]!["state"]!.GetValue<string>();

        public Task<AgentRecord> RecordAsync(Guid id) => Server.QueryAsync(db => db.Agents.AsNoTracking().SingleAsync(a => a.Id == id));

        /// <summary>The agent says hello again, reporting <paramref name="version"/>.</summary>
        public async Task ReportVersionAsync(Guid id, string version)
        {
            await Server.WithDbAsync(async db =>
            {
                await db.Agents.Where(a => a.Id == id).ExecuteUpdateAsync(u => u.SetProperty(a => a.Version, version));
            });
            await Updates.AdvanceAsync(CancellationToken.None);
        }

        public Task<HttpResponseMessage> RequestAsync(Guid id) => Client.PostAsync($"/api/agents/{id}/update", null);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Server.DisposeAsync();
            Remote.Dispose();
        }
    }

    /// <summary>
    /// The server with publishing on, reading a bare remote cloned from the lab, synced and trusted, with an operator's client
    /// and the store values the guest's setup names.
    /// </summary>
    private async Task<Setup> StartAsync(TestRepository lab, bool publish = true, TimeProvider? clock = null)
    {
        var settings = new Dictionary<string, string>();
        if (publish)
            settings["Octopushy:RepositoryEditor:PublishEnabled"] = "true";
        var server = await ServerHarness.StartAsync(postgres, lab, settings: settings, clock: clock);
        var remote = new TestRepository();
        await server.Services.GetRequiredService<GitCli>().RunAsync(["clone", "--bare", "--quiet", server.Repository.Root, remote.Root]);
        await server.WithDbAsync(async db =>
        {
            (await db.RepositorySettings.SingleAsync()).Url = remote.Root;
            await db.SaveChangesAsync();
        });
        await server.SetSecretAsync("access/app/ssh-key", ConfigureRunTests.SshKey);
        await server.SetPlainAsync("access/app/host-key", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIL4yMxTo2ZC6CYBxvdtnHHtR5HBTjQeL+w9jI3rj+PyY");
        await server.SetSecretAsync("app/tailscale", "tskey-secret-value");
        await server.SetSecretAsync("app/git-key", "git-key-value");
        var client = await InstallationTrustTests.AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        return new Setup { Server = server, Client = client, Remote = remote };
    }

    /// <summary>
    /// Answers whatever <paramref name="agent"/> is given until nothing is left: plans that change nothing, setup that succeeds,
    /// and approvals by hand when <paramref name="approve"/>. Returns the actions it was given.
    /// </summary>
    /// <param name="outputsOnly">Plans of an existing guest change outputs only, which the manual policy still asks a person about.</param>
    private static async Task<List<string>> WorkAsync(Setup setup, FakeAgent agent, bool approve = false, Func<Assignment, bool>? stop = null,
        bool outputsOnly = false)
    {
        var done = new List<string>();
        var planner = setup.Server.Services.GetRequiredService<Planner>();
        var idle = 0;
        for (var i = 0; i < 30 && idle < 2; i++)
        {
            await setup.Server.TickAsync();
            await planner.ScheduleConfigurationsAsync(CancellationToken.None);
            await setup.Server.TickAsync();
            var approved = approve && await AnyWaitingAsync(setup);
            if (approved)
                await ApproveWaitingAsync(setup);
            var assignments = agent.TakeAssignments();
            idle = assignments.Count == 0 && !approved ? idle + 1 : 0;

            foreach (var assignment in assignments)
            {
                if (stop?.Invoke(assignment) == true)
                    return done;
                done.Add($"{assignment.Executor}:{assignment.Action}");
                switch (assignment.Executor, assignment.Action)
                {
                    case ("opentofu", "preview"):
                        var create = await setup.Server.QueryAsync(db => db.Resources.AsNoTracking().AnyAsync(r => r.Address == Guest && r.BoundVmid == null));
                        await agent.ReportAsync(assignment, Outcome.Succeeded, create ? FakeAgent.Plan(create: 1) : FakeAgent.Plan(hasChanges: outputsOnly), artifacts: FakeAgent.PlanArtifact());
                        break;
                    case ("ansible", "preview"):
                        var (outputs, artifact) = ConfigureRunTests.Preview();
                        await agent.ReportAsync(assignment, Outcome.Succeeded, outputs, artifacts: artifact);
                        break;
                    case ("ansible", "run"):
                        await agent.ReportAsync(assignment, Outcome.Succeeded, ConfigureRunTests.Recap());
                        break;
                    default:
                        await agent.ReportAsync(assignment, Outcome.Succeeded);
                        break;
                }
            }
        }

        return done;
    }

    private static Task<bool> AnyWaitingAsync(Setup setup) =>
        setup.Server.QueryAsync(db => db.Steps.AnyAsync(s => s.Status == StepStatuses.AwaitingApproval));

    private static async Task ApproveWaitingAsync(Setup setup)
    {
        var waiting = await setup.Server.QueryAsync(db => db.Steps.AsNoTracking().Where(s => s.Status == StepStatuses.AwaitingApproval).ToListAsync());
        foreach (var step in waiting)
            (await setup.Client.PostAsJsonAsync($"/api/runs/{step.RunId}/steps/{step.Id}/approve", new { planSha256 = step.PlanSha256 })).EnsureSuccessStatusCode();
    }

    /// <summary>The guest provisioned and set up by <paramref name="runner"/>, every approval given by hand.</summary>
    private static async Task ProvisionAsync(Setup setup, FakeAgent runner)
    {
        var done = await WorkAsync(setup, runner, approve: true);
        Assert.Contains("ansible:run", done);
        Assert.Equal(RunStatuses.Succeeded, (await setup.Server.LatestRunAsync(Guest, Operations.Configure)).Status);
    }

    [Fact]
    public void An_agent_is_linked_to_the_guest_that_names_it()
    {
        using var test = Lab(second: true);
        var repository = LabRepositoryLoader.Load(test.Root);
        // By the guest's own name, which the worker workflows use when worker_agent_name is not set.
        Assert.Equal(Guest, AgentUpdates.GuestOf(repository, "krb-app-01", null, out _)!.Address);
        Assert.Null(AgentUpdates.GuestOf(repository, "elsewhere", null, out var ambiguous));
        Assert.False(ambiguous);

        // By worker_agent_name, which then replaces the guest's name.
        var named = File.ReadAllText(Path.Combine(test.Root, SecondFile)).Replace("  greeting: hello\n", "  greeting: hello\n  worker_agent_name: builder\n");
        test.Write(SecondFile, named);
        repository = LabRepositoryLoader.Load(test.Root);
        Assert.Equal("home/korriban/krb-app-02", AgentUpdates.GuestOf(repository, "builder", null, out _)!.Address);
        Assert.Null(AgentUpdates.GuestOf(repository, "krb-app-02", null, out _));

        // Two guests naming one agent: the one at the address it connects from, or none.
        test.Write(SecondFile, named.Replace("worker_agent_name: builder", "worker_agent_name: krb-app-01"));
        repository = LabRepositoryLoader.Load(test.Root);
        Assert.Null(AgentUpdates.GuestOf(repository, "krb-app-01", null, out ambiguous));
        Assert.True(ambiguous);
        Assert.Null(AgentUpdates.GuestOf(repository, "krb-app-01", IPAddress.Parse("10.1.0.99"), out _));
        Assert.Equal("home/korriban/krb-app-02", AgentUpdates.GuestOf(repository, "krb-app-01", SecondAddress, out _)!.Address);

        // A guest whose inputs do not set the agent's version runs no agent of Octopushy's.
        test.Write(GuestFile, File.ReadAllText(Path.Combine(test.Root, GuestFile)).Replace("  worker_agent_version: 0.0.1\n", ""));
        test.Write(SecondFile, named);
        repository = LabRepositoryLoader.Load(test.Root);
        Assert.Null(AgentUpdates.GuestOf(repository, "krb-app-01", GuestAddress, out _));
    }

    [Fact]
    public void Versions_compare_by_number_and_an_unreadable_one_is_older()
    {
        Assert.True(AgentUpdates.AtLeast("0.8.1", "0.8.1"));
        Assert.True(AgentUpdates.AtLeast("v0.10.0", "0.9.3"));
        Assert.True(AgentUpdates.AtLeast("0.9.0-dev", "0.9.0"));
        Assert.False(AgentUpdates.AtLeast("0.8.0", "0.8.1"));
        Assert.False(AgentUpdates.AtLeast("test", "0.8.1"));
        Assert.False(AgentUpdates.AtLeast(null, "0.8.1"));
    }

    [Fact]
    public async Task An_update_commits_the_version_approves_its_setup_and_ends_when_the_agent_is_back()
    {
        await using var setup = await StartAsync(Lab());
        var runner = await setup.Server.ConnectAgentFromAsync(IPAddress.Parse("10.9.9.9"), "runner", "home");
        await ProvisionAsync(setup, runner);
        var worker = await setup.Server.ConnectAgentFromAsync(GuestAddress, "krb-app-01", "home");
        await setup.ReportVersionAsync(worker.Id, "0.0.1");
        var resource = await setup.Server.ResourceAsync(Guest);

        var listed = await setup.AgentAsync(worker.Id);
        Assert.Equal(resource.Id, listed["guest"]!["resourceId"]!.GetValue<Guid>());
        Assert.Equal(Guest, listed["guest"]!["address"]!.GetValue<string>());
        Assert.Equal("0.0.1", listed["guest"]!["declaredVersion"]!.GetValue<string>());
        Assert.Equal("available", listed["update"]!["state"]!.GetValue<string>());
        Assert.Equal(Target, listed["update"]!["targetVersion"]!.GetValue<string>());
        Assert.Null(listed["update"]!["reason"]);
        Assert.Empty(listed["work"]!.AsArray());
        // Asked about nothing that runs on it, the runner shows where it has been.
        var reach = (await setup.AgentAsync(runner.Id))["reachability"]!.AsArray().OfType<JsonObject>().ToList();
        var api = reach.First(e => e["kind"]!.GetValue<string>() == "proxmox-api");
        Assert.Equal("home/korriban", api["name"]!.GetValue<string>());
        Assert.Null(api["resourceId"]);
        var ssh = reach.Single(e => e["kind"]!.GetValue<string>() == "guest-ssh");
        Assert.Equal(Guest, ssh["name"]!.GetValue<string>());
        Assert.Equal(resource.Id, ssh["resourceId"]!.GetValue<Guid>());

        Assert.Equal(HttpStatusCode.NoContent, (await setup.RequestAsync(worker.Id)).StatusCode);

        // The version is set and the digest dropped, the rest of the file as it was.
        var file = setup.RemoteFile(GuestFile);
        Assert.Contains($"  worker_agent_version: {Target}\n", file);
        Assert.DoesNotContain("worker_agent_digest", file);
        Assert.Contains("  # Pinned by hand.\n", file);
        Assert.Contains("  greeting: hello\n", file);
        var record = await setup.RecordAsync(worker.Id);
        Assert.True(record.Draining);
        Assert.True(record.UpdatePaused);
        listed = await setup.AgentAsync(worker.Id);
        Assert.Equal("updating", listed["update"]!["state"]!.GetValue<string>());
        var changeId = listed["update"]!["changeId"]!.GetValue<Guid>();
        Assert.Equal(record.UpdateChangeId, changeId);
        var change = (await setup.Client.GetFromJsonAsync<ChangeDto>($"/api/changes/{changeId}"))!;
        Assert.Equal("ops", change.Author);

        // Asked again while it runs: refused, and so is taking it back.
        var again = await setup.RequestAsync(worker.Id);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("being updated already", await again.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await setup.Client.DeleteAsync($"/api/agents/{worker.Id}/update")).StatusCode);

        // The runner rolls it out, the manual policy notwithstanding; the paused worker takes nothing.
        var seen = false;
        var done = await WorkAsync(setup, runner, stop: a =>
        {
            if (a is not { Executor: "ansible", Action: "run" } || seen)
                return false;
            seen = true;
            return true;
        });
        Assert.Contains("ansible:preview", done);
        Assert.Empty(worker.TakeAssignments());
        // Its provision plan changes nothing, so there was nothing to approve.
        var provision = await setup.Server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal(RunStatuses.Succeeded, provision.Status);
        Assert.Equal(StepStatuses.Skipped, provision.Steps.Single(s => s.Name == StepNames.Approval).Status);
        var configure = await setup.Server.LatestRunAsync(Guest, Operations.Configure);
        var approval = configure.Steps.Single(s => s.Name == StepNames.Approval);
        Assert.Equal("approved", approval.Decision);
        Assert.Equal("ops", approval.DecidedBy);
        Assert.Contains($"agent update (change {changeId})", approval.DecisionNote);

        // The playbook was handed out and is the runner's work now.
        var work = (await setup.AgentAsync(runner.Id))["work"]!.AsArray().OfType<JsonObject>().Single();
        Assert.Equal(configure.Id, work["runId"]!.GetValue<Guid>());
        Assert.Equal(configure.Number, work["runNumber"]!.GetValue<long>());
        Assert.Equal(Operations.Configure, work["operation"]!.GetValue<string>());
        Assert.Equal(Guest, work["address"]!.GetValue<string>());
        Assert.Equal(StepNames.Playbook, work["step"]!.GetValue<string>());
        Assert.Equal("assigned", work["status"]!.GetValue<string>());
        var playbook = configure.Steps.Single(s => s.Name == StepNames.Playbook).Attempts.Single();
        await runner.ReportAsync(playbook.Id.ToString(), Outcome.Succeeded, ConfigureRunTests.Recap());
        await setup.Server.TickAsync();
        await setup.Updates.AdvanceAsync(CancellationToken.None);
        change = (await setup.Client.GetFromJsonAsync<ChangeDto>($"/api/changes/{changeId}"))!;
        Assert.Equal(ChangeStatuses.Applied, change.Status);

        // Applied, and the agent is not back yet: still updating, still paused.
        listed = await setup.AgentAsync(worker.Id);
        Assert.Equal("updating", listed["update"]!["state"]!.GetValue<string>());
        Assert.Equal(change.Runs[^1].Id, listed["update"]!["runId"]!.GetValue<Guid>());
        Assert.True((await setup.RecordAsync(worker.Id)).Draining);

        await setup.ReportVersionAsync(worker.Id, Target);
        listed = await setup.AgentAsync(worker.Id);
        Assert.Equal("current", listed["update"]!["state"]!.GetValue<string>());
        Assert.Null(listed["update"]!["changeId"]);
        record = await setup.RecordAsync(worker.Id);
        Assert.False(record.Draining);
        Assert.False(record.UpdatePaused);
        Assert.Null(record.UpdateState);
        var current = await setup.RequestAsync(worker.Id);
        Assert.Equal(HttpStatusCode.Conflict, current.StatusCode);
        Assert.Contains($"already runs {Target}", await current.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_agent_that_does_not_come_back_in_time_fails_and_a_pause_set_by_hand_stays()
    {
        var clock = new TestClock();
        await using var setup = await StartAsync(Lab(), clock: clock);
        var runner = await setup.Server.ConnectAgentFromAsync(IPAddress.Parse("10.9.9.9"), "runner", "home");
        await ProvisionAsync(setup, runner);
        var worker = await setup.Server.ConnectAgentFromAsync(GuestAddress, "krb-app-01", "home");
        await setup.ReportVersionAsync(worker.Id, "0.0.1");
        (await setup.Client.PatchAsJsonAsync($"/api/agents/{worker.Id}", new { draining = true })).EnsureSuccessStatusCode();
        Assert.Equal("available", await setup.StateAsync(worker.Id));

        Assert.Equal(HttpStatusCode.NoContent, (await setup.RequestAsync(worker.Id)).StatusCode);
        Assert.False((await setup.RecordAsync(worker.Id)).UpdatePaused);
        await WorkAsync(setup, runner, outputsOnly: true);
        // A plan that changes outputs and no resource is approved with the update, as its setup is.
        var provision = await setup.Server.LatestRunAsync(Guest, Operations.Provision);
        Assert.Equal("ops", provision.Steps.Single(s => s.Name == StepNames.Approval).DecidedBy);
        Assert.Equal(RunStatuses.Succeeded, (await setup.Server.LatestRunAsync(Guest, Operations.Configure)).Status);
        await setup.Updates.AdvanceAsync(CancellationToken.None);
        Assert.Equal("updating", await setup.StateAsync(worker.Id));

        clock.Now = DateTimeOffset.UtcNow + AgentUpdates.ComeBackWithin + TimeSpan.FromMinutes(1);
        await setup.Updates.AdvanceAsync(CancellationToken.None);
        var listed = await setup.AgentAsync(worker.Id);
        Assert.Equal("failed", listed["update"]!["state"]!.GetValue<string>());
        Assert.Contains("did not come back", listed["update"]!["reason"]!.GetValue<string>());
        Assert.NotNull(listed["update"]!["changeId"]);
        Assert.True((await setup.RecordAsync(worker.Id)).Draining);

        // The repository declares the target now, so asking again is refused until its setup rolls it out.
        var again = await setup.RequestAsync(worker.Id);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("has not rolled it out yet", await again.Content.ReadAsStringAsync());

        // Back on the target after all: current, and still paused, as the operator left it.
        await setup.ReportVersionAsync(worker.Id, Target);
        Assert.Equal("current", await setup.StateAsync(worker.Id));
        Assert.True((await setup.RecordAsync(worker.Id)).Draining);
    }

    [Fact]
    public async Task Updates_go_one_at_a_time_and_a_failure_empties_the_queue()
    {
        var lab = Lab(second: true, policy: "");
        lab.Write(ThirdFile, File.ReadAllText(Path.Combine(lab.Root, SecondFile)).Replace("vmid: 311", "vmid: 312").Replace("10.1.0.32", "10.1.0.33"));
        await using var setup = await StartAsync(lab);
        await setup.Server.ConnectAgentFromAsync(IPAddress.Parse("10.9.9.9"), "runner", "home");
        var first = await setup.Server.ConnectAgentFromAsync(GuestAddress, "krb-app-01", "home");
        var second = await setup.Server.ConnectAgentFromAsync(SecondAddress, "krb-app-02", "home");
        var third = await setup.Server.ConnectAgentFromAsync(IPAddress.Parse("10.1.0.33"), "krb-app-03", "home");
        await setup.ReportVersionAsync(first.Id, "0.0.1");
        await setup.ReportVersionAsync(second.Id, "0.0.1");
        await setup.ReportVersionAsync(third.Id, "0.0.1");
        Assert.Equal("available", await setup.StateAsync(second.Id));

        Assert.Equal(HttpStatusCode.NoContent, (await setup.RequestAsync(first.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await setup.RequestAsync(second.Id)).StatusCode);
        Assert.Equal("updating", await setup.StateAsync(first.Id));
        Assert.Equal("queued", await setup.StateAsync(second.Id));
        Assert.False((await setup.RecordAsync(second.Id)).Draining);
        Assert.Equal(HttpStatusCode.Conflict, (await setup.RequestAsync(second.Id)).StatusCode);

        // Taken out of the queue, and back in.
        Assert.Equal(HttpStatusCode.NoContent, (await setup.Client.DeleteAsync($"/api/agents/{second.Id}/update")).StatusCode);
        Assert.Equal("available", await setup.StateAsync(second.Id));
        Assert.Equal(HttpStatusCode.NoContent, (await setup.RequestAsync(second.Id)).StatusCode);
        Assert.Equal("queued", await setup.StateAsync(second.Id));

        // The first is back: the second starts.
        await setup.ReportVersionAsync(first.Id, Target);
        Assert.Equal("current", await setup.StateAsync(first.Id));
        Assert.Equal("updating", await setup.StateAsync(second.Id));
        Assert.True((await setup.RecordAsync(second.Id)).Draining);
        Assert.Contains($"worker_agent_version: {Target}", setup.RemoteFile(SecondFile));

        // Updating all queues the third behind it; the second's run is cancelled, so its update fails, and the queue is emptied.
        Assert.Equal(HttpStatusCode.NoContent, (await setup.Client.PostAsync("/api/agents/update", null)).StatusCode);
        Assert.Equal("queued", await setup.StateAsync(third.Id));
        Assert.Equal("current", await setup.StateAsync(first.Id));
        var changeId = (await setup.RecordAsync(second.Id)).UpdateChangeId!.Value;
        var change = (await setup.Client.GetFromJsonAsync<ChangeDto>($"/api/changes/{changeId}"))!;
        var run = Assert.Single(change.Runs);
        (await setup.Client.PostAsync($"/api/runs/{run.Id}/cancel", null)).EnsureSuccessStatusCode();
        await setup.Updates.AdvanceAsync(CancellationToken.None);

        var listed = await setup.AgentAsync(second.Id);
        Assert.Equal("failed", listed["update"]!["state"]!.GetValue<string>());
        Assert.Contains("The change failed", listed["update"]!["reason"]!.GetValue<string>());
        Assert.Equal(run.Id, listed["update"]!["runId"]!.GetValue<Guid>());
        Assert.False((await setup.RecordAsync(second.Id)).Draining);
        Assert.Equal("available", await setup.StateAsync(third.Id));
        Assert.Null((await setup.RecordAsync(third.Id)).UpdateState);
        Assert.Contains("worker_agent_version: 0.0.1", setup.RemoteFile(ThirdFile));
    }

    [Fact]
    public async Task An_agent_that_cannot_be_updated_from_here_says_why()
    {
        await using var setup = await StartAsync(Lab(), publish: false);
        var worker = await setup.Server.ConnectAgentFromAsync(GuestAddress, "krb-app-01", "home");
        await setup.ReportVersionAsync(worker.Id, "0.0.1");
        var elsewhere = await setup.Server.ConnectAgentFromAsync(IPAddress.Parse("10.9.9.9"), "internal", "home");
        await setup.ReportVersionAsync(elsewhere.Id, "0.0.1");

        // No guest names it: the internal agent is updated with the server.
        var listed = await setup.AgentAsync(elsewhere.Id);
        Assert.Null(listed["guest"]);
        Assert.Equal("unavailable", listed["update"]!["state"]!.GetValue<string>());
        Assert.Contains("updated with the server", listed["update"]!["reason"]!.GetValue<string>());
        var refused = await setup.RequestAsync(elsewhere.Id);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("updated with the server", (await refused.Content.ReadFromJsonAsync<JsonObject>())!["title"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await setup.RequestAsync(Guid.NewGuid())).StatusCode);

        // Committing from the UI is off.
        Assert.Contains("PublishEnabled", (await setup.AgentAsync(worker.Id))["update"]!["reason"]!.GetValue<string>());
        setup.Server.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Octopushy.Server.Options.OctopushyOptions>>()
            .Value.RepositoryEditor.PublishEnabled = true;
        Assert.Equal("available", await setup.StateAsync(worker.Id));

        // Nobody else can set its guest up: the other agent is paused, and then gone.
        (await setup.Client.PatchAsJsonAsync($"/api/agents/{elsewhere.Id}", new { draining = true })).EnsureSuccessStatusCode();
        listed = await setup.AgentAsync(worker.Id);
        Assert.Equal("unavailable", listed["update"]!["state"]!.GetValue<string>());
        Assert.Contains("No other connected agent", listed["update"]!["reason"]!.GetValue<string>());
        (await setup.Client.PatchAsJsonAsync($"/api/agents/{elsewhere.Id}", new { draining = false })).EnsureSuccessStatusCode();
        Assert.Equal("available", await setup.StateAsync(worker.Id));

        // Updating all asks only for the one that can be.
        Assert.Equal(HttpStatusCode.NoContent, (await setup.Client.PostAsync("/api/agents/update", null)).StatusCode);
        Assert.Equal("updating", await setup.StateAsync(worker.Id));
        Assert.Equal("unavailable", await setup.StateAsync(elsewhere.Id));

        // Taking back an update that was never asked for is nothing.
        Assert.Equal(HttpStatusCode.NoContent, (await setup.Client.DeleteAsync($"/api/agents/{elsewhere.Id}/update")).StatusCode);
    }

    [Fact]
    public async Task An_enrollment_token_needs_no_labels()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await InstallationTrustTests.AuthenticateAsync(server);
        var created = await client.PostAsJsonAsync("/api/agents/tokens", new { name = "spare" });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        (await client.PostAsJsonAsync("/api/agents/tokens", new { name = "spare-2", labels = (string[]?)null })).EnsureSuccessStatusCode();
        var tokens = (await client.GetFromJsonAsync<JsonArray>("/api/agents/tokens"))!;
        Assert.All(tokens, t => Assert.Empty(t!["labels"]!.AsArray()));
    }
}
