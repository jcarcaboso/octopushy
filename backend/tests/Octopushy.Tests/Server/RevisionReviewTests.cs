using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Octopushy.Server.Git;
using Octopushy.Server.Options;

namespace Octopushy.Tests.Server;

public class RevisionReviewTests
{
    [Theory]
    [InlineData("2026-10-25T19:20:01+01:00")]
    [InlineData("2026-10-25T11:20:01-07:00")]
    [InlineData("2026-10-26T00:05:01+05:45")]
    [InlineData("2026-10-25T18:20:01+00:00")]
    public async Task Commit_metadata_normalizes_git_timezone_to_utc_without_changing_the_instant(string date)
    {
        using var repository = new TestRepository();
        using var data = new TestRepository();
        var git = new GitCli(NullLogger<GitCli>.Instance);
        await git.RunAsync(["init", "--quiet", "-b", "main"], repository.Root);
        await git.RunAsync(
            ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                "commit", "--quiet", "--allow-empty", "-m", "Offset fixture"],
            repository.Root,
            environment: new Dictionary<string, string> { ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date });
        var mirror = new RepositoryMirror(git, Options.Create(new OctopushyOptions { DataDirectory = data.Root }));
        var commit = await mirror.FetchAsync(repository.Root, "main", GitCredential.None, CancellationToken.None);

        var info = await mirror.CommitInfoAsync(commit, CancellationToken.None);

        Assert.Equal(commit, info.Commit);
        Assert.Equal(TimeSpan.Zero, info.CommittedAt.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 18, 20, 1, TimeSpan.Zero), info.CommittedAt);
    }

    [Fact]
    public async Task Same_tree_commits_bundle_literal_blobs_without_export_substitution_or_ignored_files_and_ignore_old_derived_caches()
    {
        using var repository = new TestRepository();
        using var data = new TestRepository();
        var git = new GitCli(NullLogger<GitCli>.Instance);
        await git.RunAsync(["init", "--quiet", "-b", "main"], repository.Root);
        repository.Write(".gitattributes", "helper.sh export-subst\nrequired.txt export-ignore\n");
        const string literal = "#!/bin/sh\n# $Format:%B$\nprintf 'literal fixture only\\n'\n";
        await File.WriteAllTextAsync(Path.Combine(repository.Root, "helper.sh"), literal);
        await File.WriteAllTextAsync(Path.Combine(repository.Root, "required.txt"), "This committed blob must not disappear.\n");
        var first = await CommitAsync(git, repository, "First message");
        await git.RunAsync(["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
            "commit", "--quiet", "--allow-empty", "-m", "Second message", "-m", "unreviewed message body"], repository.Root);
        var second = (await git.RunAsync(["rev-parse", "HEAD"], repository.Root)).Trim();
        var mirror = new RepositoryMirror(git, Options.Create(new OctopushyOptions { DataDirectory = data.Root }));
        await mirror.FetchAsync(repository.Root, "main", GitCredential.None, CancellationToken.None);
        Assert.Equal("", await mirror.ReviewDiffAsync(first, second, CancellationToken.None));
        var firstBundle = BundleFiles(await mirror.BundleAsync(first, CancellationToken.None));
        Assert.Equal(literal, firstBundle["helper.sh"]);
        Assert.Equal("This committed blob must not disappear.\n", firstBundle["required.txt"]);

        // A pre-upgrade snapshot/bundle must not carry old archive transformations forward.
        foreach (var suffix in new[] { "", "-literal-v2" })
        {
            var oldSnapshot = Path.Combine(data.Root, "git", "snapshots" + suffix, second);
            Directory.CreateDirectory(oldSnapshot);
            await File.WriteAllTextAsync(Path.Combine(oldSnapshot, "helper.sh"), "poisoned old converted content");
            var oldBundles = Path.Combine(data.Root, "git", "bundles" + suffix);
            Directory.CreateDirectory(oldBundles);
            await File.WriteAllTextAsync(Path.Combine(oldBundles, second + ".tar.gz"), "poisoned old bundle");
        }
        var secondBundle = BundleFiles(await mirror.BundleAsync(second, CancellationToken.None));
        Assert.Equal(literal, secondBundle["helper.sh"]);
        Assert.Equal(firstBundle["required.txt"], secondBundle["required.txt"]);
    }

    [Theory]
    [InlineData("text eol=crlf")]
    [InlineData("ident")]
    [InlineData("working-tree-encoding=UTF-16")]
    public async Task Bundle_preserves_literal_bytes_despite_nested_checkout_conversion_attributes(string attributes)
    {
        using var repository = new TestRepository();
        using var data = new TestRepository();
        var git = new GitCli(NullLogger<GitCli>.Instance);
        await git.RunAsync(["init", "--quiet", "-b", "main"], repository.Root);
        const string literal = "UTF-8 fixture: \u00e9\n$Id$\n";
        Directory.CreateDirectory(Path.Combine(repository.Root, "nested"));
        await File.WriteAllTextAsync(Path.Combine(repository.Root, "nested", "content.txt"), literal);
        await CommitAsync(git, repository, "Literal blob");
        // Add attributes separately: the fixture's committed blob must not itself be
        // normalized or decoded by git add while constructing the reproduction.
        repository.Write("nested/.gitattributes", $"content.txt {attributes}");
        await git.RunAsync(["add", "--", "nested/.gitattributes"], repository.Root);
        await git.RunAsync(["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
            "commit", "--quiet", "-m", "Checkout conversions"], repository.Root);
        var commit = (await git.RunAsync(["rev-parse", "HEAD"], repository.Root)).Trim();
        var mirror = new RepositoryMirror(git, Options.Create(new OctopushyOptions { DataDirectory = data.Root }));
        await mirror.FetchAsync(repository.Root, "main", GitCredential.None, CancellationToken.None);

        var files = BundleBytes(await mirror.BundleAsync(commit, CancellationToken.None));

        Assert.Equal(Encoding.UTF8.GetBytes(literal), files["nested/content.txt"]);
    }

    [Fact]
    public async Task Native_review_shows_exact_changes_without_running_external_diff_or_textconv()
    {
        using var repository = new TestRepository();
        using var data = new TestRepository();
        var git = new GitCli(NullLogger<GitCli>.Instance);
        await git.RunAsync(["init", "--quiet", "-b", "main"], repository.Root);
        repository.Write(".gitattributes", "*.txt diff=fixture\n");
        repository.Write("change.txt", "before\n");
        var first = await CommitAsync(git, repository, "First");
        repository.Write("change.txt", "after\n");
        repository.Write("names with spaces and <markup>.txt", "review this literally\n");
        var second = await CommitAsync(git, repository, "Second");
        var mirror = new RepositoryMirror(git, Options.Create(new OctopushyOptions { DataDirectory = data.Root }));
        await mirror.FetchAsync(repository.Root, "main", GitCredential.None, CancellationToken.None);
        var marker = Path.Combine(data.Root, "repository-code-ran");
        var mirrorPath = Path.Combine(data.Root, "git", "mirror.git");
        await git.RunAsync(["-C", mirrorPath, "config", "diff.fixture.command", $"touch {marker}"]);
        await git.RunAsync(["-C", mirrorPath, "config", "diff.fixture.textconv", $"touch {marker}"]);
        var diff = await mirror.ReviewDiffAsync(first, second, CancellationToken.None);
        Assert.Contains("-before", diff);
        Assert.Contains("+after", diff);
        Assert.Contains("names with spaces and <markup>.txt", diff);
        Assert.DoesNotContain(".gitattributes", diff);
        Assert.False(File.Exists(marker));
        var initial = await mirror.ReviewDiffAsync(null, first, CancellationToken.None);
        Assert.Contains("+before", initial);
        Assert.Contains(".gitattributes", initial);
        Assert.Equal("", await mirror.ReviewDiffAsync(second, second, CancellationToken.None));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Binary_payloads_are_included_as_complete_native_patches_and_obey_the_same_bound()
    {
        using var repository = new TestRepository();
        using var data = new TestRepository();
        var git = new GitCli(NullLogger<GitCli>.Instance);
        await git.RunAsync(["init", "--quiet", "-b", "main"], repository.Root);
        var payload = new byte[] { 0, 1, 2, 3, 4, 5, 6, 255 };
        await File.WriteAllBytesAsync(Path.Combine(repository.Root, "payload.bin"), payload);
        var first = await CommitAsync(git, repository, "Binary code");
        var mirror = new RepositoryMirror(git, Options.Create(new OctopushyOptions { DataDirectory = data.Root }));
        await mirror.FetchAsync(repository.Root, "main", GitCredential.None, CancellationToken.None);
        var patch = await mirror.ReviewDiffAsync(null, first, CancellationToken.None);
        Assert.Contains("GIT binary patch", patch);
        Assert.Contains("literal 8", patch);
        Assert.DoesNotContain("Binary files", patch);
        // Actually reconstruct the bytes from the reviewed patch rather than checking a marker alone.
        using var restored = new TestRepository();
        await git.RunAsync(["init", "--quiet"], restored.Root);
        var patchFile = Path.Combine(data.Root, "review.patch");
        await File.WriteAllTextAsync(patchFile, patch);
        await git.RunAsync(["apply", "--binary", "--", patchFile], restored.Root);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(restored.Root, "payload.bin")));
        var huge = System.Security.Cryptography.RandomNumberGenerator.GetBytes(600 * 1024);
        huge[0] = 0;
        await File.WriteAllBytesAsync(Path.Combine(repository.Root, "payload.bin"), huge);
        var second = await CommitAsync(git, repository, "Oversized binary code");
        await mirror.FetchAsync(repository.Root, "main", GitCredential.None, CancellationToken.None);
        await Assert.ThrowsAsync<ReviewTooLargeException>(() => mirror.ReviewDiffAsync(first, second, CancellationToken.None));
    }

    [Fact]
    public async Task Oversized_unavailable_and_unsafe_review_operands_fail_visibly()
    {
        using var repository = new TestRepository();
        using var data = new TestRepository();
        var git = new GitCli(NullLogger<GitCli>.Instance);
        await git.RunAsync(["init", "--quiet", "-b", "main"], repository.Root);
        repository.Write("huge.txt", new string('x', 600 * 1024));
        var commit = await CommitAsync(git, repository, "Large");
        var mirror = new RepositoryMirror(git, Options.Create(new OctopushyOptions { DataDirectory = data.Root }));
        await mirror.FetchAsync(repository.Root, "main", GitCredential.None, CancellationToken.None);
        await Assert.ThrowsAsync<ReviewTooLargeException>(() => mirror.ReviewDiffAsync(null, commit, CancellationToken.None));
        await Assert.ThrowsAsync<GitException>(() => mirror.ReviewDiffAsync(null, new string('0', 40), CancellationToken.None));
        await Assert.ThrowsAsync<GitException>(() => mirror.ReviewDiffAsync("--output=/tmp/unsafe", commit, CancellationToken.None));
        await Assert.ThrowsAsync<GitException>(() => mirror.ReviewDiffAsync(null, "--ext-diff", CancellationToken.None));
    }

    private static async Task<string> CommitAsync(GitCli git, TestRepository repository, string message)
    {
        await git.RunAsync(["add", "-A"], repository.Root);
        await git.RunAsync(["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", message], repository.Root);
        return (await git.RunAsync(["rev-parse", "HEAD"], repository.Root)).Trim();
    }

    private static Dictionary<string, string> BundleFiles(byte[] bundle) =>
        BundleBytes(bundle).ToDictionary(pair => pair.Key, pair => Encoding.UTF8.GetString(pair.Value));

    private static Dictionary<string, byte[]> BundleBytes(byte[] bundle)
    {
        using var gzip = new GZipStream(new MemoryStream(bundle), CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var files = new Dictionary<string, byte[]>();
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.DataStream is not null)
            {
                using var output = new MemoryStream();
                entry.DataStream.CopyTo(output);
                files[entry.Name] = output.ToArray();
            }
        }
        return files;
    }
}
