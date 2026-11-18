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
public class AgentUpdateTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
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
    private static TestRepository Lab(string version = "0.0.1", bool second = false, string policy = "policy: { approval: manual }",
        string destructive = "false")
    {
        var test = GuestConfigurationTests.Repository(policy, destructive: destructive);
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

        /// <summary>The agent connects again, reporting <paramref name="version"/>, and updates are moved on as a connection does.</summary>
        public async Task<FakeAgent> ReportVersionAsync(FakeAgent agent, string version, string[]? executors = null,
            IReadOnlyDictionary<string, string>? unavailable = null, params string[] running)
        {
            var back = await Server.ReconnectAsync(agent, version, executors, unavailable, running);
            await Updates.AdvanceAsync(CancellationToken.None);
            return back;
        }

        public Task<HttpResponseMessage> RequestAsync(Guid id) => Client.PostAsync($"/api/agents/{id}/update", null);

        public Task<HttpResponseMessage> RevertAsync(Guid id) => Client.PostAsync($"/api/agents/{id}/revert", null);

        /// <summary>How many times the agent's update was told failed.</summary>
        public Task<int> FailuresToldAsync(Guid id) =>
            Server.QueryAsync(db => db.Notifications.CountAsync(n => n.AgentId == id && n.Kind == NotificationKinds.AgentUpdateFailed));

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
    private async Task<Setup> StartAsync(TestRepository lab, bool publish = true, TimeProvider? clock = null,
        Action<IServiceCollection>? services = null)
    {
        var settings = new Dictionary<string, string>();
        if (publish)
            settings["Octopushy:RepositoryEditor:PublishEnabled"] = "true";
        var server = await ServerHarness.StartAsync(postgres, lab, settings: settings, clock: clock, services: services);
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
        // Not the only agent, so the runner is asked where it reaches.
        await WorkerRoutingTests.OfflineSpareAsync(setup.Server);
        await ProvisionAsync(setup, runner);
        var worker = await setup.Server.ConnectAgentFromAsync(GuestAddress, "krb-app-01", "home");
        worker = await setup.ReportVersionAsync(worker, "0.0.1");
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

        worker = await setup.ReportVersionAsync(worker, Target);
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
        worker = await setup.ReportVersionAsync(worker, "0.0.1");
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
        // The setup that restarts it was handed out: the deadline runs from then, and the agent is not told offline meanwhile.
        var record = await setup.RecordAsync(worker.Id);
        Assert.NotNull(record.UpdateReleasedAt);
        var update = (await setup.AgentAsync(worker.Id))["update"]!;
        Assert.Equal("updating", update["state"]!.GetValue<string>());
        Assert.Equal(AgentUpdatePhases.Restarting, update["phase"]!.GetValue<string>());
        Assert.Equal(record.UpdateReleasedAt!.Value.AddMinutes(30), update["deadlineAt"]!.GetValue<DateTimeOffset>());

        // Just before the deadline it still waits, away as its restart leaves it and not told offline; after it, it fails.
        setup.Server.Services.GetRequiredService<AgentConnections>().Disconnect(worker.Id);
        var presence = setup.Server.Services.GetRequiredService<AgentPresence>();
        Task<int> OfflineToldAsync() => setup.Server.QueryAsync(db => db.Notifications.CountAsync(n => n.AgentId == worker.Id && n.Kind == NotificationKinds.AgentOffline));
        clock.Now = record.UpdateReleasedAt.Value.AddMinutes(29);
        await setup.Updates.AdvanceAsync(CancellationToken.None);
        await presence.CheckAsync(CancellationToken.None);
        Assert.Equal("updating", await setup.StateAsync(worker.Id));
        Assert.Equal(0, await OfflineToldAsync());
        clock.Now = record.UpdateReleasedAt.Value.AddMinutes(31);
        await setup.Updates.AdvanceAsync(CancellationToken.None);
        await setup.Updates.AdvanceAsync(CancellationToken.None);
        var listed = await setup.AgentAsync(worker.Id);
        Assert.Equal("failed", listed["update"]!["state"]!.GetValue<string>());
        Assert.Equal(AgentUpdateFailures.DidNotComeBack, listed["update"]!["failure"]!.GetValue<string>());
        Assert.Contains("did not connect again", listed["update"]!["reason"]!.GetValue<string>());
        Assert.Equal(1, await setup.FailuresToldAsync(worker.Id));
        await presence.CheckAsync(CancellationToken.None);
        Assert.Equal(1, await OfflineToldAsync());
        Assert.NotNull(listed["update"]!["changeId"]);
        Assert.True((await setup.RecordAsync(worker.Id)).Draining);

        // The repository declares the target now, so asking again is refused until its setup rolls it out.
        var again = await setup.RequestAsync(worker.Id);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("has not rolled it out yet", await again.Content.ReadAsStringAsync());

        // Back on the target after all: current, and still paused, as the operator left it.
        worker = await setup.ReportVersionAsync(worker, Target);
        Assert.Equal("current", await setup.StateAsync(worker.Id));
        Assert.True((await setup.RecordAsync(worker.Id)).Draining);
    }

    [Fact]
    public async Task An_update_does_not_approve_a_destructive_setup_and_waits_for_a_person_without_a_deadline()
    {
        var clock = new TestClock();
        await using var setup = await StartAsync(Lab(policy: "policy: { approval: manual, destructive: automatic }", destructive: "true"), clock: clock);
        var runner = await setup.Server.ConnectAgentFromAsync(IPAddress.Parse("10.9.9.9"), "runner", "home");
        await ProvisionAsync(setup, runner);
        var worker = await setup.Server.ConnectAgentFromAsync(GuestAddress, "krb-app-01", "home");
        worker = await setup.ReportVersionAsync(worker, "0.0.1");

        Assert.Equal(HttpStatusCode.NoContent, (await setup.RequestAsync(worker.Id)).StatusCode);
        var done = await WorkAsync(setup, runner);

        // Its setup waits for a person, as any destructive one under a manual approval policy.
        Assert.DoesNotContain("ansible:run", done);
        var configure = await setup.Server.LatestRunAsync(Guest, Operations.Configure);
        var approval = configure.Steps.Single(s => s.Name == StepNames.Approval);
        Assert.Equal(StepStatuses.AwaitingApproval, approval.Status);
        Assert.Null(approval.DecidedBy);
        await setup.Updates.AdvanceAsync(CancellationToken.None);
        var update = (await setup.AgentAsync(worker.Id))["update"]!;
        Assert.Equal("updating", update["state"]!.GetValue<string>());
        Assert.Equal(AgentUpdatePhases.AwaitingApproval, update["phase"]!.GetValue<string>());
        Assert.Null(update["deadlineAt"]);

        // A day later it still waits for the person, and fails nothing.
        clock.Now = DateTimeOffset.UtcNow.AddDays(1);
        await setup.Updates.AdvanceAsync(CancellationToken.None);
        // Read from the record: a day later the operator's session has expired.
        Assert.Equal(AgentUpdateStates.Updating, (await setup.RecordAsync(worker.Id)).UpdateState);
        Assert.Equal(0, await setup.FailuresToldAsync(worker.Id));
    }

    [Fact]
    public async Task An_update_left_without_its_change_fails_and_lifts_its_pause()
    {
        await using var setup = await StartAsync(Lab());
        var worker = await setup.Server.ConnectAgentFromAsync(GuestAddress, "krb-app-01", "home");
        worker = await setup.ReportVersionAsync(worker, "0.0.1");
        // As a server leaves it when it stops between claiming the update and recording its change.
        await setup.Server.WithDbAsync(async db =>
        {
            await db.Agents.Where(a => a.Id == worker.Id).ExecuteUpdateAsync(u => u
                .SetProperty(a => a.UpdateState, AgentUpdateStates.Updating)
                .SetProperty(a => a.UpdateTargetVersion, Target)
                .SetProperty(a => a.Draining, true)
                .SetProperty(a => a.UpdatePaused, true));
        });

        await setup.Updates.AdvanceAsync(CancellationToken.None);

        var listed = await setup.AgentAsync(worker.Id);
        Assert.Equal("failed", listed["update"]!["state"]!.GetValue<string>());
        Assert.Contains("stopped before", listed["update"]!["reason"]!.GetValue<string>());
        Assert.False((await setup.RecordAsync(worker.Id)).Draining);
    }

    [Fact]
    public async Task Updates_go_one_at_a_time_and_a_failure_empties_the_queue()
    {
        var lab = Lab(second: true, policy: "");
        lab.Write(ThirdFile, File.ReadAllText(Path.Combine(lab.Root, SecondFile)).Replace("vmid: 311", "vmid: 312").Replace("10.1.0.32", "10.1.0.33"));
        await using var setup = await StartAsync(lab);
        var runner = await setup.Server.ConnectAgentFromAsync(IPAddress.Parse("10.9.9.9"), "runner", "home");
        // The workers take none of the lab's work: the runner does it all, and the test answers it.
        var first = await setup.Server.ConnectAgentFromAsync(GuestAddress, "krb-app-01", "elsewhere");
        var second = await setup.Server.ConnectAgentFromAsync(SecondAddress, "krb-app-02", "elsewhere");
        var third = await setup.Server.ConnectAgentFromAsync(IPAddress.Parse("10.1.0.33"), "krb-app-03", "elsewhere");
        first = await setup.ReportVersionAsync(first, "0.0.1");
        second = await setup.ReportVersionAsync(second, "0.0.1");
        third = await setup.ReportVersionAsync(third, "0.0.1");
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

        // The first's change is rolled out and it is back: the second starts.
        Assert.Contains("ansible:run", await WorkAsync(setup, runner));
        first = await setup.ReportVersionAsync(first, Target);
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
        worker = await setup.ReportVersionAsync(worker, "0.0.1");
        var elsewhere = await setup.Server.ConnectAgentFromAsync(IPAddress.Parse("10.9.9.9"), "internal", "home");
        elsewhere = await setup.ReportVersionAsync(elsewhere, "0.0.1");

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

    /// <summary>The guest provisioned by a runner, its worker on 0.0.1 asked to update, and its setup rolled out by the runner.</summary>
    private static async Task<(FakeAgent Runner, FakeAgent Worker)> RolledOutAsync(Setup setup)
    {
        var runner = await setup.Server.ConnectAgentFromAsync(IPAddress.Parse("10.9.9.9"), "runner", "home");
        await ProvisionAsync(setup, runner);
        var worker = await setup.Server.ConnectAgentFromAsync(GuestAddress, "krb-app-01", "home");
        worker = await setup.ReportVersionAsync(worker, "0.0.1");
        Assert.Equal(HttpStatusCode.NoContent, (await setup.RequestAsync(worker.Id)).StatusCode);
        await WorkAsync(setup, runner);
        Assert.Equal(RunStatuses.Succeeded, (await setup.Server.LatestRunAsync(Guest, Operations.Configure)).Status);
        await setup.Updates.AdvanceAsync(CancellationToken.None);
        Assert.Equal("updating", await setup.StateAsync(worker.Id));
        return (runner, worker);
    }

    /// <summary>
    /// Two worker guests and the worker krb-app-01, on 0.0.1, alone at first: it provisions both and is handed krb-app-02's
    /// setup, which it keeps running, a change. A runner then sets up krb-app-01, which the worker never does itself.
    /// </summary>
    private static async Task<(FakeAgent Worker, FakeAgent Runner, Assignment Held)> BusyWorkerAsync(Setup setup)
    {
        var worker = await setup.Server.ConnectAgentFromAsync(GuestAddress, "krb-app-01", "home");
        worker = await setup.ReportVersionAsync(worker, "0.0.1");
        Assignment? held = null;
        await WorkAsync(setup, worker, stop: a =>
        {
            if (a is not { Executor: "ansible", Action: "run" })
                return false;
            held = a;
            return true;
        });
        Assert.NotNull(held);
        Assert.True(held.Mutating);
        await setup.Server.Engine.OnStartedAsync(worker.Id, Guid.Parse(held.AttemptId), CancellationToken.None);
        var runner = await setup.Server.ConnectAgentFromAsync(IPAddress.Parse("10.9.9.9"), "runner", "home");
        await WorkAsync(setup, runner);
        Assert.Equal(RunStatuses.Succeeded, (await setup.Server.LatestRunAsync(Guest, Operations.Configure)).Status);
        return (worker, runner, held);
    }

    private static async Task<Attempt> PlaybookAsync(Setup setup) =>
        (await setup.Server.LatestRunAsync(Guest, Operations.Configure)).Steps.Single(s => s.Name == StepNames.Playbook).Attempts.Single();

    [Fact]
    public async Task An_update_waits_for_a_change_whose_lease_ran_out_until_the_agent_says_it_stopped()
    {
        var clock = new TestClock();
        await using var setup = await StartAsync(Lab(second: true, policy: ""), clock: clock);
        var (worker, runner, held) = await BusyWorkerAsync(setup);
        // The worker's channel is lost and the change's lease runs out: uncertain, and it may still run on the worker.
        setup.Server.Services.GetRequiredService<AgentConnections>().Disconnect(worker.Id);
        clock.Now = DateTimeOffset.UtcNow.AddMinutes(2);
        await setup.Server.TickAsync();
        var heldId = Guid.Parse(held.AttemptId);
        var attempt = await setup.Server.QueryAsync(db => db.Attempts.AsNoTracking().SingleAsync(a => a.Id == heldId));
        Assert.Equal(AttemptStatuses.Uncertain, attempt.Status);
        // It connects again, saying it still runs it.
        clock.Now = clock.Now.Value.AddSeconds(5);
        worker = await setup.ReportVersionAsync(worker, "0.0.1", null, null, held.AttemptId);

        Assert.Equal(HttpStatusCode.NoContent, (await setup.RequestAsync(worker.Id)).StatusCode);
        await WorkAsync(setup, runner);

        // The playbook that restarts it waits, and says for what.
        var playbook = await PlaybookAsync(setup);
        Assert.Equal(AttemptStatuses.Queued, playbook.Status);
        Assert.Contains("lease ran out", playbook.WaitingReason);
        var update = (await setup.AgentAsync(worker.Id))["update"]!;
        Assert.Equal(AgentUpdatePhases.WaitingForUnconfirmedChange, update["phase"]!.GetValue<string>());
        Assert.Equal(heldId, update["waitingForAttemptId"]!.GetValue<Guid>());
        Assert.Equal(attempt.RunId, update["waitingForRunId"]!.GetValue<Guid>());
        Assert.Equal(AgentUpdateHolds.UnconfirmedChange, (await setup.RecordAsync(worker.Id)).UpdateHold);

        // A heartbeat that no longer lists it: it stopped, and the playbook goes.
        clock.Now = clock.Now.Value.AddSeconds(5);
        worker.Session.ReportRunning([], clock.GetUtcNow());
        await setup.Server.TickAsync();
        Assert.Contains(runner.TakeAssignments(), a => a is { Executor: "ansible", Action: "run" });
        var record = await setup.RecordAsync(worker.Id);
        Assert.Null(record.UpdateHold);
        Assert.NotNull(record.UpdateReleasedAt);
    }

    [Fact]
    public async Task An_update_holds_changes_to_its_guest_from_its_claim_while_its_commit_is_pushed()
    {
        await using var setup = await StartAsync(Lab(second: true, policy: ""));
        var (worker, runner, held) = await BusyWorkerAsync(setup);
        var resource = await setup.Server.ResourceAsync(Guest);
        // The remote takes the update's push only once the test lets it, as a slow Git host would: outside the engine's lock.
        var received = Path.Combine(setup.Remote.Root, "push-received");
        var release = Path.Combine(setup.Remote.Root, "push-release");
        var hook = Path.Combine(setup.Remote.Root, "hooks", "pre-receive");
        File.WriteAllText(hook, $"#!/bin/sh\ncat >/dev/null\ntouch '{received}'\nwhile [ ! -e '{release}' ]; do sleep 0.1; done\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var request = setup.RequestAsync(worker.Id);
        try
        {
            for (var i = 0; i < 300 && !File.Exists(received); i++)
                await Task.Delay(100);
            Assert.True(File.Exists(received), "The update's push never reached the remote.");

            // Claimed, with its guest reserved, and no change recorded yet.
            var record = await setup.RecordAsync(worker.Id);
            Assert.Equal(AgentUpdateStates.Updating, record.UpdateState);
            Assert.Equal(resource.Id, record.UpdateResourceId);
            Assert.Null(record.UpdateChangeId);

            // A setup of the guest asked for meanwhile waits for the worker's change.
            (await setup.Client.PostAsJsonAsync($"/api/guests/{resource.Id}/configure", new { action = "setup" })).EnsureSuccessStatusCode();
            var done = await WorkAsync(setup, runner);
            Assert.DoesNotContain("ansible:run", done);
            var playbook = await PlaybookAsync(setup);
            Assert.Equal(AttemptStatuses.Queued, playbook.Status);
            Assert.Contains("to finish run of run #", playbook.WaitingReason);
        }
        finally
        {
            File.WriteAllText(release, "");
        }

        Assert.Equal(HttpStatusCode.NoContent, (await request).StatusCode);
        Assert.NotNull((await setup.RecordAsync(worker.Id)).UpdateChangeId);
        Assert.Equal(AttemptStatuses.Running, await setup.Server.QueryAsync(db => db.Attempts.Where(a => a.Id == Guid.Parse(held.AttemptId)).Select(a => a.Status).SingleAsync()));
    }

    [Fact]
    public async Task A_refused_commit_fails_the_update_only_once_another_change_to_its_guest_has_settled()
    {
        await using var setup = await StartAsync(Lab());
        var runner = await setup.Server.ConnectAgentFromAsync(IPAddress.Parse("10.9.9.9"), "runner", "home");
        await ProvisionAsync(setup, runner);
        var worker = await setup.Server.ConnectAgentFromAsync(GuestAddress, "krb-app-01", "home");
        worker = await setup.ReportVersionAsync(worker, "0.0.1");
        var resource = await setup.Server.ResourceAsync(Guest);
        // The remote refuses the update's push, once the test lets it answer.
        var received = Path.Combine(setup.Remote.Root, "push-received");
        var release = Path.Combine(setup.Remote.Root, "push-release");
        var hook = Path.Combine(setup.Remote.Root, "hooks", "pre-receive");
        File.WriteAllText(hook, $"#!/bin/sh\ncat >/dev/null\ntouch '{received}'\nwhile [ ! -e '{release}' ]; do sleep 0.1; done\nexit 1\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var request = setup.RequestAsync(worker.Id);
        Assignment second;
        try
        {
            for (var i = 0; i < 300 && !File.Exists(received); i++)
                await Task.Delay(100);
            Assert.True(File.Exists(received), "The update's push never reached the remote.");
            // The worker is idle, so a setup of its guest asked for meanwhile goes out to the runner, and is still running.
            second = await SetupRunningAsync(setup, runner, resource.Id);
        }
        finally
        {
            File.WriteAllText(release, "");
        }

        await request;
        await setup.Updates.AdvanceAsync(CancellationToken.None);
        // Refused, but failing would lift the pause while that setup may restart the agent.
        Assert.Equal("updating", await setup.StateAsync(worker.Id));
        Assert.True((await setup.RecordAsync(worker.Id)).Draining);

        await runner.ReportAsync(second, Outcome.Succeeded, ConfigureRunTests.Recap());
        await setup.Server.TickAsync();
        await setup.Updates.AdvanceAsync(CancellationToken.None);
        var record = await setup.RecordAsync(worker.Id);
        Assert.Equal(AgentUpdateStates.Failed, record.UpdateState);
        Assert.Equal(AgentUpdateFailures.CommitRefused, record.UpdateFailure);
        Assert.False(record.Draining);
    }

    [Fact]
    public async Task A_claim_a_stopped_server_left_fails_only_once_another_change_to_its_guest_has_settled()
    {
        await using var setup = await StartAsync(Lab());
        var runner = await setup.Server.ConnectAgentFromAsync(IPAddress.Parse("10.9.9.9"), "runner", "home");
        await ProvisionAsync(setup, runner);
        var worker = await setup.Server.ConnectAgentFromAsync(GuestAddress, "krb-app-01", "home");
        worker = await setup.ReportVersionAsync(worker, "0.0.1");
        var resource = await setup.Server.ResourceAsync(Guest);
        var second = await SetupRunningAsync(setup, runner, resource.Id);
        // A server claimed the update, paused the worker and stopped before it recorded the change.
        await setup.Server.WithDbAsync(db => db.Agents.Where(a => a.Id == worker.Id).ExecuteUpdateAsync(u => u
            .SetProperty(a => a.UpdateState, AgentUpdateStates.Updating).SetProperty(a => a.UpdateResourceId, resource.Id)
            .SetProperty(a => a.UpdateTargetVersion, Target).SetProperty(a => a.Draining, true).SetProperty(a => a.UpdatePaused, true)));

        await setup.Updates.AdvanceAsync(CancellationToken.None);
        Assert.Equal("updating", await setup.StateAsync(worker.Id));
        Assert.True((await setup.RecordAsync(worker.Id)).Draining);

        await runner.ReportAsync(second, Outcome.Succeeded, ConfigureRunTests.Recap());
        await setup.Server.TickAsync();
        await setup.Updates.AdvanceAsync(CancellationToken.None);
        var record = await setup.RecordAsync(worker.Id);
        Assert.Equal(AgentUpdateStates.Failed, record.UpdateState);
        Assert.Equal(AgentUpdateFailures.ChangeNotRecorded, record.UpdateFailure);
        Assert.False(record.Draining);
    }

    /// <summary>Asks for a setup of the guest and hands it to <paramref name="runner"/> up to its playbook, left running.</summary>
    private static async Task<Assignment> SetupRunningAsync(Setup setup, FakeAgent runner, Guid resourceId)
    {
        (await setup.Client.PostAsJsonAsync($"/api/guests/{resourceId}/configure", new { action = "setup" })).EnsureSuccessStatusCode();
        Assignment? playbook = null;
        await WorkAsync(setup, runner, approve: true, stop: a =>
        {
            if (a is not { Executor: "ansible", Action: "run" })
                return false;
            playbook = a;
            return true;
        });
        Assert.NotNull(playbook);
        return playbook;
    }

    [Fact]
    public async Task A_lost_executor_fails_the_update_though_another_change_to_the_guest_goes_out_after_the_new_hello()
    {
        await using var setup = await StartAsync(Lab());
        var runner = await setup.Server.ConnectAgentFromAsync(IPAddress.Parse("10.9.9.9"), "runner", "home");
        await ProvisionAsync(setup, runner);
        var worker = await setup.Server.ConnectAgentFromAsync(GuestAddress, "krb-app-01", "home");
        worker = await setup.ReportVersionAsync(worker, "0.0.1");
        Assert.Equal(HttpStatusCode.NoContent, (await setup.RequestAsync(worker.Id)).StatusCode);
        Assignment? rollout = null;
        await WorkAsync(setup, runner, stop: a =>
        {
            if (a is not { Executor: "ansible", Action: "run" })
                return false;
            rollout = a;
            return true;
        });
        Assert.NotNull(rollout);
        await runner.ReportAsync(rollout, Outcome.Succeeded, ConfigureRunTests.Recap());
        await setup.Server.TickAsync();

        // The rollout restarted the worker: its new process says hello on the target without OpenTofu, and the update has not
        // been moved on yet.
        worker = await setup.Server.ReconnectAsync(worker, Target, ["ansible", "proxmox"],
            new Dictionary<string, string> { ["opentofu"] = "tofu is not installed" });
        // Another change to the guest goes out to the runner before it is.
        var resource = await setup.Server.ResourceAsync(Guest);
        (await setup.Client.PostAsJsonAsync($"/api/guests/{resource.Id}/configure", new { action = "setup" })).EnsureSuccessStatusCode();
        Assert.Contains("ansible:run", await WorkAsync(setup, runner, approve: true));
        await setup.Updates.AdvanceAsync(CancellationToken.None);

        var update = (await setup.AgentAsync(worker.Id))["update"]!;
        Assert.Equal("failed", update["state"]!.GetValue<string>());
        Assert.Equal(AgentUpdateFailures.LostExecutors, update["failure"]!.GetValue<string>());
        Assert.Contains("opentofu (tofu is not installed)", update["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_update_past_its_deadline_does_not_fail_while_another_change_to_its_guest_still_runs()
    {
        var clock = new TestClock();
        await using var setup = await StartAsync(Lab(), clock: clock);
        var runner = await setup.Server.ConnectAgentFromAsync(IPAddress.Parse("10.9.9.9"), "runner", "home");
        await ProvisionAsync(setup, runner);
        var worker = await setup.Server.ConnectAgentFromAsync(GuestAddress, "krb-app-01", "home");
        worker = await setup.ReportVersionAsync(worker, "0.0.1");
        Assert.Equal(HttpStatusCode.NoContent, (await setup.RequestAsync(worker.Id)).StatusCode);
        Assignment? rollout = null;
        await WorkAsync(setup, runner, stop: a =>
        {
            if (a is not { Executor: "ansible", Action: "run" })
                return false;
            rollout = a;
            return true;
        });
        Assert.NotNull(rollout);
        await runner.ReportAsync(rollout, Outcome.Succeeded, ConfigureRunTests.Recap());
        await setup.Server.TickAsync();
        worker = await setup.Server.ReconnectAsync(worker, Target);

        // Another setup of the guest goes out to the runner before the update is moved on, and is still running.
        var resource = await setup.Server.ResourceAsync(Guest);
        (await setup.Client.PostAsJsonAsync($"/api/guests/{resource.Id}/configure", new { action = "setup" })).EnsureSuccessStatusCode();
        Assignment? second = null;
        await WorkAsync(setup, runner, approve: true, stop: a =>
        {
            if (a is not { Executor: "ansible", Action: "run" })
                return false;
            second = a;
            return true;
        });
        Assert.NotNull(second);

        // Past the deadline, the update neither ends nor fails: failing would lift the pause while that setup may restart the agent.
        var record = await setup.RecordAsync(worker.Id);
        clock.Now = record.UpdateReleasedAt!.Value.AddMinutes(31);
        await setup.Updates.AdvanceAsync(CancellationToken.None);
        await setup.Updates.AdvanceAsync(CancellationToken.None);
        Assert.Equal("updating", await setup.StateAsync(worker.Id));
        Assert.True((await setup.RecordAsync(worker.Id)).Draining);

        // Once it has settled, the update is done.
        await runner.ReportAsync(second, Outcome.Succeeded, ConfigureRunTests.Recap());
        await setup.Server.TickAsync();
        await setup.Updates.AdvanceAsync(CancellationToken.None);
        Assert.Equal("current", await setup.StateAsync(worker.Id));
        var done = await setup.RecordAsync(worker.Id);
        Assert.Null(done.UpdateFailure);
        Assert.False(done.Draining);
    }

    [Fact]
    public async Task A_revert_after_an_update_that_never_restarted_the_agent_ends_only_once_its_change_applied()
    {
        await using var setup = await StartAsync(Lab());
        var runner = await setup.Server.ConnectAgentFromAsync(IPAddress.Parse("10.9.9.9"), "runner", "home");
        await ProvisionAsync(setup, runner);
        var worker = await setup.Server.ConnectAgentFromAsync(GuestAddress, "krb-app-01", "home");
        worker = await setup.ReportVersionAsync(worker, "0.0.1");
        Assert.Equal(HttpStatusCode.NoContent, (await setup.RequestAsync(worker.Id)).StatusCode);
        // Its runs are cancelled before anything restarts the worker: failed, the worker still on 0.0.1, the guest declaring the new version.
        var changeId = (await setup.RecordAsync(worker.Id)).UpdateChangeId!.Value;
        foreach (var run in (await setup.Client.GetFromJsonAsync<ChangeDto>($"/api/changes/{changeId}"))!.Runs.Where(r => !RunStatuses.IsTerminal(r.Status)))
            (await setup.Client.PostAsync($"/api/runs/{run.Id}/cancel", null)).EnsureSuccessStatusCode();
        await setup.Updates.AdvanceAsync(CancellationToken.None);
        Assert.Equal("failed", await setup.StateAsync(worker.Id));
        Assert.True((await setup.AgentAsync(worker.Id))["update"]!["canRevert"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.NoContent, (await setup.RevertAsync(worker.Id)).StatusCode);
        await setup.Updates.AdvanceAsync(CancellationToken.None);

        // Its connection already reports 0.0.1, but its change has not applied: still in flight, paused.
        Assert.Equal("updating", await setup.StateAsync(worker.Id));
        Assert.True((await setup.RecordAsync(worker.Id)).Draining);
        Assert.Contains("ansible:run", await WorkAsync(setup, runner));
        var approval = (await setup.Server.LatestRunAsync(Guest, Operations.Configure)).Steps.Single(s => s.Name == StepNames.Approval);
        Assert.Equal("ops", approval.DecidedBy);
        await setup.Updates.AdvanceAsync(CancellationToken.None);

        Assert.Equal("available", await setup.StateAsync(worker.Id));
        var record = await setup.RecordAsync(worker.Id);
        Assert.Null(record.UpdateState);
        Assert.False(record.Draining);
    }

    [Fact]
    public async Task An_agent_being_updated_takes_no_work_whatever_its_pause_and_a_pause_a_person_sets_stays()
    {
        await using var setup = await StartAsync(Lab());
        var runner = await setup.Server.ConnectAgentFromAsync(IPAddress.Parse("10.9.9.9"), "runner", "home");
        await ProvisionAsync(setup, runner);
        var worker = await setup.Server.ConnectAgentFromAsync(GuestAddress, "krb-app-01", "home");
        worker = await setup.ReportVersionAsync(worker, "0.0.1");
        Assert.Equal(HttpStatusCode.NoContent, (await setup.RequestAsync(worker.Id)).StatusCode);
        Assignment? rollout = null;
        await WorkAsync(setup, runner, stop: a =>
        {
            if (a is not { Executor: "ansible", Action: "run" })
                return false;
            rollout = a;
            return true;
        });
        Assert.NotNull(rollout);
        Assert.NotNull((await setup.RecordAsync(worker.Id)).UpdateReleasedAt);

        // A person lifts its pause while the playbook that restarts it runs; work comes that either agent could take.
        (await setup.Client.PatchAsJsonAsync($"/api/agents/{worker.Id}", new { draining = false })).EnsureSuccessStatusCode();
        Assert.False((await setup.RecordAsync(worker.Id)).Draining);
        await setup.Server.WithDbAsync(db => db.Resources.Where(r => r.Kind == ResourceKinds.Installation)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.ObserveRequestedAt, (DateTimeOffset?)null)));
        await setup.Server.Services.GetRequiredService<Planner>().ScheduleObservesAsync(CancellationToken.None);
        await setup.Server.TickAsync();
        Assert.Empty(worker.TakeAssignments());
        Assert.Contains(runner.TakeAssignments(), a => a.Action == "observe");

        // The person pauses it again: theirs, and the end of the update leaves it.
        (await setup.Client.PatchAsJsonAsync($"/api/agents/{worker.Id}", new { draining = true })).EnsureSuccessStatusCode();
        await runner.ReportAsync(rollout, Outcome.Succeeded, ConfigureRunTests.Recap());
        await setup.Server.TickAsync();
        await setup.Updates.AdvanceAsync(CancellationToken.None);
        worker = await setup.ReportVersionAsync(worker, Target);
        Assert.Equal("current", await setup.StateAsync(worker.Id));
        var record = await setup.RecordAsync(worker.Id);
        Assert.True(record.Draining);
        Assert.False(record.UpdatePaused);
    }

    [Fact]
    public async Task An_update_waits_while_its_agent_runs_a_change_and_ends_when_it_connects_on_the_new_version()
    {
        var counter = new IdleTickTests.CommandCounter();
        await using var setup = await StartAsync(Lab(second: true, policy: ""),
            services: s => s.ConfigureDbContext<OctopushyDb>(o => o.AddInterceptors(counter)));
        var (worker, runner, held) = await BusyWorkerAsync(setup);
        var second = await setup.Server.ConnectAgentFromAsync(SecondAddress, "krb-app-02", "home");
        second = await setup.ReportVersionAsync(second, "0.0.1");

        // Update all: the busy worker first, the second queued behind it and not paused.
        Assert.Equal(HttpStatusCode.NoContent, (await setup.Client.PostAsync("/api/agents/update", null)).StatusCode);
        Assert.Equal("queued", await setup.StateAsync(second.Id));
        Assert.False((await setup.RecordAsync(second.Id)).Draining);
        var record = await setup.RecordAsync(worker.Id);
        Assert.Equal(AgentUpdateStates.Updating, record.UpdateState);
        Assert.True(record.Draining);
        Assert.Equal("0.0.1", record.UpdatePreviousVersion);
        Assert.Equal(["ansible", "opentofu", "proxmox"], record.UpdatePreviousExecutors!);

        // Its setup is previewed by the runner; the playbook, which restarts the worker, waits for the worker's change.
        await WorkAsync(setup, runner);
        var playbook = (await setup.Server.LatestRunAsync(Guest, Operations.Configure)).Steps.Single(s => s.Name == StepNames.Playbook).Attempts.Single();
        Assert.Equal(AttemptStatuses.Queued, playbook.Status);
        Assert.Contains("to finish run of run #", playbook.WaitingReason);
        Assert.Equal(held.AttemptId, (await setup.AgentAsync(worker.Id))["update"]!["waitingForAttemptId"]!.GetValue<string>());
        var update = (await setup.AgentAsync(worker.Id))["update"]!;
        Assert.Equal(AgentUpdatePhases.WaitingForIdle, update["phase"]!.GetValue<string>());
        Assert.Null(update["deadlineAt"]);
        Assert.Null((await setup.RecordAsync(worker.Id)).UpdateReleasedAt);
        Assert.Empty(worker.TakeAssignments());
        Assert.Equal(AttemptStatuses.Running, await setup.Server.QueryAsync(db => db.Attempts.Where(a => a.Id == Guid.Parse(held.AttemptId)).Select(a => a.Status).SingleAsync()));
        Assert.Empty(worker.TakeCancels());

        // Waiting costs a tick nothing more each time, and saves nothing.
        var writes = setup.Server.Services.GetRequiredService<SavedWrites>();
        async Task PassAsync()
        {
            await setup.Server.TickAsync();
            await setup.Updates.AdvanceAsync(CancellationToken.None);
        }

        // The passes after the one that settles what was saved before.
        await PassAsync();
        await PassAsync();
        var saved = writes.Count;
        var one = await counter.CountAsync(PassAsync);
        var two = await counter.CountAsync(PassAsync);
        Assert.Equal(one, two);
        Assert.Equal(saved, writes.Count);
        Assert.Equal(AgentUpdatePhases.WaitingForIdle, (await setup.AgentAsync(worker.Id))["update"]!["phase"]!.GetValue<string>());

        // The worker's change finishes: the playbook goes to the runner, and the deadline starts.
        Assert.True(await worker.ReportAsync(held.AttemptId, Outcome.Succeeded, ConfigureRunTests.Recap()));
        await setup.Server.TickAsync();
        var rollout = Assert.Single(runner.TakeAssignments(), a => a is { Executor: "ansible", Action: "run" });
        record = await setup.RecordAsync(worker.Id);
        Assert.NotNull(record.UpdateReleasedAt);
        Assert.Null(record.UpdateHold);
        Assert.Equal(AgentUpdatePhases.Restarting, (await setup.AgentAsync(worker.Id))["update"]!["phase"]!.GetValue<string>());
        await runner.ReportAsync(rollout, Outcome.Succeeded, ConfigureRunTests.Recap());
        await PassAsync();

        // The worker restarts on the new version and connects over the agent channel: the update is done.
        setup.Server.Services.GetRequiredService<AgentConnections>().Disconnect(worker.Id);
        var stream = new AgentStream();
        stream.Send(new AgentMessage { Hello = new Hello { AgentVersion = Target, Executors = { "opentofu", "proxmox", "ansible" } } });
        var service = ActivatorUtilities.CreateInstance<AgentChannelService>(setup.Server.Services);
        _ = service.Connect(stream, stream, await AgentStream.CallAsAsync(setup.Server, worker));
        await stream.ReadingMessages.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var listed = await setup.AgentAsync(worker.Id);
        Assert.Equal("current", listed["update"]!["state"]!.GetValue<string>());
        Assert.Equal("0.0.1", listed["update"]!["previousVersion"]!.GetValue<string>());
        Assert.True(listed["update"]!["canRevert"]!.GetValue<bool>());
        record = await setup.RecordAsync(worker.Id);
        Assert.False(record.Draining);
        Assert.Null(record.UpdateState);
        Assert.Equal(0, await setup.FailuresToldAsync(worker.Id));
        // The queue moves on.
        Assert.Equal("updating", await setup.StateAsync(second.Id));
        stream.Complete();
    }

    [Fact]
    public async Task Revert_brings_back_the_recorded_version_with_a_new_commit()
    {
        await using var setup = await StartAsync(Lab());
        // Nothing to revert to before an update.
        var runner = await setup.Server.ConnectAgentFromAsync(IPAddress.Parse("10.9.9.9"), "runner", "home");
        await ProvisionAsync(setup, runner);
        var worker = await setup.Server.ConnectAgentFromAsync(GuestAddress, "krb-app-01", "home");
        worker = await setup.ReportVersionAsync(worker, "0.0.1");
        var listed = await setup.AgentAsync(worker.Id);
        Assert.False(listed["update"]!["canRevert"]!.GetValue<bool>());
        Assert.Null(listed["update"]!["previousVersion"]);
        var refused = await setup.RevertAsync(worker.Id);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("No version from before", await refused.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await setup.RequestAsync(worker.Id)).StatusCode);
        await WorkAsync(setup, runner);
        // Not while it is in flight.
        Assert.False((await setup.AgentAsync(worker.Id))["update"]!["canRevert"]!.GetValue<bool>());
        worker = await setup.ReportVersionAsync(worker, Target);
        listed = await setup.AgentAsync(worker.Id);
        Assert.Equal("current", listed["update"]!["state"]!.GetValue<string>());
        Assert.True(listed["update"]!["canRevert"]!.GetValue<bool>());
        var commits = int.Parse(TestWorkflows.Run(setup.Remote.Root, "rev-list", "--count", "main").Trim());

        Assert.Equal(HttpStatusCode.NoContent, (await setup.RevertAsync(worker.Id)).StatusCode);
        // A new commit sets the old version; the update's commit stays in the history.
        Assert.Contains("  worker_agent_version: 0.0.1\n", setup.RemoteFile(GuestFile));
        Assert.Equal(commits + 1, int.Parse(TestWorkflows.Run(setup.Remote.Root, "rev-list", "--count", "main").Trim()));
        Assert.Contains($"worker_agent_version: {Target}", TestWorkflows.Run(setup.Remote.Root, "show", $"main~1:{GuestFile}"));
        listed = await setup.AgentAsync(worker.Id);
        Assert.Equal("updating", listed["update"]!["state"]!.GetValue<string>());
        Assert.True(listed["update"]!["revert"]!.GetValue<bool>());
        Assert.Equal("0.0.1", listed["update"]!["targetVersion"]!.GetValue<string>());
        Assert.True((await setup.RecordAsync(worker.Id)).Draining);

        // Rolled out by the runner, approved with the revert as the update was; back on 0.0.1, it is done.
        Assert.Contains("ansible:run", await WorkAsync(setup, runner));
        worker = await setup.ReportVersionAsync(worker, "0.0.1");
        listed = await setup.AgentAsync(worker.Id);
        Assert.Equal("available", listed["update"]!["state"]!.GetValue<string>());
        Assert.Equal("0.0.1", listed["update"]!["previousVersion"]!.GetValue<string>());
        Assert.False(listed["update"]!["canRevert"]!.GetValue<bool>());
        var record = await setup.RecordAsync(worker.Id);
        Assert.Null(record.UpdateState);
        Assert.False(record.Draining);
        Assert.Equal(HttpStatusCode.Conflict, (await setup.RevertAsync(worker.Id)).StatusCode);
    }

    [Fact]
    public async Task An_update_that_loses_an_executor_fails_and_offers_revert()
    {
        await using var setup = await StartAsync(Lab());
        var (_, worker) = await RolledOutAsync(setup);

        worker = await setup.ReportVersionAsync(worker, Target, ["ansible", "proxmox"], new Dictionary<string, string> { ["opentofu"] = "tofu is not installed" });
        await setup.Updates.AdvanceAsync(CancellationToken.None);

        var listed = await setup.AgentAsync(worker.Id);
        Assert.Equal("failed", listed["update"]!["state"]!.GetValue<string>());
        Assert.Equal(AgentUpdateFailures.LostExecutors, listed["update"]!["failure"]!.GetValue<string>());
        var reason = listed["update"]!["reason"]!.GetValue<string>();
        Assert.Contains("opentofu (tofu is not installed)", reason);
        Assert.Contains("Nothing was rolled back", reason);
        Assert.True(listed["update"]!["canRevert"]!.GetValue<bool>());
        Assert.False((await setup.RecordAsync(worker.Id)).Draining);
        Assert.Equal(1, await setup.FailuresToldAsync(worker.Id));
    }

    [Fact]
    public async Task An_update_that_gains_an_executor_succeeds()
    {
        await using var setup = await StartAsync(Lab());
        var (_, worker) = await RolledOutAsync(setup);

        worker = await setup.ReportVersionAsync(worker, Target, ["ansible", "nix", "opentofu", "proxmox"]);

        Assert.Equal("current", await setup.StateAsync(worker.Id));
        Assert.False((await setup.RecordAsync(worker.Id)).Draining);
        Assert.Equal(0, await setup.FailuresToldAsync(worker.Id));
    }

    [Fact]
    public async Task An_agent_that_comes_back_on_the_old_version_fails_its_update()
    {
        await using var setup = await StartAsync(Lab());
        var (_, worker) = await RolledOutAsync(setup);

        worker = await setup.ReportVersionAsync(worker, "0.0.1");
        await setup.Updates.AdvanceAsync(CancellationToken.None);

        var listed = await setup.AgentAsync(worker.Id);
        Assert.Equal("failed", listed["update"]!["state"]!.GetValue<string>());
        Assert.Equal(AgentUpdateFailures.OtherVersion, listed["update"]!["failure"]!.GetValue<string>());
        Assert.Contains("connected again on 0.0.1", listed["update"]!["reason"]!.GetValue<string>());
        Assert.False((await setup.RecordAsync(worker.Id)).Draining);
        Assert.Equal(1, await setup.FailuresToldAsync(worker.Id));
        // The repository declares the new version and the agent runs the old one: Revert puts the repository back.
        Assert.True(listed["update"]!["canRevert"]!.GetValue<bool>());
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
