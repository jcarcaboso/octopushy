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
public class TrustedRevisionTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";

    [Fact]
    public async Task Discovery_waits_for_exact_revision_trust_without_releasing_credentials()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAsync();
        var agent = await server.ConnectAgentAsync("home");
        await server.TickAsync();
        var run = await server.LatestRunAsync("home/korriban/krb-app-01");
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("Waiting for trust", run.Detail);
        Assert.Empty(agent.TakeAssignments());
        Assert.Equal(0, await server.QueryAsync(db => db.SecretResolutions.CountAsync()));

        using var client = await AuthenticateAsync(server);
        var revision = (await client.GetFromJsonAsync<JsonArray>("/api/repository/revisions"))![0]!;
        var path = $"/api/repository/revisions/{revision["id"]}";
        var review = (await client.GetFromJsonAsync<JsonObject>(path + "/review"))!;
        Assert.True(review["canTrust"]!.GetValue<bool>());
        Assert.Null(review["baseCommit"]);
        Assert.Contains("guest.yaml", review["diff"]!.GetValue<string>());
        var response = await client.PostAsJsonAsync(path + "/trust", new { commit = revision["commit"]!.GetValue<string>(), baseCommit = (string?)null });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var trusted = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("ops", trusted["trustedBy"]!.GetValue<string>());
        Assert.NotNull(trusted["trustedAt"]);
        Assert.True(trusted["isCurrentSource"]!.GetValue<bool>());
        await agent.NextAsync("preview");
        Assert.True(await server.QueryAsync(db => db.SecretResolutions.AnyAsync()));
    }

    [Fact]
    public async Task Trust_requires_authentication_and_rejects_stale_targets_and_review_bases()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAsync();
        using var anonymous = server.CreateClient();
        using var client = await AuthenticateAsync(server);
        var first = (await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!;
        var path = $"/api/repository/revisions/{first.Id}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path + "/review")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(path + "/trust", new TrustRevisionRequest(first.Commit, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path + "/trust", new TrustRevisionRequest(new string('a', 40), null))).StatusCode);
        (await TrustAsync(client, first.Id)).EnsureSuccessStatusCode();
        server.Repository.Write("notes.txt", "Second discovered revision\n");
        server.Commit("Second");
        await server.SyncAsync();
        var second = (await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!;
        server.Repository.Write("notes.txt", "Third discovered revision\n");
        server.Commit("Third");
        await server.SyncAsync();
        var third = (await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!;
        var review = (await client.GetFromJsonAsync<RevisionReviewDto>($"/api/repository/revisions/{third.Id}/review"))!;
        Assert.Equal(first.Commit, review.BaseCommit);
        (await TrustAsync(client, second.Id)).EnsureSuccessStatusCode();
        var stale = await client.PostAsJsonAsync($"/api/repository/revisions/{third.Id}/trust", new TrustRevisionRequest(third.Commit, review.BaseCommit));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("review", (await stale.Content.ReadAsStringAsync()).ToLowerInvariant());
        (await TrustAsync(client, third.Id)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Source_changes_and_disconnects_invalidate_same_hash_authority_and_current_labels()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        var first = (await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!;
        var agent = await server.ConnectAgentAsync("home");
        await UpdateSourceAsync(client, server.Repository.Root + "/.");
        Assert.Null((await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision);
        var detail = (await client.GetFromJsonAsync<RevisionDetailDto>($"/api/repository/revisions/{first.Id}"))!;
        Assert.False(detail.Revision.IsCurrentSource);
        Assert.NotNull(detail.Revision.TrustedAt);
        Assert.Equal(HttpStatusCode.Conflict, (await TrustAsync(client, first.Id)).StatusCode);
        await server.SyncAsync();
        var second = (await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!;
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Commit, second.Commit);
        Assert.Null(second.TrustedAt);
        Assert.True(second.IsCurrentSource);
        await server.TickAsync();
        Assert.Empty(agent.TakeAssignments());
        Assert.Equal(0, await server.QueryAsync(db => db.SecretResolutions.CountAsync()));
        (await TrustAsync(client, second.Id)).EnsureSuccessStatusCode();
        await agent.NextAsync("preview");
        await UpdateSourceAsync(client, null);
        await UpdateSourceAsync(client, server.Repository.Root + "/.");
        await server.SyncAsync();
        var third = (await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!;
        Assert.Null(third.TrustedAt);
        Assert.NotEqual(second.Id, third.Id);
    }

    [Fact]
    public async Task Untrusted_inventory_does_not_cancel_a_pinned_trusted_plan_or_approve_apply()
    {
        using var repository = TestRepository.Minimal();
        repository.Write("labs/home/lab.yaml", "schemaVersion: 1\nexecution: { agentLabels: [home] }\npolicy: { approval: manual }\n");
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        using var client = await AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        var agent = await server.ConnectAgentAsync("home");
        var plan = await agent.NextAsync("preview");
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        var prepared = await server.LatestRunAsync(Guest);
        Assert.Equal(RunStatuses.AwaitingApproval, prepared.Status);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "apply");
        var guestFile = Path.Combine(repository.Root, "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml");
        File.WriteAllText(guestFile, File.ReadAllText(guestFile).Replace("memory: 2048", "memory: 4096"));
        server.Commit("Untrusted desired change");
        await server.SyncAsync();
        await server.TickAsync();
        var old = await server.QueryAsync(db => db.Runs.Include(r => r.Steps).SingleAsync(r => r.Id == prepared.Id));
        Assert.Equal(RunStatuses.AwaitingApproval, old.Status);
        var approval = old.Steps.Single(s => s.Name == StepNames.Approval);
        await server.Engine.ApproveAsync(old.Id, approval.Id, approval.PlanSha256!, "ops", null, CancellationToken.None);
        var apply = await agent.NextAsync("apply");
        Assert.Equal(prepared.Id, await server.QueryAsync(db => db.Attempts.Where(a => a.Id == Guid.Parse(apply.AttemptId)).Select(a => a.RunId).SingleAsync()));
        Assert.Equal(RunStatuses.Blocked, (await server.LatestRunAsync(Guest)).Status);
    }

    [Fact]
    public async Task Trusting_a_metadata_only_revision_preserves_the_existing_plan_approval_and_artifact()
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
        await server.SyncAndTrustAsync(client);
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
    public async Task Scheduled_observation_uses_the_trusted_snapshot_even_when_untrusted_discovery_invalidates_inventory()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        var trusted = (await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!;
        var agent = await server.ConnectAgentAsync("home");
        await agent.ReportAsync(await agent.NextAsync("observe"), Outcome.Succeeded, new JsonObject());
        await server.WithDbAsync(db => db.Runs.Where(r => r.Operation == Operations.Observe)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.CreatedAt, DateTimeOffset.UtcNow.AddDays(-1))));
        server.Repository.Write("labs/home/proxmox/korriban/installation.yaml", "not: a valid installation\n");
        server.Commit("Untrusted broken installation");
        await server.SyncAsync();
        await server.Services.GetRequiredService<Planner>().ScheduleObservesAsync(CancellationToken.None);
        var observe = await agent.NextAsync("observe");
        var run = await server.QueryAsync(db => db.Runs.SingleAsync(r => r.Id == db.Attempts.Where(a => a.Id == Guid.Parse(observe.AttemptId)).Select(a => a.RunId).Single()));
        Assert.Equal(trusted.Id, run.RevisionId);
        Assert.Contains("10.1.0.10", observe.InputJson);
        await UpdateSourceAsync(client, null);
        var count = await server.QueryAsync(db => db.Runs.CountAsync());
        await server.Services.GetRequiredService<Planner>().ScheduleObservesAsync(CancellationToken.None);
        Assert.Equal(count, await server.QueryAsync(db => db.Runs.CountAsync()));
    }

    [Theory]
    [InlineData("apply", Outcome.Failed, RunStatuses.Failed)]
    [InlineData("observe", Outcome.Succeeded, RunStatuses.Succeeded)]
    public async Task A_source_change_does_not_mask_a_known_failure_or_completed_observation(string action, Outcome outcome, string expected)
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
    public async Task A_filesystem_retry_revalidates_revision_trust_before_reopening_a_failed_step()
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
        Assert.Contains("Waiting for trust", await response.Content.ReadAsStringAsync());
        Assert.Equal(RunStatuses.Failed, (await server.LatestRunAsync(Guest)).Status);
    }

    [Fact]
    public async Task The_real_assignment_builder_itself_refuses_missing_provenance_before_secret_resolution()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        await server.TickAsync();
        var run = await server.LatestRunAsync(Guest);
        run.RevisionId = null;
        var step = run.Steps.Single(s => s.Name == StepNames.Plan);
        await using var scope = server.Services.CreateAsyncScope();
        await Assert.ThrowsAsync<RevisionUntrustedException>(() => scope.ServiceProvider.GetRequiredService<AssignmentBuilder>()
            .BuildAsync(Assert.Single(step.Attempts), step, run, RunContext.Parse(run.Context), Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(0, await server.QueryAsync(db => db.SecretResolutions.CountAsync()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Persisted_trust_survives_only_in_the_fixture_database_and_restart_never_grants_missing_authority(bool trustBeforeRestart)
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
        var revision = (await afterClient.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!;
        Assert.Equal(trustBeforeRestart, revision.TrustedAt is not null);
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

    [Fact]
    public async Task A_branch_change_with_the_same_hash_requires_a_fresh_current_source_review()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var client = await AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        var first = (await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!;
        await server.Services.GetRequiredService<GitCli>().RunAsync(["-C", server.Repository.Root, "branch", "another", "main"]);
        await UpdateSourceAsync(client, server.Repository.Root, "another");
        await server.SyncAsync();
        var second = (await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!;
        Assert.Equal(first.Commit, second.Commit);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Null(second.TrustedAt);
        var review = (await client.GetFromJsonAsync<RevisionReviewDto>($"/api/repository/revisions/{second.Id}/review"))!;
        Assert.Null(review.BaseCommit);
        Assert.NotEmpty(review.Diff);
    }

    [Fact]
    public async Task An_oversized_binary_patch_cannot_be_confirmed_as_an_empty_or_partial_review()
    {
        using var repository = TestRepository.Minimal();
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(600 * 1024);
        bytes[0] = 0;
        await File.WriteAllBytesAsync(Path.Combine(repository.Root, "bundled-code.bin"), bytes);
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        await server.SyncAsync();
        using var client = await AuthenticateAsync(server);
        var revision = (await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!;
        var review = (await client.GetFromJsonAsync<RevisionReviewDto>($"/api/repository/revisions/{revision.Id}/review"))!;
        Assert.False(review.CanTrust);
        Assert.True(review.Truncated);
        Assert.Contains("512 KiB", review.BlockedReason);
        Assert.Equal(HttpStatusCode.Conflict, (await TrustAsync(client, revision.Id)).StatusCode);
        Assert.Null((await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!.TrustedAt);
    }

    [Fact]
    public async Task Non_utf8_text_cannot_be_trusted_until_the_source_encoding_is_corrected()
    {
        using var repository = TestRepository.Minimal();
        // No NUL: Git treats this as text even with --binary, but it is not UTF-8.
        var bytes = "#!/bin/sh\nprintf \""u8.ToArray().Concat(new byte[] { 0xff, (byte)'"', (byte)'\n' }).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(repository.Root, "helper.sh"), bytes);
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        await server.SyncAsync();
        using var client = await AuthenticateAsync(server);
        var revision = (await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!;
        var review = (await client.GetFromJsonAsync<RevisionReviewDto>($"/api/repository/revisions/{revision.Id}/review"))!;
        Assert.False(review.CanTrust);
        Assert.False(review.Truncated);
        Assert.Empty(review.Diff);
        Assert.Contains("UTF-8", review.BlockedReason);
        Assert.Equal(HttpStatusCode.Conflict, (await TrustAsync(client, revision.Id)).StatusCode);
        Assert.Null((await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!.TrustedAt);
        Assert.False(await server.QueryAsync(db => db.SecretResolutions.AnyAsync()));

        repository.Write("helper.sh", "#!/bin/sh\nprintf 'UTF-8 fixture only\\n'");
        server.Commit("Normalize source encoding for review");
        await server.SyncAsync();
        var corrected = (await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!;
        var correctedReview = (await client.GetFromJsonAsync<RevisionReviewDto>($"/api/repository/revisions/{corrected.Id}/review"))!;
        Assert.True(correctedReview.CanTrust);
        Assert.Contains("UTF-8 fixture only", correctedReview.Diff);
        (await TrustAsync(client, corrected.Id)).EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_definitions_or_unavailable_git_objects_cannot_be_trusted(bool invalidDefinition)
    {
        using var repository = TestRepository.Minimal();
        if (invalidDefinition) repository.Write("octopushy.yaml", "schemaVersion: 999\n");
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        await server.SyncAsync();
        using var client = await AuthenticateAsync(server);
        var revision = (await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!;
        if (!invalidDefinition) Directory.Delete(Path.Combine(server.DataDirectory, "git", "mirror.git"), recursive: true);
        var review = (await client.GetFromJsonAsync<RevisionReviewDto>($"/api/repository/revisions/{revision.Id}/review"))!;
        Assert.False(review.CanTrust);
        Assert.False(review.Truncated);
        Assert.NotNull(review.BlockedReason);
        Assert.Equal(HttpStatusCode.Conflict, (await TrustAsync(client, revision.Id)).StatusCode);
        Assert.Null((await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!.TrustedAt);
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
        await server.SyncAndTrustAsync(client);
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
    public async Task Missing_or_mismatched_run_provenance_fails_closed_even_with_a_trusted_revision(bool missing)
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
            await db.SaveChangesAsync();
        });
        var agent = await server.ConnectAgentAsync("home");
        await server.Engine.DispatchAsync(CancellationToken.None); // No advancement pass to help the final hand-off guard.
        Assert.Empty(agent.TakeAssignments());
        Assert.Equal(0, await server.QueryAsync(db => db.SecretResolutions.CountAsync()));
        Assert.Contains("Waiting for trust", (await server.LatestRunAsync(Guest)).Detail);
    }

    internal static async Task<HttpResponseMessage> TrustAsync(HttpClient client, Guid id)
    {
        var path = $"/api/repository/revisions/{id}";
        var review = (await client.GetFromJsonAsync<RevisionReviewDto>(path + "/review"))!;
        return await client.PostAsJsonAsync(path + "/trust", new TrustRevisionRequest(review.Revision.Commit, review.BaseCommit));
    }

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
