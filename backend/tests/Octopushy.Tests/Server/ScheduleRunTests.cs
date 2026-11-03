using System.Net.Http.Json;
using System.Text.Json.Nodes;
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
public class ScheduleRunTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";

    /// <summary>The day the tests' clock starts on; a day in the future, so nothing else's time is mistaken for it.</summary>
    private static readonly DateTimeOffset Today = new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero).AddDays(2);

    private static DateTimeOffset At(int hour, int minute = 0, int day = 0) => Today.AddDays(day).AddHours(hour).AddMinutes(minute);

    private static TestRepository Scheduled(string policy = "", string outcomes = "", string action = "update-system", string destructive = "false", string cron = "0 6 * * *")
    {
        var test = Repository.Repository(policy, outcomes, destructive);
        var guest = Path.Combine(test.Root, GuestFile);
        test.Write(GuestFile, File.ReadAllText(guest).Replace(
            "access:\n  user: ops",
            $"schedules:\n    - {{ action: {action}, cron: \"{cron}\" }}\naccess:\n  user: ops"));
        return test;
    }

    private const string Deferred = """
        outcomes:
          deferred:
            description: Work is running.
            approve: { vars: { allow_busy: true } }
        """;

    private const string Declared = """
        outcomes:
          refused: { description: The guest said no., result: failed }
          unchanged: { description: Nothing needed doing., result: succeeded }
        """;

    private static Task FireAsync(ServerHarness server, DateTimeOffset now) =>
        server.Services.GetRequiredService<Planner>().FireSchedulesAsync(now, CancellationToken.None);

    private static Task ConfigureAsync(ServerHarness server) =>
        server.Services.GetRequiredService<Planner>().ScheduleConfigurationsAsync(CancellationToken.None);

    /// <summary>A provisioned guest whose first configure run has succeeded, so it has a snapshot.</summary>
    private static async Task<(FakeAgent Agent, HttpClient Http)> ConfiguredAsync(ServerHarness server)
    {
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        await ConfigureAsync(server);
        await ConfigureRunTests.PreviewAsync(agent, new JsonObject { ["version"] = "1" });
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        return (agent, http);
    }

    private static async Task<GuestScheduleState> StateAsync(ServerHarness server)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OctopushyDb>().GuestSchedules.AsNoTracking().SingleAsync();
    }

    private static async Task<int> ConfigureRunsAsync(ServerHarness server)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OctopushyDb>().Runs.CountAsync(r => r.Operation == Operations.Configure);
    }

    [Fact]
    public async Task A_schedule_fires_once_at_its_time_and_owes_nothing_from_before_it_was_seen()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Scheduled());
        var (_, http) = await ConfiguredAsync(server);
        using var _ = http;
        var before = await ConfigureRunsAsync(server);

        await FireAsync(server, At(6, 5));
        Assert.Equal(before, await ConfigureRunsAsync(server));

        await FireAsync(server, At(7));
        Assert.Equal(before, await ConfigureRunsAsync(server));

        await FireAsync(server, At(6, 1, day: 1));
        await FireAsync(server, At(6, 2, day: 1));
        Assert.Equal(before + 1, await ConfigureRunsAsync(server));
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunReasons.Schedule, run.Reason);
        Assert.Equal(run.Id, (await StateAsync(server)).LastRunId);
    }

    [Fact]
    public async Task A_deferred_scheduled_run_ends_by_itself_promotes_nothing_and_leaves_the_guest_free()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Scheduled(outcomes: Deferred));
        var (agent, http) = await ConfiguredAsync(server);
        using var _ = http;
        await FireAsync(server, At(1));
        await FireAsync(server, At(6, 1));
        await ConfigureRunTests.PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap("deferred", "Three threads."));
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Contains("deferred", run.Detail);
        Assert.Contains("Three threads.", run.Detail);
        Assert.Equal(StepStatuses.Skipped, run.Steps.Single(s => s.Name == StepNames.OutcomeApproval).Status);

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var configuration = await scope.ServiceProvider.GetRequiredService<OctopushyDb>().GuestConfigurations.AsNoTracking().SingleAsync();
            Assert.Equal("setup", configuration.AppliedAction);
            Assert.Contains("\"1\"", configuration.Snapshot);
        }

        await FireAsync(server, At(6, 1, day: 1));
        Assert.NotEqual(run.Id, (await server.LatestRunAsync(Guest, Operations.Configure)).Id);
    }

    [Theory]
    [InlineData("refused", RunStatuses.Failed, "setup")]
    [InlineData("unchanged", RunStatuses.Succeeded, "update-system")]
    public async Task An_outcome_no_person_can_approve_ends_a_scheduled_run_as_it_declares(string outcome, string status, string appliedAction)
    {
        await using var server = await ServerHarness.StartAsync(postgres, Scheduled(outcomes: Declared));
        var (agent, http) = await ConfiguredAsync(server);
        using var _ = http;
        await FireAsync(server, At(1));
        await FireAsync(server, At(6, 1));
        await ConfigureRunTests.PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap(outcome, "From the playbook."));
        await server.TickAsync();

        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(status, run.Status);
        Assert.Contains(outcome, run.Detail);
        Assert.Contains("From the playbook.", run.Detail);
        Assert.DoesNotContain(run.Steps, s => s.Name == StepNames.OutcomeApproval);
        // Only the declared success counts as applied; the first run's promotion otherwise stands.
        Assert.Equal(appliedAction, await server.QueryAsync(db => db.GuestConfigurations.AsNoTracking().Select(c => c.AppliedAction).SingleAsync()));

        await FireAsync(server, At(6, 1, day: 1));
        Assert.NotEqual(run.Id, (await server.LatestRunAsync(Guest, Operations.Configure)).Id);
    }

    [Fact]
    public async Task A_run_started_by_hand_still_waits_for_a_person_on_the_same_outcome()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Scheduled(outcomes: Deferred));
        var (agent, http) = await ConfiguredAsync(server);
        using var _ = http;
        await server.Services.GetRequiredService<Planner>().ConfigureGuestAsync(
            (await server.LatestRunAsync(Guest, Operations.Configure)).ResourceId, "update-system", CancellationToken.None);
        await ConfigureRunTests.PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap("deferred"));
        await server.TickAsync();
        Assert.Equal(RunStatuses.AwaitingApproval, (await server.LatestRunAsync(Guest, Operations.Configure)).Status);
    }

    [Fact]
    public async Task A_firing_while_a_run_is_active_is_skipped_and_says_why()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Scheduled());
        var (agent, http) = await ConfiguredAsync(server);
        using var _ = http;
        await FireAsync(server, At(1));
        await FireAsync(server, At(6, 1));
        var count = await ConfigureRunsAsync(server);

        await FireAsync(server, At(6, 1, day: 1));
        Assert.Equal(count, await ConfigureRunsAsync(server));
        var state = await StateAsync(server);
        Assert.StartsWith("Skipped: Run #", state.Note);

        await ConfigureRunTests.PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        await FireAsync(server, At(6, 1, day: 2));
        Assert.Equal(count + 1, await ConfigureRunsAsync(server));
        Assert.Null((await StateAsync(server)).Note);
    }

    [Fact]
    public async Task An_uncertain_run_holds_the_schedule_until_a_person_closes_it()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Scheduled());
        var (agent, http) = await ConfiguredAsync(server);
        using var _ = http;
        await FireAsync(server, At(1));
        await FireAsync(server, At(6, 1));
        await ConfigureRunTests.PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Uncertain, error: "lost");
        await server.TickAsync();
        var count = await ConfigureRunsAsync(server);

        await FireAsync(server, At(6, 1, day: 1));
        Assert.Equal(count, await ConfigureRunsAsync(server));
        Assert.Contains("uncertain", (await StateAsync(server)).Note);
    }

    [Fact]
    public async Task Under_manual_approval_it_waits_and_the_next_firing_is_skipped()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Scheduled("policy:\n  destructive: manual\n", action: "setup", destructive: "true"));
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        await FireAsync(server, At(1));
        await FireAsync(server, At(6, 1));
        Assert.Equal(1, await ConfigureRunsAsync(server));
        await ConfigureRunTests.PreviewAsync(agent);
        await server.TickAsync();
        Assert.Equal(RunStatuses.AwaitingApproval, (await server.LatestRunAsync(Guest, Operations.Configure)).Status);

        await FireAsync(server, At(6, 1, day: 1));
        Assert.Equal(1, await ConfigureRunsAsync(server));
        Assert.Contains("awaiting-approval", (await StateAsync(server)).Note);
    }

    [Fact]
    public async Task A_time_missed_by_more_than_the_limit_is_skipped_and_only_the_newest_counts()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Scheduled());
        var (_, http) = await ConfiguredAsync(server);
        using var _ = http;
        await FireAsync(server, At(1));
        var count = await ConfigureRunsAsync(server);

        // Down for three days, back at noon: nothing runs, and nothing is owed.
        await FireAsync(server, At(12, day: 3));
        Assert.Equal(count, await ConfigureRunsAsync(server));
        Assert.StartsWith("Missed", (await StateAsync(server)).Note);
        await FireAsync(server, At(12, 1, day: 3));
        Assert.Equal(count, await ConfigureRunsAsync(server));

        // Down for three days, back within the hour: one run, not four.
        await FireAsync(server, At(6, 30, day: 7));
        Assert.Equal(count + 1, await ConfigureRunsAsync(server));
    }

    [Fact]
    public async Task A_failure_is_not_repeated_until_the_next_time_and_stays_on_the_guest_page()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Scheduled());
        var (agent, http) = await ConfiguredAsync(server);
        using var _ = http;
        await FireAsync(server, At(1));
        await FireAsync(server, At(6, 1));
        await ConfigureRunTests.PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Failed, error: "a task failed");
        await server.TickAsync();
        var failed = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.Failed, failed.Status);

        await FireAsync(server, At(6, 30));
        await FireAsync(server, At(23));
        Assert.Equal(failed.Id, (await server.LatestRunAsync(Guest, Operations.Configure)).Id);

        var detail = await http.GetFromJsonAsync<JsonObject>($"/api/guests/{failed.ResourceId}");
        var schedule = detail!["configuration"]!["schedules"]![0]!;
        Assert.Equal("update-system", schedule["action"]!.GetValue<string>());
        Assert.Equal("failed", schedule["lastRunStatus"]!.GetValue<string>());
        Assert.Equal("UTC", schedule["timezone"]!.GetValue<string>());
        Assert.NotNull(schedule["nextAt"]);

        await FireAsync(server, At(6, 1, day: 1));
        Assert.NotEqual(failed.Id, (await server.LatestRunAsync(Guest, Operations.Configure)).Id);
    }

    [Fact]
    public async Task The_page_of_a_guest_with_a_time_that_cannot_be_read_still_opens()
    {
        var test = Repository.Repository();
        var file = Path.Combine(test.Root, GuestFile);
        test.Write(GuestFile, File.ReadAllText(file).Replace(
            "access:\n  user: ops",
            "schedules:\n    - { action: setup, cron: \"not a cron\" }\naccess:\n  user: ops"));
        await using var server = await ServerHarness.StartAsync(postgres, test);
        using var http = server.CreateClient();
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        (await http.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        await server.SyncAndTrustAsync(http);

        await using var scope = server.Services.CreateAsyncScope();
        var guest = await scope.ServiceProvider.GetRequiredService<OctopushyDb>().Resources.AsNoTracking().SingleAsync(r => r.Address == Guest);
        Assert.False(guest.Valid);
        (await http.GetAsync($"/api/guests/{guest.Id}")).EnsureSuccessStatusCode();
        await FireAsync(server, At(6, 1));
    }

    private static Task ExpireAsync(ServerHarness server, DateTimeOffset now) =>
        server.Services.GetRequiredService<Planner>().ExpireScheduledAsync(now, CancellationToken.None);

    [Fact]
    public async Task A_scheduled_run_no_agent_picked_up_stops_waiting_after_the_limit()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Scheduled());
        var (_, http) = await ConfiguredAsync(server);
        using var _ = http;
        await FireAsync(server, At(1));
        await FireAsync(server, At(6, 1));
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunReasons.Schedule, run.Reason);

        await ExpireAsync(server, At(6) + Planner.ScheduleLateLimit - TimeSpan.FromMinutes(1));
        Assert.NotEqual(RunStatuses.Cancelled, (await server.LatestRunAsync(Guest, Operations.Configure)).Status);

        await ExpireAsync(server, At(6) + Planner.ScheduleLateLimit + TimeSpan.FromMinutes(1));
        var stopped = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(run.Id, stopped.Id);
        Assert.Equal(RunStatuses.Cancelled, stopped.Status);
    }

    [Fact]
    public async Task A_catch_up_keeps_the_limit_of_the_time_it_was_for()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Scheduled());
        var (_, http) = await ConfiguredAsync(server);
        using var _ = http;
        await FireAsync(server, At(1));
        await FireAsync(server, At(6, 59));

        await ExpireAsync(server, At(7, 1));
        Assert.Equal(RunStatuses.Cancelled, (await server.LatestRunAsync(Guest, Operations.Configure)).Status);
    }

    [Fact]
    public async Task A_finished_preview_does_not_keep_a_run_that_changed_nothing_waiting()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Scheduled());
        var (agent, http) = await ConfiguredAsync(server);
        using var _ = http;
        await FireAsync(server, At(1));
        await FireAsync(server, At(6, 1));
        await ConfigureRunTests.PreviewAsync(agent);
        server.Services.GetRequiredService<AgentConnections>().Disconnect(agent.Id);
        await server.TickAsync();

        await ExpireAsync(server, At(8));
        Assert.Equal(RunStatuses.Cancelled, (await server.LatestRunAsync(Guest, Operations.Configure)).Status);
        await FireAsync(server, At(6, 1, day: 1));
        Assert.NotEqual(RunStatuses.Cancelled, (await server.LatestRunAsync(Guest, Operations.Configure)).Status);
    }

    [Fact]
    public async Task A_run_that_has_gone_out_to_change_the_guest_is_not_cancelled()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Scheduled());
        var (agent, http) = await ConfiguredAsync(server);
        using var _ = http;
        await FireAsync(server, At(1));
        await FireAsync(server, At(6, 1));
        await ConfigureRunTests.PreviewAsync(agent);
        await agent.NextAsync("run");

        await ExpireAsync(server, At(8));
        Assert.NotEqual(RunStatuses.Cancelled, (await server.LatestRunAsync(Guest, Operations.Configure)).Status);
    }

    /// <summary>One pass of the orchestrator's loop with its minute's checks due, in its order: the engine goes first.</summary>
    private static async Task PassAsync(ServerHarness server, TestClock clock, DateTimeOffset now)
    {
        clock.Now = now;
        await server.TickAsync();
        await FireAsync(server, now);
        await ExpireAsync(server, now);
    }

    private static Task<int> ChangesAsync(ServerHarness server, Guid run) =>
        server.QueryAsync(db => db.Attempts.CountAsync(a => a.RunId == run && a.Mutating && a.Status != AttemptStatuses.Cancelled));

    [Fact]
    public async Task A_change_queued_in_time_does_not_go_out_after_the_limit()
    {
        var clock = new TestClock();
        await using var server = await ServerHarness.StartAsync(postgres, Scheduled(), clock: clock);
        var (agent, http) = await ConfiguredAsync(server);
        using var _ = http;
        await PassAsync(server, clock, At(1));
        await PassAsync(server, clock, At(6, 1));
        await ConfigureRunTests.PreviewAsync(agent);
        server.Services.GetRequiredService<AgentConnections>().Disconnect(agent.Id);
        var late = await server.ConnectAgentAsync("home");

        // The periodic pass is not due yet: the loop reaches its engine first.
        clock.Now = At(7, 1);
        await server.TickAsync();

        Assert.Empty(late.TakeAssignments());
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.Cancelled, run.Status);
        Assert.Equal(0, await ChangesAsync(server, run.Id));
    }

    [Fact]
    public async Task A_result_after_the_limit_does_not_release_the_change()
    {
        var clock = new TestClock();
        await using var server = await ServerHarness.StartAsync(postgres, Scheduled(), clock: clock);
        var (agent, http) = await ConfiguredAsync(server);
        using var _ = http;
        await PassAsync(server, clock, At(1));
        await PassAsync(server, clock, At(6, 1));
        var preview = await agent.NextAsync("preview");

        // The callback runs on its own, between two periodic passes.
        clock.Now = At(7, 1);
        var (outputs, artifact) = ConfigureRunTests.Preview();
        await agent.ReportAsync(preview, Outcome.Succeeded, outputs, artifacts: artifact);

        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.Cancelled, run.Status);
        Assert.Contains("nothing reached the guest", run.Detail);
        Assert.Equal(0, await ChangesAsync(server, run.Id));
        await server.TickAsync();
        Assert.Empty(agent.TakeAssignments());
    }

    [Fact]
    public async Task A_change_still_goes_out_at_the_limit_itself()
    {
        var clock = new TestClock();
        await using var server = await ServerHarness.StartAsync(postgres, Scheduled(), clock: clock);
        var (agent, http) = await ConfiguredAsync(server);
        using var _ = http;
        await PassAsync(server, clock, At(1));
        await PassAsync(server, clock, At(6, 1));
        await ConfigureRunTests.PreviewAsync(agent);

        await PassAsync(server, clock, At(6) + Planner.ScheduleLateLimit);
        Assert.Contains(agent.TakeAssignments(), a => a.Action == "run");
    }

    private static readonly DateTimeOffset Limit = At(6) + Planner.ScheduleLateLimit;

    /// <summary>Lets the limit pass after this many reads of the clock, wherever that lands in the work.</summary>
    private static void CrossTheLimit(TestClock clock, int reads)
    {
        clock.Now = Limit - TimeSpan.FromMilliseconds(1);
        clock.MoveOnAfter(reads, Limit + TimeSpan.FromMilliseconds(1));
    }

    private static Task<Attempt?> ChangeAsync(ServerHarness server, Guid run) =>
        server.QueryAsync(db => db.Attempts.AsNoTracking().SingleOrDefaultAsync(a => a.RunId == run && a.Mutating));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task The_limit_passing_during_assignment_cannot_send_a_change_late(int reads)
    {
        var clock = new TestClock();
        await using var server = await ServerHarness.StartAsync(postgres, Scheduled(), clock: clock);
        var (agent, http) = await ConfiguredAsync(server);
        using var _ = http;
        await PassAsync(server, clock, At(1));
        await PassAsync(server, clock, At(6, 1));
        await ConfigureRunTests.PreviewAsync(agent);
        var run = await server.LatestRunAsync(Guest, Operations.Configure);

        CrossTheLimit(clock, reads);
        await server.Engine.DispatchAsync(CancellationToken.None);

        var change = await ChangeAsync(server, run.Id);
        if (agent.TakeAssignments().Any(a => a.Action == "run"))
            Assert.True(change!.AssignedAt <= Limit, $"Assigned at {change.AssignedAt:O}, after {Limit:O}.");
        else
            Assert.Equal(AttemptStatuses.Cancelled, change!.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public async Task The_limit_passing_during_a_result_cannot_create_a_change_late(int reads)
    {
        var clock = new TestClock();
        await using var server = await ServerHarness.StartAsync(postgres, Scheduled(), clock: clock);
        var (agent, http) = await ConfiguredAsync(server);
        using var _ = http;
        await PassAsync(server, clock, At(1));
        await PassAsync(server, clock, At(6, 1));
        var preview = await agent.NextAsync("preview");
        var run = await server.LatestRunAsync(Guest, Operations.Configure);

        CrossTheLimit(clock, reads);
        var (outputs, artifact) = ConfigureRunTests.Preview();
        await agent.ReportAsync(preview, Outcome.Succeeded, outputs, artifacts: artifact);

        if (await ChangeAsync(server, run.Id) is { } change)
            Assert.True(change.CreatedAt <= Limit, $"Created at {change.CreatedAt:O}, after {Limit:O}.");
    }

    [Fact]
    public async Task A_person_who_approves_after_the_limit_decides_and_the_limit_stops_applying()
    {
        var clock = new TestClock();
        await using var server = await ServerHarness.StartAsync(postgres, Scheduled("policy:\n  destructive: manual\n", action: "setup", destructive: "true"), clock: clock);
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        await PassAsync(server, clock, At(1));
        await PassAsync(server, clock, At(6, 1));
        await ConfigureRunTests.PreviewAsync(agent);
        await server.TickAsync();

        await PassAsync(server, clock, At(9));
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);

        server.Services.GetRequiredService<AgentConnections>().Disconnect(agent.Id);
        var approval = run.Steps.Single(s => s.Name == StepNames.Approval);
        await server.Engine.ApproveAsync(run.Id, approval.Id, approval.PlanSha256!, "ops", null, CancellationToken.None);
        await PassAsync(server, clock, At(9, 1));
        Assert.Equal(RunStatuses.Running, (await server.LatestRunAsync(Guest, Operations.Configure)).Status);

        var next = await server.ConnectAgentAsync("home");
        await next.NextAsync("run");
        await ExpireAsync(server, At(9, 2));
        Assert.Equal(RunStatuses.Running, (await server.LatestRunAsync(Guest, Operations.Configure)).Status);
    }

    [Theory]
    // 02:30 does not exist on this morning: the clocks go from 02:00 to 03:00.
    [InlineData("2026-03-29T00:00:00Z", "2026-03-29T01:00:00Z")]
    // 02:30 happens twice on this morning: the clocks go from 03:00 back to 02:00.
    [InlineData("2026-10-24T23:00:00Z", "2026-10-25T00:30:00Z")]
    public async Task A_time_the_clocks_move_through_fires_once(string seen, string due)
    {
        var test = Scheduled(cron: "30 2 * * *");
        test.Write("labs/home/lab.yaml", File.ReadAllText(Path.Combine(test.Root, "labs/home/lab.yaml")) + "timezone: Europe/Madrid\n");
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (_, http) = await ConfiguredAsync(server);
        using var _ = http;
        await FireAsync(server, DateTimeOffset.Parse(seen).ToUniversalTime());
        var count = await ConfigureRunsAsync(server);

        var fallsDue = DateTimeOffset.Parse(due).ToUniversalTime();
        await FireAsync(server, fallsDue - TimeSpan.FromMinutes(1));
        Assert.Equal(count, await ConfigureRunsAsync(server));
        await FireAsync(server, fallsDue);
        Assert.Equal(count + 1, await ConfigureRunsAsync(server));
        Assert.Equal(fallsDue, (await StateAsync(server)).LastSlot);

        // A second firing would be refused by the run in the way, and say so.
        await FireAsync(server, fallsDue + TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        Assert.Null((await StateAsync(server)).Note);
    }

    [Fact]
    public async Task A_time_zone_only_a_guest_with_no_schedule_uses_cannot_stop_the_pass()
    {
        var test = Repository.Repository();
        test.Write("labs/home/lab.yaml", File.ReadAllText(Path.Combine(test.Root, "labs/home/lab.yaml")) + "timezone: Mars/Olympus\n");
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (_, _, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        await FireAsync(server, At(6, 1));
    }
}
