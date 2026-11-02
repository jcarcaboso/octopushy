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

    private static TestRepository Scheduled(string policy = "", string outcomes = "", string action = "update-system", string destructive = "false")
    {
        var test = Repository.Repository(policy, outcomes, destructive);
        var guest = Path.Combine(test.Root, GuestFile);
        test.Write(GuestFile, File.ReadAllText(guest).Replace(
            "access:\n  user: ops",
            $"  schedules:\n    - {{ action: {action}, cron: \"0 6 * * *\" }}\naccess:\n  user: ops"));
        return test;
    }

    private const string Deferred = """
        outcomes:
          deferred:
            description: Work is running.
            approve: { vars: { allow_busy: true } }
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
            Assert.Equal("configure", configuration.AppliedAction);
            Assert.Contains("\"1\"", configuration.Snapshot);
        }

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
        await using var server = await ServerHarness.StartAsync(postgres, Scheduled("policy:\n  destructive: manual\n", action: "configure", destructive: "true"));
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
    public async Task A_scheduled_run_no_agent_picked_up_stops_waiting_after_the_limit()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Scheduled());
        var (_, http) = await ConfiguredAsync(server);
        using var _ = http;
        await FireAsync(server, At(1));
        await FireAsync(server, At(6, 1));
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunReasons.Schedule, run.Reason);

        var planner = server.Services.GetRequiredService<Planner>();
        await planner.ExpireScheduledAsync(DateTimeOffset.UtcNow + Planner.ScheduleLateLimit - TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.NotEqual(RunStatuses.Cancelled, (await server.LatestRunAsync(Guest, Operations.Configure)).Status);

        await planner.ExpireScheduledAsync(DateTimeOffset.UtcNow + Planner.ScheduleLateLimit + TimeSpan.FromMinutes(1), CancellationToken.None);
        var stopped = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(run.Id, stopped.Id);
        Assert.Equal(RunStatuses.Cancelled, stopped.Status);
    }
}
