using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Octopushy.Server.Api;
using Octopushy.Server.Git;
using Octopushy.Server.Options;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class RepositoryEditorTests(PostgresFixture postgres)
{
    private const string Drafts = "/api/repository/drafts";

    [Fact]
    public async Task A_draft_is_edited_validated_and_pushed_only_when_publishing_is_enabled_and_confirmed()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        using var remote = new TestRepository();
        var git = server.Services.GetRequiredService<GitCli>();
        await git.RunAsync(["clone", "--bare", "--quiet", server.Repository.Root, remote.Root]);
        await server.WithDbAsync(async db =>
        {
            (await db.RepositorySettings.SingleAsync()).Url = remote.Root;
            await db.SaveChangesAsync();
        });
        await server.SyncAsync();
        var initial = (await git.RunAsync(["rev-parse", "main"], remote.Root)).Trim();
        using var anonymous = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Drafts)).StatusCode);
        using var client = await TrustedRevisionTests.AuthenticateAsync(server);

        var draft = await ReadAsync<DraftDetailDto>(await client.PostAsJsonAsync(Drafts, new DraftCreateRequest("Tidy the lab")));
        Assert.Equal("open", draft.Draft.Status);
        Assert.Equal(initial, draft.Draft.BaseCommit);
        Assert.Contains(draft.Files, f => f.Path == "labs/home/lab.yaml" && f.Kind == "file");
        Assert.Equal("The operator must enable RepositoryEditor.PublishEnabled before drafts can be pushed to Git.", draft.PublishBlockedReason);
        var path = $"{Drafts}/{draft.Draft.Id}";
        var lab = (await client.GetFromJsonAsync<DraftFileContentDto>($"{path}/file?path=labs/home/lab.yaml"))!;
        Assert.True(lab.Editable);
        Assert.Contains("name: Home", lab.Content);

        // An edit breaking a definition and a malformed non-definition file fail validation.
        draft = await ReadAsync<DraftDetailDto>(await client.PostAsJsonAsync($"{path}/changes", new DraftChangesRequest(draft.Draft.HeadCommit, [
            new("labs/home/lab.yaml", "schemaVersion: 1\nname: [unclosed\n"),
            new("notes/config.json", "{ \"open\": "),
        ])));
        Assert.Equal(2, draft.Draft.ChangedFiles);
        var validation = await ReadAsync<DraftValidationDto>(await client.PostAsync($"{path}/validate", null));
        Assert.False(validation.Valid);
        Assert.Contains(validation.NewIssues, i => i.File == "labs/home/lab.yaml" && i.Severity == "error");
        Assert.Contains(validation.FormatIssues, i => i.File == "notes/config.json");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{path}/changes",
            new DraftChangesRequest(initial, [new("stale.txt", "lost")]))).StatusCode);

        draft = await ReadAsync<DraftDetailDto>(await client.PostAsJsonAsync($"{path}/changes", new DraftChangesRequest(draft.Draft.HeadCommit, [
            new("labs/home/lab.yaml", lab.Content!.Replace("name: Home", "name: Home lab")),
            new("notes/config.json", "{ \"open\": true }\n"),
        ])));
        Assert.True((await ReadAsync<DraftValidationDto>(await client.PostAsync($"{path}/validate", null))).Valid);
        var publish = new DraftPublishRequest(draft.Draft.HeadCommit, "Rename the home lab", true);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{path}/publish", publish)).StatusCode);
        server.Services.GetRequiredService<IOptions<OctopushyOptions>>().Value.RepositoryEditor.PublishEnabled = true;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{path}/publish", publish with { ConfirmRepositoryWrite = false })).StatusCode);
        Assert.Equal(initial, (await git.RunAsync(["rev-parse", "main"], remote.Root)).Trim());

        var published = await ReadAsync<DraftPublishedDto>(await client.PostAsJsonAsync($"{path}/publish", publish));
        Assert.Equal(published.Commit, (await git.RunAsync(["rev-parse", "main"], remote.Root)).Trim());
        Assert.Equal($"ops|Rename the home lab|{initial}", (await git.RunAsync(["log", "-1", "--format=%an|%s|%P", "main"], remote.Root)).Trim());
        Assert.Equal("published", published.Draft.Status);
        Assert.Equal(2, published.Draft.ChangedFiles);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{path}/changes",
            new DraftChangesRequest(published.Commit, [new("more.txt", "x")]))).StatusCode);
        await server.SyncAsync();
        await server.WithDbAsync(async db =>
            Assert.Equal(published.Commit, (await db.RepositorySettings.SingleAsync()).HeadCommit));

        var other = await ReadAsync<DraftDetailDto>(await client.PostAsJsonAsync(Drafts, new DraftCreateRequest("Throwaway")));
        Assert.Equal(published.Commit, other.Draft.BaseCommit);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Drafts}/{other.Draft.Id}")).StatusCode);
        var list = (await client.GetFromJsonAsync<List<DraftSummaryDto>>(Drafts))!;
        Assert.Equal([draft.Draft.Id], list.Select(d => d.Id));
    }

    [Fact]
    public async Task Updating_a_behind_draft_keeps_conflict_markers_until_the_file_is_saved()
    {
        await using var server = await ServerHarness.StartAsync(postgres);
        await server.SyncAsync();
        using var client = await TrustedRevisionTests.AuthenticateAsync(server);
        var draft = await ReadAsync<DraftDetailDto>(await client.PostAsJsonAsync(Drafts, new DraftCreateRequest("Conflict")));
        var path = $"{Drafts}/{draft.Draft.Id}";
        draft = await ReadAsync<DraftDetailDto>(await client.PostAsJsonAsync($"{path}/changes",
            new DraftChangesRequest(draft.Draft.HeadCommit, [new("notes.txt", "draft\n")])));
        server.Repository.Write("notes.txt", "upstream");
        server.Commit("Upstream notes");
        await server.SyncAsync();

        var behind = (await client.GetFromJsonAsync<DraftDetailDto>(path))!;
        Assert.True(behind.Draft.Behind);
        var updated = await ReadAsync<DraftDetailDto>(await client.PostAsJsonAsync($"{path}/update", new DraftUpdateRequest(behind.Draft.HeadCommit)));
        Assert.Equal("conflicted", updated.Draft.Status);
        Assert.Equal(["notes.txt"], updated.Draft.ConflictPaths);
        Assert.False(updated.Draft.Behind);
        var marked = (await client.GetFromJsonAsync<DraftFileContentDto>($"{path}/file?path=notes.txt"))!;
        Assert.True(RepositoryEditor.HasConflictMarkers(marked.Content!));
        Assert.Equal("upstream\n", marked.BaseContent);

        var still = await ReadAsync<DraftDetailDto>(await client.PostAsJsonAsync($"{path}/changes",
            new DraftChangesRequest(updated.Draft.HeadCommit, [new("notes.txt", marked.Content + "more\n")])));
        Assert.Equal("conflicted", still.Draft.Status);
        var resolved = await ReadAsync<DraftDetailDto>(await client.PostAsJsonAsync($"{path}/changes",
            new DraftChangesRequest(still.Draft.HeadCommit, [new("notes.txt", "draft and upstream\n")])));
        Assert.Equal("open", resolved.Draft.Status);
        Assert.Empty(resolved.Draft.ConflictPaths);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
