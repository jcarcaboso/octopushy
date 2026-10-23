using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Octopushy.Server.Options;

namespace Octopushy.Server.Git;

public sealed record CommitInfo(string Commit, string Subject, string Author, DateTimeOffset CommittedAt);

public sealed class ReviewTooLargeException() : GitException("The diff exceeds the 512 KiB review limit. Reduce the change and sync again.");

/// <summary>How to authenticate to the lab repository's remote.</summary>
public sealed record GitCredential(string Kind, string? Username, string? Secret)
{
    public static readonly GitCredential None = new("none", null, null);
}

/// <summary>
/// Octopushy's clone of the lab repository: a bare mirror it fetches into, plus one extracted
/// snapshot directory per commit it has read, and the bundle of that snapshot sent to agents.
/// </summary>
public sealed partial class RepositoryMirror
{
    private readonly GitCli _git;
    private readonly string _directory;
    private readonly string _mirror;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RepositoryMirror(GitCli git, IOptions<OctopushyOptions> options)
    {
        _git = git;
        _directory = Path.Combine(options.Value.DataDirectory, "git");
        _mirror = Path.Combine(_directory, "mirror.git");
    }

    // v3 also invalidates pre-publication v2 caches that honored checkout conversions.
    private string SnapshotsDirectory => Path.Combine(_directory, "snapshots-literal-v3");

    private string BundlesDirectory => Path.Combine(_directory, "bundles-literal-v3");

    /// <summary>Fetches the branch and returns the commit it points at.</summary>
    public async Task<string> FetchAsync(string url, string branch, GitCredential credential, CancellationToken cancellationToken)
    {
        ValidateUrl(url);
        if (!BranchPattern().IsMatch(branch))
        {
            throw new GitException($"'{branch}' is not a valid branch name.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(_mirror))
            {
                Directory.CreateDirectory(_directory);
                await _git.RunAsync(["init", "--bare", "--quiet", _mirror], cancellationToken: cancellationToken);
            }

            var (environment, secrets, cleanup) = PrepareCredential(credential);
            try
            {
                var remoteRef = $"refs/remotes/origin/{branch}";
                await _git.RunAsync(
                    ["-C", _mirror, "fetch", "--quiet", "--prune", "--no-tags", "--force", url, $"+refs/heads/{branch}:{remoteRef}"],
                    environment: environment,
                    secrets: secrets,
                    cancellationToken: cancellationToken);
                return (await _git.RunAsync(["-C", _mirror, "rev-parse", "--verify", remoteRef + "^{commit}"], cancellationToken: cancellationToken)).Trim();
            }
            finally
            {
                cleanup();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CommitInfo> CommitInfoAsync(string commit, CancellationToken cancellationToken)
    {
        CheckCommit(commit);
        var text = await _git.RunAsync(
            ["-C", _mirror, "log", "-1", "--format=%H%x00%s%x00%an%x00%cI", commit, "--"],
            cancellationToken: cancellationToken);
        var parts = text.TrimEnd('\n').Split('\0');
        return new CommitInfo(parts[0], parts[1], parts[2], DateTimeOffset.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>A bounded native Git diff; never invokes repository external diffs or text converters.</summary>
    public async Task<string> ReviewDiffAsync(string? baseCommit, string commit, CancellationToken cancellationToken)
    {
        CheckCommit(commit);
        if (baseCommit is not null)
        {
            CheckCommit(baseCommit);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            // Materialize the empty tree in this mirror, also supporting SHA-256 repositories.
            var basis = baseCommit ?? (await _git.RunAsync(
                ["-C", _mirror, "hash-object", "-w", "-t", "tree", "--", "/dev/null"], cancellationToken: timeout.Token)).Trim();
            using var output = new ReviewOutput();
            var error = await _git.RunToStreamAsync(
                ["-C", _mirror, "-c", "core.attributesFile=/dev/null", "-c", "diff.external=",
                    "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--no-renames", "--binary", "--full-index",
                    "--ignore-submodules=none", "--submodule=short",
                    "--src-prefix=a/", "--dst-prefix=b/", basis, commit, "--"],
                output, cancellationToken: timeout.Token);
            if (error is not null)
            {
                throw new GitException(error);
            }
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(output.ToArray());
        }
        catch (DecoderFallbackException)
        {
            throw new GitException("The review diff is not valid UTF-8. Normalize the source encoding before requesting trust.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitException("The review diff exceeded its 10 second time limit.");
        }
    }

    private sealed class ReviewOutput : MemoryStream
    {
        private void CheckLength(int count)
        {
            if (Length + count > 512 * 1024)
            {
                throw new ReviewTooLargeException();
            }
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckLength(count);
            base.Write(buffer, offset, count);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            CheckLength(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    /// <summary>The directory holding the files of <paramref name="commit"/>, extracted on first use.</summary>
    public async Task<string> SnapshotAsync(string commit, CancellationToken cancellationToken)
    {
        CheckCommit(commit);
        var target = Path.Combine(SnapshotsDirectory, commit);
        if (Directory.Exists(target))
        {
            return target;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Directory.Exists(target))
            {
                return target;
            }

            var staging = target + ".partial";
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            Directory.CreateDirectory(staging);
            // info/attributes outranks every repository .gitattributes file. The native
            // archive must materialize the literal reviewed blobs, not commit-message
            // substitutions, checkout conversions or an export-ignore subset.
            var attributes = Path.Combine(_mirror, "info", "attributes");
            Directory.CreateDirectory(Path.GetDirectoryName(attributes)!);
            await File.WriteAllTextAsync(attributes,
                "* -export-subst -export-ignore -text -eol -crlf -ident -filter -working-tree-encoding\n",
                cancellationToken);
            var archive = new MemoryStream();
            var error = await _git.RunToStreamAsync(["-C", _mirror, "archive", "--format=tar", commit], archive, cancellationToken: cancellationToken);
            if (error is not null)
            {
                throw new GitException(error);
            }

            archive.Position = 0;
            ExtractSafely(archive, staging);
            Directory.Move(staging, target);
            return target;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The snapshot as a gzip tar for agents, without symbolic links or anything outside it.
    /// Cached per commit.
    /// </summary>
    public async Task<byte[]> BundleAsync(string commit, CancellationToken cancellationToken)
    {
        var snapshot = await SnapshotAsync(commit, cancellationToken);
        var path = Path.Combine(BundlesDirectory, commit + ".tar.gz");
        if (File.Exists(path))
        {
            return await File.ReadAllBytesAsync(path, cancellationToken);
        }

        Directory.CreateDirectory(BundlesDirectory);
        var buffer = new MemoryStream();
        await using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(snapshot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var name = Path.GetRelativePath(snapshot, file).Replace(Path.DirectorySeparatorChar, '/');
                if (new FileInfo(file).LinkTarget is not null)
                {
                    continue;
                }

                var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                    ModificationTime = DateTimeOffset.UnixEpoch,
                    DataStream = File.OpenRead(file),
                };
                await tar.WriteEntryAsync(entry, cancellationToken);
                await entry.DataStream.DisposeAsync();
            }
        }

        var bytes = buffer.ToArray();
        var temporary = path + ".partial";
        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
        File.Move(temporary, path, overwrite: true);
        return bytes;
    }

    /// <summary>Removes snapshots and bundles of commits not in <paramref name="keep"/>.</summary>
    public void Prune(IReadOnlySet<string> keep)
    {
        foreach (var directory in Directory.Exists(SnapshotsDirectory) ? Directory.GetDirectories(SnapshotsDirectory) : [])
        {
            if (!keep.Contains(Path.GetFileName(directory)))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        foreach (var file in Directory.Exists(BundlesDirectory) ? Directory.GetFiles(BundlesDirectory) : [])
        {
            if (!keep.Contains(Path.GetFileName(file).Split('.')[0]))
            {
                File.Delete(file);
            }
        }
    }

    internal static void ExtractSafely(Stream tarStream, string destination)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var reader = new TarReader(tarStream);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes)
            {
                continue;
            }

            var full = Path.GetFullPath(Path.Combine(destination, entry.Name));
            if (!full.StartsWith(root, StringComparison.Ordinal) && full + Path.DirectorySeparatorChar != root)
            {
                throw new GitException($"The repository contains a path outside itself: {entry.Name}");
            }

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(full);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    entry.ExtractToFile(full, overwrite: false);
                    break;
                default:
                    // Symbolic links and other special entries are not extracted, so nothing in
                    // a snapshot can point outside it.
                    break;
            }
        }
    }

    private (Dictionary<string, string> Environment, List<string> Secrets, Action Cleanup) PrepareCredential(GitCredential credential)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var secrets = new List<string>();
        var files = new List<string>();
        switch (credential.Kind)
        {
            case "token" when credential.Secret is not null:
                {
                    var askPass = Path.Combine(_directory, "askpass.sh");
                    if (!File.Exists(askPass))
                    {
                        File.WriteAllText(askPass, "#!/bin/sh\ncase \"$1\" in\n  Username*) printf '%s' \"$OCTOPUSHY_GIT_USERNAME\" ;;\n  *) printf '%s' \"$OCTOPUSHY_GIT_PASSWORD\" ;;\nesac\n");
                        File.SetUnixFileMode(askPass, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                    }

                    environment["GIT_ASKPASS"] = askPass;
                    environment["OCTOPUSHY_GIT_USERNAME"] = credential.Username ?? "git";
                    environment["OCTOPUSHY_GIT_PASSWORD"] = credential.Secret;
                    secrets.Add(credential.Secret);
                    break;
                }
            case "ssh" when credential.Secret is not null:
                {
                    var keyFile = Path.Combine(_directory, $"ssh-key-{Guid.NewGuid():N}");
                    File.WriteAllText(keyFile, credential.Secret.ReplaceLineEndings("\n").TrimEnd('\n') + "\n");
                    File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    files.Add(keyFile);
                    var knownHosts = Path.Combine(_directory, "known_hosts");
                    environment["GIT_SSH_COMMAND"] =
                        $"ssh -i '{keyFile}' -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o UserKnownHostsFile='{knownHosts}'";
                    break;
                }
        }

        return (environment, secrets, () =>
        {
            foreach (var file in files)
            {
                File.Delete(file);
            }
        }
        );
    }

    private static void ValidateUrl(string url)
    {
        var ok = url.StartsWith("https://", StringComparison.Ordinal)
                 || url.StartsWith("http://", StringComparison.Ordinal)
                 || url.StartsWith("ssh://", StringComparison.Ordinal)
                 || url.StartsWith("file://", StringComparison.Ordinal)
                 || url.StartsWith('/')
                 || ScpLikeUrl().IsMatch(url);
        if (!ok || url.StartsWith('-'))
        {
            throw new GitException("The repository URL must be https://, ssh://, user@host:path, file://, or an absolute path.");
        }
    }

    private static void CheckCommit(string commit)
    {
        if (!CommitPattern().IsMatch(commit))
        {
            throw new GitException($"'{commit}' is not a commit id.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+@[A-Za-z0-9.-]+:[^-].*$")]
    private static partial Regex ScpLikeUrl();

    [GeneratedRegex("^(?!-)(?!.*\\.\\.)[A-Za-z0-9._/-]+$")]
    private static partial Regex BranchPattern();

    [GeneratedRegex("^[0-9a-f]{40}([0-9a-f]{24})?$")]
    private static partial Regex CommitPattern();
}
