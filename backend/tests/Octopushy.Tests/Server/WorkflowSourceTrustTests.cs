using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Api;
using Octopushy.Server.Data;
using Octopushy.Server.Git;

namespace Octopushy.Tests.Server;

public class WorkflowSourceTrustTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Path = "/api/repository/workflows/trust";
    private static Dictionary<string, string> LocalSources => new() { ["Octopushy:AllowLocalWorkflowSources"] = "true" };

    private static async Task<RepositoryDto> RepositoryAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!;

    private static ConfirmWorkflowSourceRequest Confirmation(RepositoryDto repository)
    {
        var trust = repository.WorkflowsTrust!;
        var pending = Assert.IsType<WorkflowSourcePairDto>(trust.Pending);
        return new(trust.SourceId, trust.PendingCommit!, pending.Repository, pending.CredentialKey, pending.CredentialVersion);
    }

    private static string MirrorDirectory(ServerHarness server, string url) =>
        System.IO.Path.Combine(server.DataDirectory, "git", "workflows", WorkflowsMirrors.Id(url));

    [Fact]
    public async Task First_setup_and_a_changed_URL_fetch_nothing_until_confirmed_and_keep_existing_work()
    {
        using var first = TestWorkflows.Create();
        using var second = TestWorkflows.Create();
        await using var server = await ServerHarness.StartAsync(postgres, first.Config(), settings: LocalSources);
        using var client = await InstallationTrustTests.AuthenticateAsync(server);
        await server.SyncAsync();
        var waiting = await RepositoryAsync(client);
        Assert.Null(waiting.Revision);
        Assert.Null(waiting.WorkflowsTrust!.Confirmed);
        Assert.Equal(first.Root, waiting.WorkflowsTrust.Pending!.Repository);
        Assert.False(Directory.Exists(MirrorDirectory(server, first.Root)));
        Assert.Empty(await server.QueryAsync(db => db.WorkflowReleases.ToListAsync()));
        Assert.Empty(await server.QueryAsync(db => db.Runs.ToListAsync()));

        (await client.PostAsJsonAsync(Path, Confirmation(waiting))).EnsureSuccessStatusCode();
        await server.SyncAndTrustAsync(client);
        var applied = await RepositoryAsync(client);
        Assert.True(applied.Revision!.Valid);
        Assert.Null(applied.WorkflowsTrust!.Pending);
        Assert.NotNull(applied.WorkflowsTrust.ConfirmedAt);
        Assert.NotNull(applied.WorkflowsTrust.ConfirmedBy);
        var run = await server.LatestRunAsync("home/korriban/krb-app-01");

        server.Repository.Write("octopushy.yaml", $"schemaVersion: 1\nworkflows:\n  repository: {second.Root}\n");
        server.Commit("Propose a new workflows URL");
        await server.SyncAsync();
        waiting = await RepositoryAsync(client);
        Assert.Equal(applied.Revision.Id, waiting.Revision!.Id);
        Assert.Equal(applied.HeadCommit, waiting.HeadCommit);
        Assert.Equal(first.Root, waiting.WorkflowsTrust!.Confirmed!.Repository);
        Assert.Equal(second.Root, waiting.WorkflowsTrust.Pending!.Repository);
        Assert.False(Directory.Exists(MirrorDirectory(server, second.Root)));
        Assert.DoesNotContain(await server.QueryAsync(db => db.WorkflowReleases.ToListAsync()), r => r.Repository == second.Root);
        // The retained revision is still eligible, and work already built from it can be assigned.
        var agent = await server.ConnectAgentAsync("home");
        await server.TickAsync();
        var assignment = await agent.NextAsync("preview");
        var attemptId = Guid.Parse(assignment.AttemptId);
        Assert.Equal(run.Id, await server.QueryAsync(db => db.Attempts.Where(a => a.Id == attemptId).Select(a => a.Step!.RunId).SingleAsync()));

        (await client.PostAsJsonAsync(Path, Confirmation(waiting))).EnsureSuccessStatusCode();
        await server.SyncAsync();
        var confirmed = await RepositoryAsync(client);
        Assert.NotEqual(applied.Revision.Id, confirmed.Revision!.Id);
        Assert.Equal(second.Root, confirmed.Revision.WorkflowsRepository);
        Assert.True(Directory.Exists(MirrorDirectory(server, second.Root)));
        Assert.Contains(await server.QueryAsync(db => db.WorkflowReleases.ToListAsync()), r => r.Repository == second.Root);
        // Older work retains its original source and immutable workflow commit.
        Assert.Equal(first.Root, Octopushy.Server.Orchestration.RunContext.Parse(
            (await server.QueryAsync(db => db.Runs.SingleAsync(r => r.Id == run.Id))).Context).Workflow!.Repository);
    }

    [Theory]
    [InlineData("git/new", null)]
    [InlineData("git/old", 2)]
    public async Task A_changed_credential_key_or_pinned_version_waits_and_only_the_confirmed_key_is_read(string newKey, int? version)
    {
        const string url = "https://workflows.example.invalid/workflows.git";
        using var config = TestRepository.Minimal();
        config.Delete("workflows");
        config.Delete("modules");
        config.Write("octopushy.yaml", $"schemaVersion: 1\nworkflows:\n  repository: {url}\n  credential: {{ store: git/old }}\n");
        await using var server = await ServerHarness.StartAsync(postgres, config);
        using var client = await InstallationTrustTests.AuthenticateAsync(server);
        await server.SyncAsync();
        var initial = await RepositoryAsync(client);
        // No key lookup (and no network) on discovery. Missing keys expose which pair was used after confirmation.
        Assert.Null(initial.Workflows!.LastFetchError);
        Assert.False(Directory.Exists(MirrorDirectory(server, url)));
        (await client.PostAsJsonAsync(Path, Confirmation(initial))).EnsureSuccessStatusCode();
        await server.SyncAsync();
        Assert.Contains("git/old, has not been set", (await RepositoryAsync(client)).Workflows!.LastFetchError);

        var pinned = version is { } number ? $", version: {number}" : "";
        config.Write("octopushy.yaml", $"schemaVersion: 1\nworkflows:\n  repository: {url}\n  credential: {{ store: {newKey}{pinned} }}\n");
        server.Commit("Propose a different credential reference");
        await server.SyncAsync();
        var waiting = await RepositoryAsync(client);
        Assert.Equal("git/old", waiting.WorkflowsTrust!.Confirmed!.CredentialKey);
        Assert.Null(waiting.WorkflowsTrust.Confirmed.CredentialVersion);
        Assert.Equal(newKey, waiting.WorkflowsTrust.Pending!.CredentialKey);
        Assert.Equal(version, waiting.WorkflowsTrust.Pending.CredentialVersion);
        Assert.Contains("git/old, has not been set", waiting.Workflows!.LastFetchError);
        Assert.DoesNotContain("git/new", waiting.Workflows.LastFetchError!);
        (await client.PostAsJsonAsync(Path, Confirmation(waiting))).EnsureSuccessStatusCode();
        await server.SyncAsync();
        var confirmed = await RepositoryAsync(client);
        Assert.Null(confirmed.WorkflowsTrust!.Pending);
        Assert.Equal(newKey, confirmed.WorkflowsTrust.Confirmed!.CredentialKey);
        Assert.Equal(version, confirmed.WorkflowsTrust.Confirmed.CredentialVersion);
        Assert.Contains($"{newKey}, has not been set", confirmed.Workflows!.LastFetchError);
    }

    [Fact]
    public async Task Confirmation_requires_an_operator_session_and_the_exact_discovered_source_commit_and_pair()
    {
        using var workflows = TestWorkflows.Create();
        await using var server = await ServerHarness.StartAsync(postgres, workflows.Config(), settings: LocalSources);
        using var client = await InstallationTrustTests.AuthenticateAsync(server);
        await server.SyncAsync();
        var request = Confirmation(await RepositoryAsync(client));
        using var anonymous = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Path, request)).StatusCode);
        var created = (await (await client.PostAsJsonAsync("/api/auth/operator-tokens", new { name = "automation" }))
            .Content.ReadFromJsonAsync<Octopushy.Server.Auth.CreatedOperatorToken>())!;
        using var automation = server.CreateClient();
        automation.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.Token);
        Assert.Equal(HttpStatusCode.OK, (await automation.GetAsync("/api/repository")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await automation.PostAsJsonAsync(Path, request)).StatusCode);
        foreach (var stale in new[] { request with { SourceId = Guid.NewGuid() }, request with { Commit = new string('f', 40) },
                     request with { CredentialKey = "git/other" }, request with { CredentialVersion = 3 }, request with { Repository = workflows.Root + "/." } })
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path, stale)).StatusCode);
        Assert.Null((await RepositoryAsync(client)).WorkflowsTrust!.Confirmed);
        // A newer discovery invalidates the old screen even when the pair is unchanged.
        server.Commit("Another lab commit");
        await server.SyncAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path, request)).StatusCode);
        (await client.PostAsJsonAsync(Path, Confirmation(await RepositoryAsync(client)))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path,
            new { request.SourceId, commit = (string?)null, repository = (string?)null, credentialKey = (string?)null, credentialVersion = (int?)null })).StatusCode);
        // Trust persists over restart, but does not carry to another lab source or branch.
        await server.RestartFixtureAsync();
        using var restarted = server.CreateClient();
        (await restarted.PostAsJsonAsync("/api/auth/login", new { username = "ops", password = "a long password" })).EnsureSuccessStatusCode();
        Assert.Equal(workflows.Root, (await RepositoryAsync(restarted)).WorkflowsTrust!.Confirmed!.Repository);
        var repo = await RepositoryAsync(restarted);
        (await restarted.PutAsJsonAsync("/api/repository", new RepositoryUpdate(repo.Url, "another-branch", repo.PollSeconds,
            repo.CredentialKind, repo.CredentialUsername, null))).EnsureSuccessStatusCode();
        var changed = await RepositoryAsync(restarted);
        Assert.Null(changed.WorkflowsTrust!.Confirmed);
        Assert.Null(changed.WorkflowsTrust.Pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repository_declared_absolute_paths_and_file_URLs_are_rejected_by_default_even_when_confirmed(bool fileUrl)
    {
        using var workflows = TestWorkflows.Create();
        var config = workflows.Config();
        var url = fileUrl ? new Uri(workflows.Root).AbsoluteUri : workflows.Root;
        config.Write("octopushy.yaml", $"schemaVersion: 1\nworkflows:\n  repository: {url}\n");
        // No local-source option here: this tests the production default, not the opt-in fixture path.
        await using var server = await ServerHarness.StartAsync(postgres, config);
        using var client = await InstallationTrustTests.AuthenticateAsync(server);
        await server.SyncAsync();
        var waiting = await RepositoryAsync(client);
        Assert.Contains("disabled", waiting.LastFetchError);
        Assert.Contains("disabled", waiting.WorkflowsTrust!.BlockedReason);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path, Confirmation(waiting))).StatusCode);
        Assert.False(Directory.Exists(MirrorDirectory(server, url)));
        // Turning the setting off must also stop a pair confirmed while it was enabled.
        await server.WithDbAsync(async db =>
        {
            (await db.RepositorySettings.SingleAsync()).ConfirmedWorkflowsRepository = url;
            await db.SaveChangesAsync();
        });
        await server.SyncAsync();
        Assert.False(Directory.Exists(MirrorDirectory(server, url)));
        Assert.Empty(await server.QueryAsync(db => db.WorkflowReleases.ToListAsync()));
    }
}
