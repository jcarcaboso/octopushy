using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Repository = Octopushy.Tests.Definitions.GuestConfigurationTests;

namespace Octopushy.Tests.Server;

/// <summary>A new active version of a store value a guest's setup names starts its onChange action once.</summary>
[Collection(PostgresCollection.Name)]
public class FollowedSecretTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string Shared = "app/git-key";

    private static Task ScheduleAsync(ServerHarness server) =>
        server.Services.GetRequiredService<Planner>().ScheduleConfigurationsAsync(CancellationToken.None);

    internal static async Task<int> StageAsync(HttpClient http, string key, string value)
    {
        var staged = await http.PostAsJsonAsync("/api/variables/candidates", new { key, value });
        staged.EnsureSuccessStatusCode();
        return (await staged.Content.ReadFromJsonAsync<JsonObject>())!["version"]!.GetValue<int>();
    }

    internal static async Task PromoteAsync(HttpClient http, string key, int version, int current) =>
        (await http.PostAsJsonAsync("/api/variables/promote", new { key, version, expectedCurrentVersion = current, verificationNote = "checked by hand" }))
            .EnsureSuccessStatusCode();

    /// <summary>A provisioned guest whose first configure run has succeeded.</summary>
    private static async Task<(FakeAgent Agent, HttpClient Http)> ConfiguredAsync(ServerHarness server)
    {
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        await ScheduleAsync(server);
        await FinishAsync(server, agent);
        return (agent, http);
    }

    private static async Task<Assignment> FinishAsync(ServerHarness server, FakeAgent agent)
    {
        // Prepared values make a snapshot, which update-system reuses.
        await ConfigureRunTests.PreviewAsync(agent, new JsonObject { ["version"] = "1.2.3" });
        var playbook = await agent.NextAsync("run");
        await agent.ReportAsync(playbook, Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        return playbook;
    }

    private static Task<int> ConfigureRunCountAsync(ServerHarness server) =>
        server.QueryAsync(db => db.Runs.CountAsync(r => r.Operation == Operations.Configure));

    [Fact]
    public async Task A_promoted_version_starts_the_onChange_action_once_and_says_which_value_changed()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (agent, http) = await ConfiguredAsync(server);
        using var _ = http;
        var first = await server.LatestRunAsync(Guest, Operations.Configure);

        // Staging a candidate changes nothing that runs use.
        var version = await StageAsync(http, Shared, "rotated-git-key");
        await ScheduleAsync(server);
        Assert.Equal(first.Id, (await server.LatestRunAsync(Guest, Operations.Configure)).Id);

        await PromoteAsync(http, Shared, version, 1);
        var resource = await server.ResourceAsync(Guest);
        var page = (await http.GetFromJsonAsync<JsonObject>($"/api/guests/{resource.Id}"))!;
        var followed = page["configuration"]!["secrets"]!.AsArray().Select(s => s!.AsObject()).ToList();
        Assert.Equal(new[] { "app/git-key", "app/tailscale" }, followed.Select(s => s["key"]!.GetValue<string>()));
        var git = followed.Single(s => s["key"]!.GetValue<string>() == Shared);
        Assert.Equal(2, git["activeVersion"]!.GetValue<int>());
        Assert.Equal(1, git["startedVersion"]!.GetValue<int>());
        Assert.True(git["pending"]!.GetValue<bool>());
        Assert.False(followed.Single(s => s["key"]!.GetValue<string>() == "app/tailscale")["pending"]!.GetValue<bool>());

        await ScheduleAsync(server);
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.NotEqual(first.Id, run.Id);
        Assert.Equal(RunReasons.Secret, run.Reason);
        Assert.Equal("A store value this guest's setup uses changed: app/git-key v1 to v2.", run.ReasonDetail);
        Assert.Equal("setup", RunContext.Parse(run.Context).Ansible!.Action);
        var summary = (await http.GetFromJsonAsync<JsonObject>($"/api/runs/{run.Id}"))!["run"]!;
        Assert.Equal("secret", summary["reason"]!.GetValue<string>());
        Assert.Contains("app/git-key v1 to v2", summary["reasonDetail"]!.GetValue<string>());

        var playbook = await FinishAsync(server, agent);
        Assert.Equal("rotated-git-key", playbook.Secrets[SecretNames.AnsibleFile("git_key_src")]);
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Guest, Operations.Configure)).Status);

        // Once per version.
        await ScheduleAsync(server);
        Assert.Equal(run.Id, (await server.LatestRunAsync(Guest, Operations.Configure)).Id);
        page = (await http.GetFromJsonAsync<JsonObject>($"/api/guests/{resource.Id}"))!;
        Assert.All(page["configuration"]!["secrets"]!.AsArray(), s => Assert.False(s!["pending"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task A_failed_run_is_not_repeated_for_the_same_version()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (agent, http) = await ConfiguredAsync(server);
        using var _ = http;
        await PromoteAsync(http, Shared, await StageAsync(http, Shared, "rotated"), 1);
        await ScheduleAsync(server);
        await ConfigureRunTests.PreviewAsync(agent);
        await agent.ReportAsync(await agent.NextAsync("run"), Outcome.Failed, ConfigureRunTests.Recap(), "the task \"Install tools\" failed");
        await server.TickAsync();
        var failed = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.Failed, failed.Status);

        await ScheduleAsync(server);
        Assert.Equal(failed.Id, (await server.LatestRunAsync(Guest, Operations.Configure)).Id);
    }

    [Fact]
    public async Task A_rollback_to_an_older_version_is_a_change_and_a_revoked_one_is_not()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (agent, http) = await ConfiguredAsync(server);
        using var _ = http;
        await PromoteAsync(http, Shared, await StageAsync(http, Shared, "rotated"), 1);
        await ScheduleAsync(server);
        await FinishAsync(server, agent);
        var count = await ConfigureRunCountAsync(server);

        // Revoking the active version leaves nothing to deliver: the run would only block.
        (await http.PostAsJsonAsync("/api/variables/revoke", new { key = Shared, version = 2, reason = "leaked" })).EnsureSuccessStatusCode();
        await ScheduleAsync(server);
        Assert.Equal(count, await ConfigureRunCountAsync(server));

        // Going back to version 1 is a change like any other, and it reaches the guest.
        await PromoteAsync(http, Shared, 1, 2);
        await ScheduleAsync(server);
        var rollback = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(count + 1, await ConfigureRunCountAsync(server));
        Assert.Equal(RunReasons.Secret, rollback.Reason);
        Assert.Contains("app/git-key v2 to v1", rollback.ReasonDetail);
        var playbook = await FinishAsync(server, agent);
        Assert.Equal("git-key-value", playbook.Secrets[SecretNames.AnsibleFile("git_key_src")]);
    }

    [Fact]
    public async Task A_change_waits_behind_an_active_run_and_is_not_owed_if_that_run_took_it()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        var first = await server.LatestRunAsync(Guest, Operations.Configure);

        // Promoted after the run was made and before its first step went out: the run pins it.
        await PromoteAsync(http, Shared, await StageAsync(http, Shared, "early"), 1);
        await ScheduleAsync(server);
        Assert.Equal(first.Id, (await server.LatestRunAsync(Guest, Operations.Configure)).Id);
        var playbook = await FinishAsync(server, agent);
        Assert.Equal("early", playbook.Secrets[SecretNames.AnsibleFile("git_key_src")]);

        await ScheduleAsync(server);
        Assert.Equal(first.Id, (await server.LatestRunAsync(Guest, Operations.Configure)).Id);
    }

    [Fact]
    public async Task A_change_after_the_run_pinned_waits_for_it_then_runs_once()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        await ScheduleAsync(server);
        var first = await server.LatestRunAsync(Guest, Operations.Configure);
        await ConfigureRunTests.PreviewAsync(agent);
        var playbook = await agent.NextAsync("run");

        await PromoteAsync(http, Shared, await StageAsync(http, Shared, "late"), 1);
        await ScheduleAsync(server);
        Assert.Equal(first.Id, (await server.LatestRunAsync(Guest, Operations.Configure)).Id);

        await agent.ReportAsync(playbook, Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        await ScheduleAsync(server);
        var next = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.NotEqual(first.Id, next.Id);
        Assert.Equal(RunReasons.Secret, next.Reason);
        Assert.Equal("late", (await FinishAsync(server, agent)).Secrets[SecretNames.AnsibleFile("git_key_src")]);
    }

    [Fact]
    public async Task An_uncertain_run_holds_the_change_until_a_person_closes_it()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (agent, http) = await ConfiguredAsync(server);
        using var _ = http;
        var resource = await server.ResourceAsync(Guest);
        (await http.PostAsJsonAsync($"/api/guests/{resource.Id}/configure", new { action = "update-system" })).EnsureSuccessStatusCode();
        await server.TickAsync();
        await ConfigureRunTests.PreviewAsync(agent);
        var playbook = await agent.NextAsync("run");
        await server.Engine.OnStartedAsync(agent.Id, Guid.Parse(playbook.AttemptId), CancellationToken.None);
        await server.WithDbAsync(db => db.Attempts.Where(a => a.Id == Guid.Parse(playbook.AttemptId))
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        await server.TickAsync();
        await server.TickAsync();
        var uncertain = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunStatuses.Uncertain, uncertain.Status);

        await PromoteAsync(http, Shared, await StageAsync(http, Shared, "rotated"), 1);
        await ScheduleAsync(server);
        Assert.Equal(uncertain.Id, (await server.LatestRunAsync(Guest, Operations.Configure)).Id);

        (await http.PostAsync($"/api/runs/{uncertain.Id}/close", null)).EnsureSuccessStatusCode();
        await ScheduleAsync(server);
        var next = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunReasons.Secret, next.Reason);
        Assert.Equal("setup", RunContext.Parse(next.Context).Ansible!.Action);
    }

    [Fact]
    public async Task Only_a_run_of_the_onChange_action_counts_as_delivering_a_version()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (agent, http) = await ConfiguredAsync(server);
        using var _ = http;
        var resource = await server.ResourceAsync(Guest);

        // Another action takes the new version too, but the onChange action is still owed it.
        await PromoteAsync(http, Shared, await StageAsync(http, Shared, "v2"), 1);
        (await http.PostAsJsonAsync($"/api/guests/{resource.Id}/configure", new { action = "update-system" })).EnsureSuccessStatusCode();
        await FinishAsync(server, agent);
        await ScheduleAsync(server);
        var owed = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunReasons.Secret, owed.Reason);
        await FinishAsync(server, agent);

        // A person running the onChange action delivers what is active, so nothing is owed after it.
        await PromoteAsync(http, Shared, await StageAsync(http, Shared, "v3"), 2);
        (await http.PostAsJsonAsync($"/api/guests/{resource.Id}/configure", new { action = "setup" })).EnsureSuccessStatusCode();
        var manual = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal("manual", manual.Reason);
        await FinishAsync(server, agent);
        await ScheduleAsync(server);
        Assert.Equal(manual.Id, (await server.LatestRunAsync(Guest, Operations.Configure)).Id);
    }

    [Fact]
    public async Task The_access_key_and_a_version_pinned_in_Git_are_not_followed()
    {
        var test = Repository.Repository();
        const string guestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
        test.Write(guestFile, File.ReadAllText(Path.Combine(test.Root, guestFile)).Replace("{ store: app/tailscale }", "{ store: app/tailscale, version: 1 }"));
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (_, http) = await ConfiguredAsync(server);
        using var _ = http;
        var count = await ConfigureRunCountAsync(server);

        await PromoteAsync(http, "app/tailscale", await StageAsync(http, "app/tailscale", "tskey-2"), 1);
        await PromoteAsync(http, "access/app/ssh-key", await StageAsync(http, "access/app/ssh-key", "another key"), 1);
        await ScheduleAsync(server);
        Assert.Equal(count, await ConfigureRunCountAsync(server));
        var resource = await server.ResourceAsync(Guest);
        var page = (await http.GetFromJsonAsync<JsonObject>($"/api/guests/{resource.Id}"))!;
        Assert.Equal(new[] { Shared }, page["configuration"]!["secrets"]!.AsArray().Select(s => s!["key"]!.GetValue<string>()));
    }

    [Fact]
    public async Task An_upgrade_records_what_is_active_and_starts_nothing()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, http) = await ConfiguredAsync(server);
        using var _ = http;
        var count = await ConfigureRunCountAsync(server);
        var resource = await server.ResourceAsync(Guest);

        // As a guest configured before this was built: nothing recorded, and a version promoted since.
        await server.WithDbAsync(db => db.GuestConfigurations.Where(c => c.ResourceId == resource.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.SecretVersions, (string?)null)));
        await PromoteAsync(http, Shared, await StageAsync(http, Shared, "rotated"), 1);
        await ScheduleAsync(server);
        Assert.Equal(count, await ConfigureRunCountAsync(server));
        var recorded = await server.QueryAsync(db => db.GuestConfigurations.AsNoTracking().Where(c => c.ResourceId == resource.Id)
            .Select(c => c.SecretVersions).SingleAsync());
        Assert.Equal(2, FollowedSecrets.Parse(recorded)[Shared]);
    }

    [Fact]
    public void Changes_ignore_a_key_with_nothing_to_deliver_and_keep_its_record()
    {
        var recorded = new Dictionary<string, int?> { ["a"] = 2, ["b"] = null, ["c"] = 1 };
        var usable = new Dictionary<string, int?> { ["a"] = null, ["b"] = 1, ["c"] = 1 };
        var change = Assert.Single(FollowedSecrets.Changes(recorded, usable));
        Assert.Equal(new FollowedSecrets.Change("b", null, 1), change);
        Assert.Equal("A store value this guest's setup uses changed: b set, v1.", FollowedSecrets.Describe([change]));
        Assert.Equal(2, FollowedSecrets.Started(recorded, usable)["a"]);
    }

    [Fact]
    public async Task A_rollback_while_a_run_holds_a_newer_version_still_runs()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        await ScheduleAsync(server); // record v1
        await PromoteAsync(http, Shared, await StageAsync(http, Shared, "new-v2"), 1);
        await ConfigureRunTests.PreviewAsync(agent);
        var playbook = await agent.NextAsync("run"); // first dispatch pinned v2
        Assert.Equal("new-v2", playbook.Secrets[SecretNames.AnsibleFile("git_key_src")]);
        var before = await ConfigureRunCountAsync(server);
        await PromoteAsync(http, Shared, 1, 2); // rollback while the v2 playbook is active
        await ScheduleAsync(server); // held behind the active run
        Assert.Equal(before, await ConfigureRunCountAsync(server));
        await agent.ReportAsync(playbook, Outcome.Succeeded, ConfigureRunTests.Recap());
        await server.TickAsync();
        await ScheduleAsync(server);
        Assert.Equal(before + 1, await ConfigureRunCountAsync(server));
    }

    [Fact]
    public async Task A_binding_pinned_in_Git_does_not_stand_in_for_one_that_follows()
    {
        var test = Repository.Repository();
        const string file = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
        // Same store key used by two roles, one deliberately pinned to v2.
        test.Write(file, File.ReadAllText(Path.Combine(test.Root, file))
            .Replace("tailscale_key: { store: app/tailscale }", "tailscale_key: { store: app/git-key, version: 2 }"));
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (_, agent, http) = await ConfigureRunTests.ProvisionedAsync(server);
        using var _ = http;
        await PromoteAsync(http, Shared, await StageAsync(http, Shared, "new-v2"), 1);
        await PromoteAsync(http, Shared, 1, 2); // v2 has been activated, but v1 is current
        await ScheduleAsync(server);
        var playbook = await FinishAsync(server, agent);
        Assert.Equal("new-v2", playbook.Secrets[SecretNames.AnsibleVar("tailscale_key")]);
        Assert.NotEqual("new-v2", playbook.Secrets[SecretNames.AnsibleFile("git_key_src")]);
        var before = await ConfigureRunCountAsync(server);
        await PromoteAsync(http, Shared, 2, 1);
        await ScheduleAsync(server);
        Assert.Equal(before + 1, await ConfigureRunCountAsync(server));
    }

    [Fact]
    public async Task A_restart_neither_loses_nor_repeats_a_change()
    {
        await using var server = await ServerHarness.StartAsync(postgres, Repository.Repository());
        var (_, http) = await ConfiguredAsync(server);
        using var _ = http;
        var before = await ConfigureRunCountAsync(server);
        await PromoteAsync(http, Shared, await StageAsync(http, Shared, "restart-v2"), 1);
        await server.RestartFixtureAsync();
        await ScheduleAsync(server);
        Assert.Equal(before + 1, await ConfigureRunCountAsync(server));
        var run = await server.LatestRunAsync(Guest, Operations.Configure);
        await server.RestartFixtureAsync();
        await ScheduleAsync(server);
        Assert.Equal(run.Id, (await server.LatestRunAsync(Guest, Operations.Configure)).Id);
        var agent = await server.ConnectAgentAsync("home");
        Assert.Equal("restart-v2", (await FinishAsync(server, agent)).Secrets[SecretNames.AnsibleFile("git_key_src")]);
        await server.RestartFixtureAsync();
        await ScheduleAsync(server);
        Assert.Equal(before + 1, await ConfigureRunCountAsync(server));
    }

    [Fact]
    public async Task A_scheduled_run_delivers_the_new_version_and_a_change_does_not_overlap_it()
    {
        var test = Repository.Repository();
        const string file = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
        test.Write(file, File.ReadAllText(Path.Combine(test.Root, file)).Replace("access:\n  user: ops",
            "schedules:\n    - { action: setup, cron: \"0 6 * * *\" }\naccess:\n  user: ops"));
        await using var server = await ServerHarness.StartAsync(postgres, test);
        var (agent, http) = await ConfiguredAsync(server);
        using var _ = http;
        var planner = server.Services.GetRequiredService<Planner>();
        var today = new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero).AddDays(2);
        await planner.FireSchedulesAsync(today.AddHours(5), CancellationToken.None);
        await PromoteAsync(http, Shared, await StageAsync(http, Shared, "scheduled-v2"), 1);
        await planner.FireSchedulesAsync(today.AddHours(6), CancellationToken.None);
        var scheduled = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunReasons.Schedule, scheduled.Reason);
        await ScheduleAsync(server);
        Assert.Equal(scheduled.Id, (await server.LatestRunAsync(Guest, Operations.Configure)).Id);
        Assert.Equal("scheduled-v2", (await FinishAsync(server, agent)).Secrets[SecretNames.AnsibleFile("git_key_src")]);
        await ScheduleAsync(server);
        Assert.Equal(scheduled.Id, (await server.LatestRunAsync(Guest, Operations.Configure)).Id);
        await PromoteAsync(http, Shared, await StageAsync(http, Shared, "scheduled-v3"), 2);
        await ScheduleAsync(server);
        var secret = await server.LatestRunAsync(Guest, Operations.Configure);
        Assert.Equal(RunReasons.Secret, secret.Reason);
        await planner.FireSchedulesAsync(today.AddDays(1).AddHours(6), CancellationToken.None);
        Assert.Equal(secret.Id, (await server.LatestRunAsync(Guest, Operations.Configure)).Id);
    }
}
