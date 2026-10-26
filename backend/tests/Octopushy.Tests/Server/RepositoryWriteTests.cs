using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Octopushy.Server.Git;
using Octopushy.Server.Options;

namespace Octopushy.Tests.Server;

public class RepositoryWriteTests
{
    [Fact]
    public async Task Atomic_add_preserves_raw_bytes_existing_files_and_refuses_stale_or_conflicting_writes()
    {
        using var remote = new TestRepository();
        using var data = new TestRepository();
        var git = new GitCli(NullLogger<GitCli>.Instance);
        await git.RunAsync(["init", "--bare", "--quiet", "-b", "main", remote.Root]);
        using var source = new TestRepository();
        await git.RunAsync(["init", "--quiet", "-b", "main", source.Root]);
        source.Write("keep.txt", "untouched");
        source.Write(".gitattributes", "* text eol=crlf ident\n");
        await git.RunAsync(["add", "."], source.Root);
        await git.RunAsync(["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
            "commit", "--quiet", "-m", "base"], source.Root);
        await git.RunAsync(["push", "--quiet", remote.Root, "HEAD:refs/heads/main"], source.Root);
        var mirror = new RepositoryMirror(git, Options.Create(new OctopushyOptions { DataDirectory = data.Root }));
        var basis = await mirror.FetchAsync(remote.Root, "main", GitCredential.None, default);
        var files = new Dictionary<string, string> { ["guests/test.yaml"] = "literal $Id$\n" };
        var result = await mirror.AddFilesAsync(remote.Root, "main", basis, files, GitCredential.None, default);
        Assert.NotEqual(basis, result.Commit);
        Assert.False(result.AlreadyPresent);
        Assert.Equal("literal $Id$\n", await git.RunAsync(["show", $"{result.Commit}:guests/test.yaml"], remote.Root));
        Assert.Equal("untouched\n", await git.RunAsync(["show", $"{result.Commit}:keep.txt"], remote.Root));
        Assert.Equal(basis, (await git.RunAsync(["rev-parse", result.Commit + "^"], remote.Root)).Trim());
        var retry = await mirror.AddFilesAsync(remote.Root, "main", basis, files, GitCredential.None, default);
        Assert.True(retry.AlreadyPresent);
        Assert.Equal(result.Commit, retry.Commit);
        await Assert.ThrowsAsync<GitException>(() => mirror.AddFilesAsync(remote.Root, "main", basis,
            new Dictionary<string, string> { ["another.yaml"] = "stale" }, GitCredential.None, default));
        await Assert.ThrowsAsync<GitException>(() => mirror.AddFilesAsync(remote.Root, "main", result.Commit,
            new Dictionary<string, string> { ["keep.txt"] = "overwrite" }, GitCredential.None, default));
        await Assert.ThrowsAsync<GitException>(() => mirror.AddFilesAsync(remote.Root, "main", result.Commit,
            new Dictionary<string, string> { ["keep.txt/child"] = "file ancestor" }, GitCredential.None, default));
        Assert.Equal(result.Commit, (await git.RunAsync(["rev-parse", "main"], remote.Root)).Trim());
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData(".git/config")]
    [InlineData("ok/../escape")]
    [InlineData("line\nbreak")]
    [InlineData("path\\escape")]
    public async Task Unsafe_paths_are_rejected_before_remote_access(string path)
    {
        using var data = new TestRepository();
        var mirror = new RepositoryMirror(new GitCli(NullLogger<GitCli>.Instance),
            Options.Create(new OctopushyOptions { DataDirectory = data.Root }));
        await Assert.ThrowsAsync<GitException>(() => mirror.AddFilesAsync("/does-not-exist", "main",
            new string('a', 40), new Dictionary<string, string> { [path] = "bad" }, GitCredential.None, default));
    }
}
