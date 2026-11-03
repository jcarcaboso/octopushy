using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Octopushy.Agent.V1;
using Octopushy.Server.Api;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>Workflows from their own repository, released by tags: what a sync records, resolves and runs.</summary>
[Collection(PostgresCollection.Name)]
public class WorkflowsRepositoryTests(PostgresFixture postgres)
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string Pinned = "home/korriban/krb-app-02";
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";

    /// <summary>The minimal lab, with its workflows from <paramref name="workflows"/>, and a second guest pinned to exactly 1.0.</summary>
    private static TestRepository Config(TestWorkflows workflows)
    {
        var config = workflows.Config();
        var guest = File.ReadAllText(Path.Combine(config.Root, GuestFile));
        config.Write("labs/home/proxmox/korriban/guests/krb-app-02/guest.yaml", guest
            .Replace("vmid: 310", "vmid: 311").Replace("10.1.0.31", "10.1.0.32").Replace("workflow: test-vm@1", "workflow: test-vm@1.0").TrimEnd('\n'));
        return config;
    }

    private static Task<List<WorkflowReleaseRecord>> ReleasesAsync(ServerHarness server) =>
        server.QueryAsync(db => db.WorkflowReleases.AsNoTracking().OrderBy(r => r.Major).ThenBy(r => r.Minor).ToListAsync());

    private static Task<List<DefinitionRevision>> RevisionsAsync(ServerHarness server) =>
        server.QueryAsync(db => db.Revisions.AsNoTracking().OrderBy(r => r.Sequence).ToListAsync());

    private static Task<List<Run>> RunsAsync(ServerHarness server, string address) =>
        server.QueryAsync(async db =>
        {
            var resource = await db.Resources.AsNoTracking().SingleAsync(r => r.Address == address);
            return await db.Runs.AsNoTracking().Where(r => r.ResourceId == resource.Id).OrderBy(r => r.Number).ToListAsync();
        });

    private static async Task<RepositoryDto> RepositoryAsync(HttpClient client) => (await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!;

    private static Dictionary<string, string> Bundle(byte[] bundle)
    {
        using var gzip = new GZipStream(new MemoryStream(bundle), CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.GetNextEntry() is { } entry)
        {
            using var content = new StreamReader(entry.DataStream!, Encoding.UTF8);
            files[entry.Name] = content.ReadToEnd();
        }

        return files;
    }

    [Fact]
    public async Task A_new_minor_release_is_a_new_revision_of_the_same_commit_and_moves_only_the_guests_on_its_major()
    {
        using var workflows = TestWorkflows.Create();
        var first = workflows.Head;
        await using var server = await ServerHarness.StartAsync(postgres, Config(workflows));
        using var client = await InstallationTrustTests.AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);

        var revision = Assert.Single(await RevisionsAsync(server));
        Assert.True(revision.RepositoryValid, revision.Issues);
        Assert.Equal(workflows.Root, revision.WorkflowsRepository);
        Assert.NotEqual("", revision.WorkflowsDigest);
        var release = Assert.Single(await ReleasesAsync(server));
        Assert.Equal(("test-vm", "1.0", first, null), (release.WorkflowId, release.Version, release.Commit, release.Problem));
        var run = Assert.Single(await RunsAsync(server, Guest));
        var workflow = RunContext.Parse(run.Context).Workflow!;
        Assert.Equal(new WorkflowContext("test-vm", "1.0", "test-vm@1", first, workflows.Root), workflow);
        Assert.Single(await RunsAsync(server, Pinned));

        // A sync that finds nothing new loads nothing.
        await server.SyncAsync();
        Assert.Single(await RevisionsAsync(server));

        workflows.WriteVersion("1.1", "A test VM, now better.");
        var second = workflows.Commit("test-vm 1.1");
        workflows.Tag("test-vm/v1.1");
        await server.SyncAsync();

        var revisions = await RevisionsAsync(server);
        Assert.Equal(2, revisions.Count);
        Assert.Equal(revisions[0].Commit, revisions[1].Commit);
        Assert.NotEqual(revisions[0].WorkflowsDigest, revisions[1].WorkflowsDigest);
        Assert.Equal(revisions[1].Id, (await server.QueryAsync(db => db.RepositorySettings.AsNoTracking().SingleAsync())).HeadRevisionId);

        // The guest on @1 is planned again with 1.1; the guest pinned to @1.0 is not.
        var runs = await RunsAsync(server, Guest);
        Assert.Equal(2, runs.Count);
        Assert.Equal(revisions[1].Id, runs[1].RevisionId);
        Assert.Equal(new WorkflowContext("test-vm", "1.1", "test-vm@1", second, workflows.Root), RunContext.Parse(runs[1].Context).Workflow);
        Assert.Equal(RunStatuses.Cancelled, runs[0].Status);
        var pinned = Assert.Single(await RunsAsync(server, Pinned));
        Assert.Equal(revisions[0].Id, pinned.RevisionId);
        Assert.Equal("1.0", RunContext.Parse(pinned.Context).Workflow!.Version);

        var repository = await RepositoryAsync(client);
        Assert.Equal(revisions[1].WorkflowsDigest, repository.Revision!.WorkflowsDigest);
        var source = repository.Workflows!;
        Assert.Equal(("repository", workflows.Root, null), (source.Source, source.Repository, source.LastFetchError));
        Assert.Equal(["1.1", "1.0"], Assert.Single(source.Workflows).Releases.Select(r => r.Version));
        Assert.Empty(source.Issues);
        Assert.Equal(new (string, string, string?, string?)[] { (Guest, "test-vm@1", "1.1", second), (Pinned, "test-vm@1.0", "1.0", first) },
            source.Uses.Select(u => (u.Address, u.Reference, u.Version, u.Commit)));

        // A new major is offered, not taken.
        workflows.WriteVersion("2.0");
        workflows.Commit("test-vm 2.0");
        workflows.Tag("test-vm/v2.0");
        await server.SyncAsync();
        Assert.Equal(2, (await RevisionsAsync(server)).Count);
        Assert.Equal(2, (await RunsAsync(server, Guest)).Count);
        Assert.Equal("2.0", (await RepositoryAsync(client)).Workflows!.Uses.Single(u => u.Address == Guest).NewerMajor);
    }

    [Fact]
    public async Task An_attempts_bundle_is_the_config_tree_with_the_workflows_tree_at_the_resolved_commit()
    {
        using var workflows = TestWorkflows.Create();
        var tagged = workflows.Head;
        // Untagged work on main is never what a run gets.
        workflows.WriteVersion("1.0", "Untagged work in progress.");
        workflows.Files.Write("workflows/test-vm/notes.md", "draft");
        workflows.Commit("work in progress");
        await using var server = await ServerHarness.StartAsync(postgres, Config(workflows));
        var agent = await server.ConnectAgentAsync("home");
        using var client = await InstallationTrustTests.AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);

        var plan = await agent.NextAsync("preview");
        var files = Bundle(plan.FilesTarGz.ToByteArray());
        Assert.Contains("octopushy.yaml", files.Keys);
        Assert.Contains(GuestFile, files.Keys);
        Assert.Contains("description: A test VM.", files[TestWorkflows.Workflow]);
        Assert.Contains(TestWorkflows.Module, files.Keys);
        Assert.DoesNotContain("workflows/test-vm/notes.md", files.Keys);
        Assert.Equal("modules/opentofu/test-vm", System.Text.Json.Nodes.JsonNode.Parse(plan.InputJson)!["root"]!.GetValue<string>());

        var run = await server.LatestRunAsync(Guest, Operations.Provision);
        var detail = (await client.GetFromJsonAsync<RunDetailDto>($"/api/runs/{run.Id}"))!;
        Assert.Equal(new RunWorkflowDto("test-vm", "1.0", "test-vm@1", tagged, workflows.Root), detail.Workflow);
        await agent.ReportAsync(plan, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
        var apply = await agent.NextAsync("apply");
        Assert.Equal(files[TestWorkflows.Workflow], Bundle(apply.FilesTarGz.ToByteArray())[TestWorkflows.Workflow]);
    }

    [Fact]
    public async Task A_moved_tag_keeps_the_first_commit_and_is_shown_as_an_issue()
    {
        using var workflows = TestWorkflows.Create();
        var original = workflows.Head;
        await using var server = await ServerHarness.StartAsync(postgres, Config(workflows));
        using var client = await InstallationTrustTests.AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        var digest = Assert.Single(await RevisionsAsync(server)).WorkflowsDigest;

        workflows.Files.Write(TestWorkflows.Module, "resource \"terraform_data\" \"moved\" {}");
        var moved = workflows.Commit("rewrite 1.0");
        workflows.Tag("test-vm/v1.0", force: true);
        await server.SyncAsync();

        var release = Assert.Single(await ReleasesAsync(server));
        Assert.Equal((original, moved), (release.Commit, release.MovedTo));
        // What 1.0 is did not change, so neither did the revision.
        Assert.Equal(digest, Assert.Single(await RevisionsAsync(server)).WorkflowsDigest);
        Assert.Single(await RunsAsync(server, Guest));

        var source = (await RepositoryAsync(client)).Workflows!;
        Assert.Equal([$"test-vm/v1.0 was moved from {original[..10]} to {moved[..10]}; the original commit is used. Moved tags are not followed."], source.Issues);
        var shown = Assert.Single(Assert.Single(source.Workflows).Releases);
        Assert.Equal((original, moved, true), (shown.Commit, shown.MovedTo, shown.Usable));
    }

    [Fact]
    public async Task A_tag_whose_workflow_declares_another_version_is_an_invalid_release_and_never_used()
    {
        using var workflows = TestWorkflows.Create();
        await using var server = await ServerHarness.StartAsync(postgres, Config(workflows));
        workflows.Files.Write(TestWorkflows.Module, "resource \"terraform_data\" \"forgot\" {}");
        var wrong = workflows.Commit("forgot the version bump");
        workflows.Tag("test-vm/v1.1");
        workflows.Tag("not-a-release");
        using var client = await InstallationTrustTests.AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);

        var releases = await ReleasesAsync(server);
        Assert.Equal(["1.0", "1.1"], releases.Select(r => r.Version));
        Assert.Equal("workflows/test-vm/workflow.yaml declares version \"1.0\", and the tag says 1.1.", releases[1].Problem);
        var run = Assert.Single(await RunsAsync(server, Guest));
        Assert.Equal("1.0", RunContext.Parse(run.Context).Workflow!.Version);

        Assert.Equal([$"The release test-vm/v1.1 at {wrong[..10]} is invalid and is never used: workflows/test-vm/workflow.yaml declares version \"1.0\", and the tag says 1.1."],
            (await RepositoryAsync(client)).Workflows!.Issues);
    }

    [Fact]
    public async Task A_deleted_tag_stays_a_release_and_one_whose_commit_is_gone_makes_its_references_invalid()
    {
        using var workflows = TestWorkflows.Create();
        await using var server = await ServerHarness.StartAsync(postgres, Config(workflows));
        using var client = await InstallationTrustTests.AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        workflows.DeleteTag("test-vm/v1.0");
        await server.SyncAsync();

        var release = Assert.Single(await ReleasesAsync(server));
        Assert.NotNull(release.DeletedAt);
        Assert.Single(await RevisionsAsync(server));
        Assert.True((await server.ResourceAsync(Guest)).Valid);

        // A release recorded once whose commit the mirror no longer holds, as after a rebuilt mirror.
        await server.WithDbAsync(async db =>
        {
            db.WorkflowReleases.Add(new WorkflowReleaseRecord
            {
                Repository = workflows.Root, WorkflowId = "test-vm", Major = 1, Minor = 5, Commit = new string('e', 40),
                FirstSeenAt = DateTimeOffset.UtcNow, DeletedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        await server.SyncAsync();

        Assert.Equal(2, (await RevisionsAsync(server)).Count);
        var guest = await server.ResourceAsync(Guest);
        Assert.False(guest.Valid);
        Assert.Contains("test-vm@1 resolved to test-vm@1.5, whose commit eeeeeeeeee is no longer in the mirror", guest.Issues);
        Assert.True((await server.ResourceAsync(Pinned)).Valid);
        Assert.Contains("test-vm/v1.5 was deleted, and its commit eeeeeeeeee is no longer in the mirror: references that resolve to it are invalid.",
            (await RepositoryAsync(client)).Workflows!.Issues);
    }

    [Fact]
    public async Task A_lab_repository_that_keeps_workflows_of_its_own_beside_a_workflows_repository_is_invalid()
    {
        using var workflows = TestWorkflows.Create();
        var config = TestRepository.Minimal();
        config.Write("octopushy.yaml", $"schemaVersion: 1\nworkflows:\n  repository: {workflows.Root}\n");
        await using var server = await ServerHarness.StartAsync(postgres, config);
        await server.SyncAsync();
        var revision = Assert.Single(await RevisionsAsync(server));
        Assert.False(revision.RepositoryValid);
        Assert.Contains($"Workflows come from {workflows.Root}; remove workflows/ and modules/ here.", revision.Issues);
        Assert.Empty(await server.QueryAsync(db => db.Runs.AsNoTracking().ToListAsync()));
    }
}
