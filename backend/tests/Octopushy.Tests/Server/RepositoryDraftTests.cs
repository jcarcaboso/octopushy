using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Octopushy.Server.Api;
using Octopushy.Server.Git;
using Octopushy.Server.Options;

namespace Octopushy.Tests.Server;

public sealed class RepositoryDraftTests : IDisposable
{
    private readonly TestRepository _remote = new();
    private readonly TestRepository _source = new();
    private readonly TestRepository _data = new();
    private readonly GitCli _git = new(NullLogger<GitCli>.Instance);
    private readonly RepositoryMirror _mirror;
    private static readonly CommitAuthor Author = new("admin", "admin@octopushy.invalid");

    public RepositoryDraftTests()
    {
        _mirror = new RepositoryMirror(_git, Options.Create(new OctopushyOptions { DataDirectory = _data.Root }));
    }

    public void Dispose()
    {
        _remote.Dispose();
        _source.Dispose();
        _data.Dispose();
    }

    private async Task<string> InitAsync()
    {
        await _git.RunAsync(["init", "--bare", "--quiet", "-b", "main", _remote.Root]);
        await _git.RunAsync(["init", "--quiet", "-b", "main", _source.Root]);
        // The fixture writer ends each file with a newline.
        _source.Write("notes.txt", "one\ntwo\nthree");
        _source.Write("deploy.sh", "#!/bin/sh\necho hi");
        _source.Write("docs/readme.md", "hello");
        File.SetUnixFileMode(Path.Combine(_source.Root, "deploy.sh"), (UnixFileMode)0b111_101_101);
        await CommitAndPushAsync("base");
        return await _mirror.FetchAsync(_remote.Root, "main", GitCredential.None, default);
    }

    private async Task CommitAndPushAsync(string message)
    {
        await _git.RunAsync(["add", "-A"], _source.Root);
        await _git.RunAsync(["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
            "commit", "--quiet", "-m", message], _source.Root);
        await _git.RunAsync(["push", "--quiet", _remote.Root, "HEAD:refs/heads/main"], _source.Root);
    }

    private Task<string> RemoteAsync(params string[] arguments) => _git.RunAsync(arguments, _remote.Root);

    [Fact]
    public async Task Edits_are_literal_commits_on_the_draft_that_keep_modes_and_refuse_stale_heads()
    {
        var basis = await InitAsync();
        var id = Guid.NewGuid();
        await _mirror.CreateDraftAsync(id, basis, default);

        var head = await _mirror.WriteDraftAsync(id, basis, [
            new("deploy.sh", "#!/bin/sh\necho changed\r\n"),
            new("guests/new.yaml", "literal $Id$\n"),
            new("docs/readme.md", null),
        ], "edit", default);

        var changes = await _mirror.ChangesAsync(basis, head, default);
        Assert.Equal([new("deploy.sh", "modified"), new("docs/readme.md", "deleted"), new("guests/new.yaml", "added")],
            changes.OrderBy(c => c.Path, StringComparer.Ordinal));
        var script = await _mirror.ReadFileAsync(head, "deploy.sh", default);
        Assert.Equal("100755", script!.File.Mode);
        Assert.Equal("#!/bin/sh\necho changed\r\n", script.Text);
        Assert.Null(await _mirror.ReadFileAsync(head, "docs/readme.md", default));
        // Nothing reaches the remote until the draft is published.
        Assert.Equal(basis, (await RemoteAsync("rev-parse", "main")).Trim());

        Assert.Equal(head, await _mirror.WriteDraftAsync(id, head, [new("notes.txt", "one\ntwo\nthree\n")], "no-op", default));
        await Assert.ThrowsAsync<DraftChangedException>(() =>
            _mirror.WriteDraftAsync(id, basis, [new("notes.txt", "stale")], "stale", default));
        await Assert.ThrowsAsync<GitException>(() =>
            _mirror.WriteDraftAsync(id, head, [new("notes.txt/child", "under a file")], "collide", default));
        await Assert.ThrowsAsync<GitException>(() =>
            _mirror.WriteDraftAsync(id, head, [new("guests", "over a folder")], "collide", default));
        await Assert.ThrowsAsync<GitException>(() =>
            _mirror.WriteDraftAsync(id, head, [new(".git/config", "escape")], "unsafe", default));
    }

    [Fact]
    public async Task Publishing_an_up_to_date_draft_pushes_one_commit_attributed_to_the_operator()
    {
        var basis = await InitAsync();
        var id = Guid.NewGuid();
        await _mirror.CreateDraftAsync(id, basis, default);
        var first = await _mirror.WriteDraftAsync(id, basis, [new("notes.txt", "one\n")], "first", default);
        var head = await _mirror.WriteDraftAsync(id, first, [new("extra.txt", "x\n")], "second", default);

        var candidate = await _mirror.PreparePublishAsync(_remote.Root, "main", GitCredential.None, basis, head, "Edit notes", Author, default);
        Assert.Empty(candidate.Conflicts);
        Assert.Equal(basis, candidate.Remote);
        await _mirror.PushAsync(_remote.Root, "main", GitCredential.None, candidate.Remote, candidate.Commit!, default);

        Assert.Equal(candidate.Commit, (await RemoteAsync("rev-parse", "main")).Trim());
        Assert.Equal(basis, (await RemoteAsync("rev-parse", "main^")).Trim());
        Assert.Equal("admin <admin@octopushy.invalid>|Edit notes", (await RemoteAsync("log", "-1", "--format=%an <%ae>|%s", "main")).Trim());
        Assert.Equal("one\n", await RemoteAsync("show", "main:notes.txt"));

        // A stale lease never overwrites the branch.
        await Assert.ThrowsAsync<GitException>(() =>
            _mirror.PushAsync(_remote.Root, "main", GitCredential.None, basis, head, default));
        await _mirror.DeleteDraftAsync(id, default);
        await _mirror.DeleteDraftAsync(id, default);
    }

    [Fact]
    public async Task A_moved_branch_is_merged_and_conflicts_are_resolved_in_the_draft()
    {
        var basis = await InitAsync();
        var id = Guid.NewGuid();
        await _mirror.CreateDraftAsync(id, basis, default);
        var head = await _mirror.WriteDraftAsync(id, basis, [new("notes.txt", "one\nTWO\nthree\n"), new("draft.txt", "d\n")], "edit", default);

        _source.Write("other.txt", "upstream");
        await CommitAndPushAsync("unrelated");
        var clean = await _mirror.PreparePublishAsync(_remote.Root, "main", GitCredential.None, basis, head, "Merge cleanly", Author, default);
        Assert.Empty(clean.Conflicts);
        Assert.NotEqual(basis, clean.Remote);
        Assert.Equal("upstream\n", await _git.RunAsync(["-C", Path.Combine(_data.Root, "git", "mirror.git"), "show", $"{clean.Commit}:other.txt"]));

        _source.Write("notes.txt", "one\n2\nthree");
        await CommitAndPushAsync("conflicting");
        var blocked = await _mirror.PreparePublishAsync(_remote.Root, "main", GitCredential.None, basis, head, "Conflict", Author, default);
        Assert.Equal(["notes.txt"], blocked.Conflicts);
        Assert.Null(blocked.Commit);

        var updated = await _mirror.UpdateDraftAsync(id, head, basis, blocked.Remote, default);
        Assert.Equal(["notes.txt"], updated.Conflicts);
        var marked = (await _mirror.ReadFileAsync(updated.Head, "notes.txt", default))!.Text!;
        Assert.True(RepositoryEditor.HasConflictMarkers(marked));
        Assert.Equal([new("draft.txt", "added"), new("notes.txt", "modified")],
            (await _mirror.ChangesAsync(blocked.Remote, updated.Head, default)).OrderBy(c => c.Path, StringComparer.Ordinal));

        var resolved = await _mirror.WriteDraftAsync(id, updated.Head, [new("notes.txt", "one\nTWO and 2\nthree\n")], "resolve", default);
        var candidate = await _mirror.PreparePublishAsync(_remote.Root, "main", GitCredential.None, blocked.Remote, resolved, "Resolved", Author, default);
        Assert.Empty(candidate.Conflicts);
        await _mirror.PushAsync(_remote.Root, "main", GitCredential.None, candidate.Remote, candidate.Commit!, default);
        Assert.Equal("one\nTWO and 2\nthree\n", await RemoteAsync("show", "main:notes.txt"));
        Assert.Equal("upstream\n", await RemoteAsync("show", "main:other.txt"));
        Assert.Equal("d\n", await RemoteAsync("show", "main:draft.txt"));
    }

    [Fact]
    public async Task Binary_files_are_read_but_not_offered_as_text()
    {
        var basis = await InitAsync();
        await File.WriteAllBytesAsync(Path.Combine(_source.Root, "image.bin"), [0x89, 0x50, 0x00, 0xff]);
        await CommitAndPushAsync("binary");
        var head = await _mirror.FetchAsync(_remote.Root, "main", GitCredential.None, default);
        var file = await _mirror.ReadFileAsync(head, "image.bin", default);
        Assert.NotNull(file);
        Assert.Null(file.Text);
        Assert.NotEqual(basis, head);
    }
}
