using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Octopushy.Server.Git;
using Octopushy.Server.Options;

namespace Octopushy.Tests.Server;

/// <summary>A commit over the snapshot limits is not read, and says which limit; the mirror keeps working.</summary>
public sealed class SnapshotLimitTests : IDisposable
{
    private readonly TestRepository _source = new();
    private readonly TestRepository _data = new();
    private readonly GitCli _git = new(NullLogger<GitCli>.Instance);

    public void Dispose()
    {
        _source.Dispose();
        _data.Dispose();
    }

    private RepositoryMirror Mirror(SnapshotOptions limits) =>
        new(_git, Options.Create(new OctopushyOptions { DataDirectory = _data.Root, Snapshots = limits }));

    private async Task<string> CommitAsync(string message)
    {
        if (!Directory.Exists(Path.Combine(_source.Root, ".git")))
            await _git.RunAsync(["init", "--quiet", "-b", "main", _source.Root]);
        await _git.RunAsync(["add", "-A"], _source.Root);
        await _git.RunAsync(["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", message], _source.Root);
        return (await _git.RunAsync(["rev-parse", "HEAD"], _source.Root)).Trim();
    }

    private string Snapshots => Path.Combine(_data.Root, "git", "snapshots-literal-v3");

    [Fact]
    public async Task A_commit_with_too_many_files_is_refused_and_the_next_one_is_read()
    {
        for (var i = 0; i < 30; i++)
            _source.Write($"many/f{i}.txt", "x");
        var many = await CommitAsync("many");
        var mirror = Mirror(new SnapshotOptions { MaxEntries = 20 });
        await mirror.FetchAsync(_source.Root, "main", GitCredential.None, default);

        var error = await Assert.ThrowsAsync<GitException>(() => mirror.SnapshotAsync(many, default));
        Assert.Contains(many, error.Message);
        Assert.Contains("more than 20 entries", error.Message);
        Assert.Contains("Octopushy:Snapshots:MaxEntries", error.Message);
        // Nothing is left behind: no snapshot, no half-extracted staging directory, no archive.
        Assert.Empty(Directory.GetFileSystemEntries(Snapshots));

        Directory.Delete(Path.Combine(_source.Root, "many"), recursive: true);
        _source.Write("lab.yaml", "a: 1");
        var fewer = await CommitAsync("fewer");
        await mirror.FetchAsync(_source.Root, "main", GitCredential.None, default);
        Assert.True(File.Exists(Path.Combine(await mirror.SnapshotAsync(fewer, default), "lab.yaml")));
    }

    [Fact]
    public async Task A_commit_larger_than_its_archive_may_be_is_cut_off_while_git_writes_it()
    {
        _source.Write("big.bin", new string('x', 200_000));
        var big = await CommitAsync("big");
        // The archive may take 1000 bytes and one 2 KiB block of headers: Git's output is refused well before 200 KB.
        var mirror = Mirror(new SnapshotOptions { MaxTotalBytes = 1000, MaxFileBytes = 1000, MaxEntries = 1 });
        await mirror.FetchAsync(_source.Root, "main", GitCredential.None, default);

        var error = await Assert.ThrowsAsync<GitException>(() => mirror.SnapshotAsync(big, default));
        Assert.Contains("Octopushy:Snapshots:MaxTotalBytes", error.Message);
        Assert.Empty(Directory.GetFileSystemEntries(Snapshots));
    }

    [Fact]
    public async Task A_file_over_its_limit_is_named()
    {
        _source.Write("ok.yaml", "a: 1");
        _source.Write("images/disk.img", new string('x', 5000));
        var commit = await CommitAsync("image");
        var mirror = Mirror(new SnapshotOptions { MaxFileBytes = 4096 });
        await mirror.FetchAsync(_source.Root, "main", GitCredential.None, default);

        var error = await Assert.ThrowsAsync<GitException>(() => mirror.SnapshotAsync(commit, default));
        Assert.Contains("images/disk.img", error.Message);
        Assert.Contains("Octopushy:Snapshots:MaxFileBytes", error.Message);
    }
}
