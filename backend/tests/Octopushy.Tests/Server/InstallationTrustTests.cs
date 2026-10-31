using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Api;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;
using Octopushy.Server.Git;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class InstallationTrustTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string InstallationFile = "labs/home/proxmox/korriban/installation.yaml";

    [Fact]
    public async Task Sync_persists_a_non_utc_commit_and_a_new_installation_waits_for_trust()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        using var client = await AuthenticateAsync(server);
        await server.Services.GetRequiredService<GitCli>().RunAsync(
            ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                "commit", "--quiet", "--allow-empty", "-m", "Merge with local timezone"],
            server.Repository.Root,
            environment: new Dictionary<string, string> { ["GIT_COMMITTER_DATE"] = "2026-10-25T19:20:01+01:00" });

        await server.SyncAsync();

        var repository = (await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!;
        var revision = Assert.IsType<RevisionDto>(repository.Revision);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 18, 20, 1, TimeSpan.Zero), revision.CommittedAt);
        Assert.True(revision.IsCurrentSource);
        var run = await server.LatestRunAsync(Guest);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("Waiting for trust", run.Detail);
        Assert.Contains("new Proxmox installation", run.Detail);
        Assert.Equal(0, await server.QueryAsync(db => db.SecretResolutions.CountAsync()));
    }

    [Fact]
    public async Task A_new_installation_waits_for_trust_without_releasing_credentials()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("Waiting for trust", run.Detail);
        Assert.Empty(agent.TakeAssignments());
        Assert.Contains("Waiting for trust", await server.QueryAsync(db => db.Attempts.Where(a => a.StepId == null).Select(a => a.WaitingReason).SingleAsync()));
        Assert.Equal(0, await server.QueryAsync(db => db.SecretResolutions.CountAsync()));

        using var client = await AuthenticateAsync(server);
        var installation = await InstallationAsync(client);
        Assert.Equal(["https://10.1.0.10:8006/"], installation.Trust.Endpoints);
        Assert.Equal(installation.Trust.Endpoints, installation.Trust.Untrusted);
        Assert.Null(installation.Trust.TrustedAt);
        var response = await TrustAsync(client, installation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var trusted = (await response.Content.ReadFromJsonAsync<InstallationTrustDto>())!;
        Assert.Equal("ops", trusted.TrustedBy);
        Assert.NotNull(trusted.TrustedAt);
        Assert.Empty(trusted.Untrusted);
        await agent.NextAsync("preview");
        Assert.True(await server.QueryAsync(db => db.SecretResolutions.AnyAsync()));
        Assert.Empty((await InstallationAsync(client)).Trust.Untrusted);
    }

    [Fact]
    public async Task New_revisions_of_a_trusted_installation_run_without_another_trust()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAndTrustAsync();
        var agent = await server.ConnectAgentAsync("home");
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());
        var applied = (await server.LatestRunAsync(Guest)).Commit;

        var guestFile = Path.Combine(server.Repository.Root, "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml");
        File.WriteAllText(guestFile, File.ReadAllText(guestFile).Replace("memory: 2048", "memory: 4096"));
        server.Commit("More memory");
        await server.SyncAsync();

        var plan = await agent.NextAsync("preview");
        var latest = await server.LatestRunAsync(Guest);
        Assert.Equal(latest.Id, await server.QueryAsync(db => db.Attempts.Where(a => a.Id == Guid.Parse(plan.AttemptId)).Select(a => a.RunId).SingleAsync()));
        Assert.NotEqual(applied, latest.Commit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_new_endpoint_waits_for_trust_again(bool hostEndpoint)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        var agent = await server.ConnectAgentAsync("home");
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded, new JsonObject { ["outputs"] = new JsonObject() });
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded, new JsonObject());

        var installationFile = Path.Combine(server.Repository.Root, InstallationFile);
        var definition = File.ReadAllText(installationFile);
        File.WriteAllText(installationFile, hostEndpoint
            ? definition.Replace("    datastores:", "    endpoint: https://10.1.0.99:8006/\n    datastores:")
            : definition.Replace("https://10.1.0.10:8006/", "https://10.1.0.99:8006/"));
        var guestFile = Path.Combine(server.Repository.Root, "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml");
        File.WriteAllText(guestFile, File.ReadAllText(guestFile).Replace("memory: 2048", "memory: 4096"));
        server.Commit("Move the API");
        await server.SyncAsync();
        await server.TickAsync();

        var releases = await server.QueryAsync(db => db.SecretResolutions.CountAsync());
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action is "preview" or "observe");
        var run = await server.LatestRunAsync(Guest);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("https://10.1.0.99:8006/ is a new endpoint", run.Detail);
        var installation = await InstallationAsync(client);
        Assert.Equal(["https://10.1.0.99:8006/"], installation.Trust.Untrusted);
        Assert.Equal(releases, await server.QueryAsync(db => db.SecretResolutions.CountAsync()));

        (await TrustAsync(client, installation)).EnsureSuccessStatusCode();
        Assert.Contains("10.1.0.99", (await agent.NextAsync("preview")).InputJson);
    }

    [Fact]
    public async Task Trust_requires_authentication_a_valid_definition_and_the_endpoints_shown()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAsync();
        using var anonymous = server.CreateClient();
        using var client = await AuthenticateAsync(server);
        var installation = await InstallationAsync(client);
        var path = $"/api/installations/{installation.Id}/trust";
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync(path, new TrustInstallationRequest(installation.Trust.Endpoints))).StatusCode);
        var stale = await client.PostAsJsonAsync(path, new TrustInstallationRequest(["https://10.9.9.9:8006/"]));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("endpoints changed", await stale.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, new TrustInstallationRequest([]))).StatusCode);
        Assert.Empty(await server.QueryAsync(db => db.InstallationTrusts.ToListAsync()));

        server.Repository.Write(InstallationFile, "not: a valid installation\n");
        server.Commit("Break the installation");
        await server.SyncAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await TrustAsync(client, installation)).StatusCode);
        Assert.Empty(await server.QueryAsync(db => db.InstallationTrusts.ToListAsync()));
    }

    [Fact]
    public async Task Source_changes_need_no_new_trust_but_previous_source_work_is_not_sent()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        var first = (await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!;
        var stale = await server.LatestRunAsync(Guest);
        await UpdateSourceAsync(client, server.Repository.Root + "/.");
        Assert.Null((await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision);
        var detail = (await client.GetFromJsonAsync<RevisionDetailDto>($"/api/repository/revisions/{first.Id}"))!;
        Assert.False(detail.Revision.IsCurrentSource);
        var agent = await server.ConnectAgentAsync("home");
        await server.TickAsync();
        Assert.Empty(agent.TakeAssignments());
        Assert.Contains("current repository source", (await server.QueryAsync(db => db.Runs.SingleAsync(r => r.Id == stale.Id))).Detail);
        Assert.Equal(0, await server.QueryAsync(db => db.SecretResolutions.CountAsync()));

        await server.SyncAsync();
        var second = (await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!;
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Commit, second.Commit);
        Assert.True(second.IsCurrentSource);
        await agent.NextAsync("preview");
    }

    [Fact]
    public async Task A_metadata_only_revision_preserves_the_existing_plan_approval_and_artifact()
    {
        using var repository = TestRepository.Minimal();
        repository.Write("labs/home/lab.yaml", "schemaVersion: 1\nexecution: { agentLabels: [home] }\npolicy: { approval: manual }\n");
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        using var client = await AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        var agent = await server.ConnectAgentAsync("home");
        var artifact = FakeAgent.PlanArtifact("original saved plan");
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: artifact);
        var original = await server.LatestRunAsync(Guest);
        repository.Write("README.md", "Metadata only; no infrastructure input changes.\n");
        server.Commit("Explain the repository");
        await server.SyncAsync();
        var retained = await server.LatestRunAsync(Guest);
        Assert.Equal(original.Id, retained.Id);
        Assert.Equal(RunStatuses.AwaitingApproval, retained.Status);
        Assert.Equal(1, await server.QueryAsync(db => db.Runs.CountAsync(r => r.ResourceId == retained.ResourceId)));
        var approval = retained.Steps.Single(s => s.Name == StepNames.Approval);
        Assert.Equal(artifact.Sha256, approval.PlanSha256);
        await server.Engine.ApproveAsync(retained.Id, approval.Id, artifact.Sha256, "ops", null, CancellationToken.None);
        Assert.Equal(artifact.Content, Assert.Single((await agent.NextAsync("apply")).Artifacts).Content);
    }

    [Fact]
    public async Task Scheduled_observation_uses_the_latest_valid_snapshot_when_the_head_is_invalid()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        var valid = (await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!;
        var agent = await server.ConnectAgentAsync("home");
        await agent.ReportAsync(await agent.NextAsync("observe"), Outcome.Succeeded, new JsonObject());
        await MakeObservationsDueAsync(server);
        server.Repository.Write("octopushy.yaml", "schemaVersion: 999\n");
        server.Commit("Broken repository");
        await server.SyncAsync();
        await server.Services.GetRequiredService<Planner>().ScheduleObservesAsync(CancellationToken.None);
        var observe = await agent.NextAsync("observe");
        var revisionId = await server.QueryAsync(db => db.Attempts.Where(a => a.Id == Guid.Parse(observe.AttemptId)).Select(a => a.RevisionId).SingleAsync());
        Assert.Equal(valid.Id, revisionId);
        Assert.Contains("10.1.0.10", observe.InputJson);
        await agent.ReportAsync(observe, Outcome.Succeeded, new JsonObject());
        await UpdateSourceAsync(client, null);
        await MakeObservationsDueAsync(server);
        var count = await server.QueryAsync(db => db.Attempts.CountAsync());
        await server.Services.GetRequiredService<Planner>().ScheduleObservesAsync(CancellationToken.None);
        Assert.Equal(count, await server.QueryAsync(db => db.Attempts.CountAsync()));
    }

    private static Task MakeObservationsDueAsync(ServerHarness server) =>
        server.WithDbAsync(db => db.Resources.Where(r => r.Kind == ResourceKinds.Installation)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.ObserveRequestedAt, DateTimeOffset.UtcNow.AddDays(-1))));

    [Fact]
    public async Task A_source_change_does_not_discard_a_completed_observation()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        var agent = await server.ConnectAgentAsync("home");
        var assignment = await agent.NextAsync("observe");
        await UpdateSourceAsync(client, server.Repository.Root + "/.");
        var releases = await server.QueryAsync(db => db.SecretResolutions.CountAsync());
        await agent.ReportAsync(assignment, Outcome.Succeeded, new JsonObject(), "known fixture outcome");
        await server.TickAsync();
        var installation = await server.ResourceAsync("home/korriban");
        Assert.Null(installation.ObserveError);
        Assert.Equal(1, await server.QueryAsync(db => db.Observations.CountAsync(o => o.ResourceId == installation.Id)));
        Assert.Equal(releases, await server.QueryAsync(db => db.SecretResolutions.CountAsync()));
    }

    [Theory]
    [InlineData("apply", Outcome.Failed, RunStatuses.Failed)]
    public async Task A_source_change_does_not_mask_a_known_failure(string action, Outcome outcome, string expected)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        var agent = await server.ConnectAgentAsync("home");
        if (action == "apply")
            await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        var assignment = await agent.NextAsync(action);
        var runId = await server.QueryAsync(db => db.Attempts.Where(a => a.Id == Guid.Parse(assignment.AttemptId)).Select(a => a.RunId).SingleAsync());
        await UpdateSourceAsync(client, server.Repository.Root + "/.");
        var releases = await server.QueryAsync(db => db.SecretResolutions.CountAsync());
        await agent.ReportAsync(assignment, outcome, new JsonObject(), "known fixture outcome");
        await server.TickAsync();
        var detail = (await client.GetFromJsonAsync<RunDetailDto>($"/api/runs/{runId}"))!;
        Assert.Equal(expected, detail.Run.Status);
        Assert.Equal(releases, await server.QueryAsync(db => db.SecretResolutions.CountAsync()));
    }

    [Fact]
    public async Task A_filesystem_retry_rechecks_the_source_before_reopening_a_failed_step()
    {
        using var repository = Definitions.GuestFilesystemTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        using var client = await AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        var agent = await server.ConnectAgentAsync("home");
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(update: 1), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("apply"), Outcome.Succeeded);
        await agent.ReportAsync(await agent.NextAsync("verify-guest"), Outcome.Succeeded);
        await agent.ReportAsync(await agent.NextAsync("grow"), Outcome.Failed, error: "known guest failure");
        var failed = await server.LatestRunAsync(Guest);
        await UpdateSourceAsync(client, repository.Root + "/.");
        var response = await client.PostAsync($"/api/runs/{failed.Id}/retry-filesystem", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("current repository source", await response.Content.ReadAsStringAsync());
        Assert.Equal(RunStatuses.Failed, (await server.LatestRunAsync(Guest)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_real_assignment_builder_itself_refuses_untrusted_work_before_secret_resolution(bool untrustedInstallation)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest);
        if (untrustedInstallation)
            await server.WithDbAsync(db => db.InstallationTrusts.ExecuteDeleteAsync());
        else
            run.RevisionId = null;
        var step = run.Steps.Single(s => s.Name == StepNames.Plan);
        await using var scope = server.Services.CreateAsyncScope();
        await Assert.ThrowsAsync<ExecutionUntrustedException>(() => scope.ServiceProvider.GetRequiredService<AssignmentBuilder>()
            .BuildAsync(Assert.Single(step.Attempts), step, run, RunContext.Parse(run.Context), Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(0, await server.QueryAsync(db => db.SecretResolutions.CountAsync()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Persisted_trust_survives_a_restart_and_a_restart_never_grants_missing_trust(bool trustBeforeRestart)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using (var client = await AuthenticateAsync(server))
        {
            if (trustBeforeRestart) await server.SyncAndTrustAsync(client);
            else await server.SyncAsync();
            await server.TickAsync();
        }
        var before = await server.LatestRunAsync(Guest);
        await server.RestartFixtureAsync();
        using var afterClient = server.CreateClient();
        (await afterClient.PostAsJsonAsync("/api/auth/login", new { username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        Assert.Equal(trustBeforeRestart, (await InstallationAsync(afterClient)).Trust.TrustedAt is not null);
        var agent = await server.ConnectAgentAsync("home");
        await server.TickAsync();
        if (trustBeforeRestart)
        {
            Assert.Contains(agent.TakeAssignments(), a => a.Action == "preview");
        }
        else
        {
            Assert.Empty(agent.TakeAssignments());
            Assert.Equal(RunStatuses.Blocked, (await server.LatestRunAsync(Guest)).Status);
            Assert.Empty(RunContext.Parse(before.Context).SecretVersions);
            Assert.Equal(0, await server.QueryAsync(db => db.SecretResolutions.CountAsync()));
        }
    }

    [Fact]
    public async Task A_source_update_during_out_of_gate_fetch_and_parse_discards_the_stale_sync_result()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        // Queue both operations behind a fixture-held gate: capture first, source edit
        // second. The native fetch/parse occurs outside that gate, so the queued edit
        // wins before sync can re-enter for persistence, without sleeps or Git hooks.
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var barrier = server.Engine.ExecuteAsync(async (_, _) =>
        {
            held.SetResult();
            await release.Task;
            return true;
        }, CancellationToken.None);
        await held.Task;
        var sync = server.SyncAsync();
        var edit = server.Engine.ExecuteAsync(async (db, _) =>
        {
            var settings = await db.RepositorySettings.SingleAsync();
            settings.SourceId = Guid.NewGuid();
            settings.Url = server.Repository.Root + "/.";
            settings.HeadCommit = null;
            await db.SaveChangesAsync();
            return true;
        }, CancellationToken.None);
        release.SetResult();
        await Task.WhenAll(barrier, sync, edit);
        Assert.Equal(0, await server.QueryAsync(db => db.Revisions.CountAsync()));
        Assert.Equal(0, await server.QueryAsync(db => db.Runs.CountAsync()));
        Assert.Null(await server.QueryAsync(db => db.RepositorySettings.Select(s => s.HeadCommit).SingleAsync()));
        await server.SyncAsync();
        Assert.Equal(1, await server.QueryAsync(db => db.Revisions.CountAsync()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_change_never_masks_a_mutating_uncertain_outcome_or_allows_cancel_to_release_serialization(bool expiredLease)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        var agent = await server.ConnectAgentAsync("home");
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        var apply = await agent.NextAsync("apply");
        var original = await server.LatestRunAsync(Guest);
        await UpdateSourceAsync(client, server.Repository.Root + "/.");
        await server.SyncAsync();
        if (expiredLease)
        {
            await server.WithDbAsync(db => db.Attempts.Where(a => a.Id == Guid.Parse(apply.AttemptId))
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
            await server.TickAsync();
        }
        else
        {
            await agent.ReportAsync(apply, Outcome.Uncertain, error: "The fixture cannot determine the mutation outcome.");
        }
        var detail = (await client.GetFromJsonAsync<RunDetailDto>($"/api/runs/{original.Id}"))!;
        Assert.Equal(RunStatuses.Uncertain, detail.Run.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/runs/{original.Id}/cancel", null)).StatusCode);
        var releases = await server.QueryAsync(db => db.SecretResolutions.CountAsync());
        await server.TickAsync();
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action is "preview" or "apply" or "verify-guest");
        Assert.Equal(releases, await server.QueryAsync(db => db.SecretResolutions.CountAsync()));
        (await client.PostAsync($"/api/runs/{original.Id}/close", null)).EnsureSuccessStatusCode();
        await agent.NextAsync("preview"); // Only an explicit uncertain-outcome closure permits the new-source plan.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_or_mismatched_run_provenance_fails_closed_even_for_a_trusted_installation(bool missing)
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        await server.TickAsync(); // Queue real attempts before making this a legacy/corrupted fixture.
        await server.WithDbAsync(async db =>
        {
            foreach (var run in await db.Runs.ToListAsync())
            {
                if (missing) run.RevisionId = null;
                else run.Commit = new string('f', 40);
            }
            foreach (var observation in await db.Attempts.Where(a => a.StepId == null).ToListAsync())
            {
                if (missing) observation.RevisionId = null;
                else
                {
                    var context = JsonNode.Parse(observation.Context!)!;
                    context["commit"] = new string('f', 40);
                    observation.Context = context.ToJsonString();
                }
            }
            await db.SaveChangesAsync();
        });
        var agent = await server.ConnectAgentAsync("home");
        await server.Engine.DispatchAsync(CancellationToken.None); // No advancement pass to help the final hand-off guard.
        Assert.Empty(agent.TakeAssignments());
        Assert.Equal(0, await server.QueryAsync(db => db.SecretResolutions.CountAsync()));
        Assert.Contains("current repository source", (await server.LatestRunAsync(Guest)).Detail);
        Assert.Contains("current repository source", await server.QueryAsync(db => db.Attempts.Where(a => a.StepId == null).Select(a => a.WaitingReason).SingleAsync()));
    }

    private static async Task<InstallationSummaryDto> InstallationAsync(HttpClient client, string address = "home/korriban") =>
        (await client.GetFromJsonAsync<InventoryDto>("/api/inventory"))!.Labs.SelectMany(l => l.Installations).Single(i => i.Address == address);

    internal static Task<HttpResponseMessage> TrustAsync(HttpClient client, InstallationSummaryDto installation) =>
        client.PostAsJsonAsync($"/api/installations/{installation.Id}/trust", new TrustInstallationRequest(installation.Trust.Endpoints));

    internal static async Task UpdateSourceAsync(HttpClient client, string? url, string branch = "main") =>
        (await client.PutAsJsonAsync("/api/repository", new RepositoryUpdate(url, branch, 60, "none", null, null))).EnsureSuccessStatusCode();

    internal static async Task<HttpClient> AuthenticateAsync(ServerHarness server)
    {
        var client = server.CreateClient();
        var code = (await File.ReadAllTextAsync(Path.Combine(server.DataDirectory, "setup-code"))).Trim();
        var response = await client.PostAsJsonAsync("/api/auth/setup", new { setupCode = code, username = "ops", password = "a long password" });
        response.EnsureSuccessStatusCode();
        return client;
    }
}
