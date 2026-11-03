using System.Formats.Tar;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Server.Options;

namespace Octopushy.Server.Git;

public sealed record CommitInfo(string Commit, string Subject, string Author, DateTimeOffset CommittedAt);

/// <summary>How to authenticate to the lab repository's remote.</summary>
public sealed record GitCredential(string Kind, string? Username, string? Secret)
{
    public static readonly GitCredential None = new("none", null, null);
}

/// <summary>A tag of a repository and the commit it points to now.</summary>
public sealed record TagTarget(string Name, string Commit);

/// <summary>
/// Octopushy's clone of a repository: a bare mirror it fetches into, plus one extracted snapshot directory per commit it has
/// read. The lab repository's mirror also keeps the bundles sent to agents; a workflows repository has a mirror of its own,
/// next to it, from <see cref="WorkflowsMirrors"/>.
/// </summary>
public sealed partial class RepositoryMirror
{
    private readonly GitCli _git;
    private readonly string _directory;
    private readonly string _mirror;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RepositoryMirror(GitCli git, IOptions<OctopushyOptions> options)
        : this(git, Path.Combine(options.Value.DataDirectory, "git"), 0)
    {
    }

    private RepositoryMirror(GitCli git, string directory, int _)
    {
        _git = git;
        _directory = directory;
        _mirror = Path.Combine(_directory, "mirror.git");
    }

    /// <summary>A mirror kept in <paramref name="directory"/>, such as a workflows repository's.</summary>
    public static RepositoryMirror At(GitCli git, string directory) => new(git, directory, 0);

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

    /// <summary>
    /// Fetches every branch's objects and every tag, and returns where each tag points now. Tags that moved are fetched as
    /// they are; what a release is stays in Octopushy's records, and <see cref="PinAsync"/> keeps its commit here.
    /// </summary>
    public async Task<IReadOnlyList<TagTarget>> FetchTagsAsync(string url, GitCredential credential, CancellationToken cancellationToken)
    {
        ValidateUrl(url);
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
                await _git.RunAsync(
                    ["-C", _mirror, "fetch", "--quiet", "--prune", "--no-tags", "--force", url,
                        "+refs/heads/*:refs/remotes/origin/*", "+refs/tags/*:refs/tags/*"],
                    environment: environment,
                    secrets: secrets,
                    cancellationToken: cancellationToken);
            }
            finally
            {
                cleanup();
            }

            var output = await _git.RunAsync(
                ["-C", _mirror, "for-each-ref", "--format=%(refname:strip=2)%00%(objecttype)%00%(objectname)%00%(*objecttype)%00%(*objectname)", "refs/tags"],
                cancellationToken: cancellationToken);
            var tags = new List<TagTarget>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\0');
                // An annotated tag names a tag object; the commit is what it peels to.
                var (type, commit) = parts[3].Length > 0 ? (parts[3], parts[4]) : (parts[1], parts[2]);
                if (type == "commit")
                    tags.Add(new TagTarget(parts[0], commit));
            }

            return tags;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Keeps each release's commit in the mirror under a ref of Octopushy's own, <c>refs/octopushy/releases/&lt;tag&gt;</c>,
    /// whatever happens to the tag it came from. Fetches prune only <c>refs/tags</c> and <c>refs/remotes</c>.
    /// </summary>
    public async Task PinAsync(IReadOnlyList<(string Name, string Commit)> releases, CancellationToken cancellationToken)
    {
        foreach (var (name, commit) in releases)
        {
            CheckCommit(commit);
            if (!PinPattern().IsMatch(name))
                throw new GitException($"'{name}' is not a release name.");
        }

        if (releases.Count == 0)
            return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _git.RunAsync(["-C", _mirror, "update-ref", "--stdin"], cancellationToken: cancellationToken,
                input: string.Concat(releases.Select(r => $"update refs/octopushy/releases/{r.Name} {r.Commit}\n")));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Which of <paramref name="commits"/> the mirror does not hold.</summary>
    public async Task<IReadOnlySet<string>> MissingCommitsAsync(IEnumerable<string> commits, CancellationToken cancellationToken)
    {
        var wanted = commits.Distinct(StringComparer.Ordinal).ToList();
        foreach (var commit in wanted)
        {
            CheckCommit(commit);
        }

        if (wanted.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(_mirror))
            return wanted.ToHashSet(StringComparer.Ordinal);
        var output = await _git.RunAsync(["-C", _mirror, "cat-file", "--batch-check=%(objectname) %(objecttype)"],
            cancellationToken: cancellationToken, input: string.Join('\n', wanted.Select(c => c + "^{commit}")) + "\n");
        var present = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(' '))
            .Where(parts => parts.Length == 2 && parts[1] == "commit")
            .Select(parts => parts[0])
            .ToHashSet(StringComparer.Ordinal);
        return wanted.Where(c => !present.Contains(c)).ToHashSet(StringComparer.Ordinal);
    }

    public async Task<CommitInfo> CommitInfoAsync(string commit, CancellationToken cancellationToken)
    {
        CheckCommit(commit);
        var text = await _git.RunAsync(
            ["-C", _mirror, "log", "-1", "--format=%H%x00%s%x00%an%x00%cI", commit, "--"],
            cancellationToken: cancellationToken);
        var parts = text.TrimEnd('\n').Split('\0');
        return new CommitInfo(parts[0], parts[1], parts[2], DateTimeOffset.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime());
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
                SnapshotArchive.LiteralAttributes,
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

    /// <summary>What changed between two commits under some paths, as <c>M path</c>, at most 200 lines. Empty when either commit is gone.</summary>
    public async Task<IReadOnlyList<string>> ChangedFilesAsync(string from, string to, IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        try
        {
            var output = await _git.RunAsync(["-C", _mirror, "diff", "--name-status", "--no-renames", from, to, "--", .. paths],
                cancellationToken: cancellationToken);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Replace('\t', ' ')).Take(200).ToList();
        }
        catch (GitException)
        {
            return [];
        }
    }

    /// <summary>The folders of a workflows repository that a bundle takes, and the lab repository's bundle leaves out.</summary>
    public static readonly string[] WorkflowFolders = ["workflows", "modules"];

    /// <summary>The name a bundle is cached under: the commit, or with workflows from another repository, both commits.</summary>
    public static string BundleKey(string commit, string? workflowsCommit) => workflowsCommit is null ? commit : $"{commit}+{workflowsCommit}";

    /// <summary>
    /// The snapshot as a gzip tar for agents, without symbolic links or anything outside it. With
    /// <paramref name="workflowsSnapshot"/>, the files of a workflows repository's commit, its <c>workflows/</c> and
    /// <c>modules/</c> join the lab repository's files at the root, in place of any of the lab repository's own: every
    /// path stays what it is in its repository. Cached per commit, or per pair of commits.
    /// </summary>
    public async Task<byte[]> BundleAsync(string commit, CancellationToken cancellationToken, string? workflowsSnapshot = null, string? workflowsCommit = null)
    {
        if ((workflowsSnapshot is null) != (workflowsCommit is null))
            throw new ArgumentException("A workflows snapshot comes with its commit.");
        if (workflowsCommit is not null)
            CheckCommit(workflowsCommit);
        var snapshot = await SnapshotAsync(commit, cancellationToken);
        var path = Path.Combine(BundlesDirectory, BundleKey(commit, workflowsCommit) + ".tar.gz");
        if (File.Exists(path))
        {
            return await File.ReadAllBytesAsync(path, cancellationToken);
        }

        static bool InWorkflowFolders(string name) => WorkflowFolders.Any(f => name.StartsWith(f + "/", StringComparison.Ordinal));
        var files = Directory.EnumerateFiles(snapshot, "*", SearchOption.AllDirectories)
            .Select(f => (Name: Path.GetRelativePath(snapshot, f).Replace(Path.DirectorySeparatorChar, '/'), Full: f))
            .Where(f => workflowsSnapshot is null || !InWorkflowFolders(f.Name))
            .Concat(workflowsSnapshot is null ? [] : Directory.EnumerateFiles(workflowsSnapshot, "*", SearchOption.AllDirectories)
                .Select(f => (Name: Path.GetRelativePath(workflowsSnapshot, f).Replace(Path.DirectorySeparatorChar, '/'), Full: f))
                .Where(f => InWorkflowFolders(f.Name)))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
        Directory.CreateDirectory(BundlesDirectory);
        var buffer = new MemoryStream();
        await using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, file) in files)
            {
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

    /// <summary>
    /// Removes snapshots of commits not in <paramref name="keep"/>, and bundles whose key, from <see cref="BundleKey"/>, is not
    /// in <paramref name="keepBundles"/>, or by default whose commit is not in <paramref name="keep"/>.
    /// </summary>
    public void Prune(IReadOnlySet<string> keep, IReadOnlySet<string>? keepBundles = null)
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
            var key = Path.GetFileName(file).Split('.')[0];
            if (!(keepBundles?.Contains(key) ?? keep.Contains(key)))
            {
                File.Delete(file);
            }
        }
    }

    internal static void ExtractSafely(Stream tarStream, string destination)
    {
        try
        {
            SnapshotArchive.Extract(tarStream, destination);
        }
        catch (InvalidDataException e)
        {
            throw new GitException(e.Message);
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

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?/v[0-9]+\\.[0-9]+$")]
    private static partial Regex PinPattern();
}
