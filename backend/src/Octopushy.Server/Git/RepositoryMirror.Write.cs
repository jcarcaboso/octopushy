using System.Text;

namespace Octopushy.Server.Git;

public sealed record RepositoryWriteResult(string Commit, bool AlreadyPresent);

public sealed partial class RepositoryMirror
{
    /// <summary>Add literal files using native Git objects and a remote expected-head CAS.
    /// No working tree, filters, hooks, shell or overwrite.</summary>
    public async Task<RepositoryWriteResult> AddFilesAsync(
        string url, string branch, string expectedCommit, IReadOnlyDictionary<string, string> files,
        GitCredential credential, CancellationToken ct)
    {
        ValidateUrl(url);
        CheckCommit(expectedCommit);
        if (!BranchPattern().IsMatch(branch) || files.Count is < 1 or > 32
            || files.Sum(f => Encoding.UTF8.GetByteCount(f.Value)) > 512 * 1024)
            throw new GitException("The repository addition is invalid or exceeds the bounded review limit.");
        if (files.Keys.Any(path => !IsSafePath(path)))
            throw new GitException("A generated repository path is invalid.");

        await _gate.WaitAsync(ct);
        var temporary = Path.Combine(_directory, "write-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporary);
            var (environment, secrets, cleanup) = PrepareCredential(credential);
            environment["GIT_INDEX_FILE"] = Path.Combine(temporary, "index");
            environment["GIT_AUTHOR_NAME"] = environment["GIT_COMMITTER_NAME"] = "Octopushy worker wizard";
            environment["GIT_AUTHOR_EMAIL"] = environment["GIT_COMMITTER_EMAIL"] = "worker-wizard@octopushy.invalid";
            try
            {
                var remoteRef = $"refs/remotes/origin/{branch}";
                await _git.RunAsync(["-C", _mirror, "fetch", "--quiet", "--no-tags", "--force",
                    url, $"+refs/heads/{branch}:{remoteRef}"], environment: environment, secrets: secrets, cancellationToken: ct);
                var current = (await _git.RunAsync(["-C", _mirror, "rev-parse", remoteRef + "^{commit}"],
                    cancellationToken: ct)).Trim();
                var entries = (await _git.RunAsync(["-C", _mirror, "ls-tree", "-rz", current],
                    cancellationToken: ct)).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Split('\t', 2))
                    .ToDictionary(parts => parts[1], parts => parts[0].Split(' '), StringComparer.Ordinal);
                var blobs = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (path, content) in files)
                {
                    var file = Path.Combine(temporary, "blob");
                    await File.WriteAllTextAsync(file, content, new UTF8Encoding(false, true), ct);
                    var blob = (await _git.RunAsync(["-C", _mirror, "hash-object", "-w", "--no-filters", "--", file],
                        cancellationToken: ct)).Trim();
                    blobs.Add(path, blob);
                    if (entries.TryGetValue(path, out var entry) && (entry[0] != "100644" || entry[2] != blob)
                        || entries.Keys.Any(existing => path.StartsWith(existing + "/", StringComparison.Ordinal)
                            || existing.StartsWith(path + "/", StringComparison.Ordinal)))
                        throw new GitException($"The repository already contains conflicting content at '{path}'. Nothing was overwritten.");
                }
                // Reconcile a lost successful push response without creating another VM definition.
                if (blobs.All(f => entries.TryGetValue(f.Key, out var entry) && entry[0] == "100644" && entry[2] == f.Value))
                    return new(current, true);
                if (current != expectedCommit)
                    throw new GitException("The repository branch changed. Sync and review the worker definition again.");

                await _git.RunAsync(["-C", _mirror, "read-tree", current], environment: environment, cancellationToken: ct);
                foreach (var (path, blob) in blobs)
                    await _git.RunAsync(["-C", _mirror, "update-index", "--add", "--cacheinfo", $"100644,{blob},{path}"],
                        environment: environment, cancellationToken: ct);
                var tree = (await _git.RunAsync(["-C", _mirror, "write-tree"], environment: environment, cancellationToken: ct)).Trim();
                var commit = (await _git.RunAsync(["-C", _mirror, "commit-tree", tree, "-p", current,
                    "-m", "Add managed NixOS worker (template v1)"], environment: environment, cancellationToken: ct)).Trim();
                // The expected value is explicit; another writer cannot be overwritten.
                await _git.RunAsync(["-C", _mirror, "push", "--quiet", $"--force-with-lease=refs/heads/{branch}:{current}",
                    url, $"{commit}:refs/heads/{branch}"], environment: environment, secrets: secrets, cancellationToken: ct);
                return new(commit, false);
            }
            finally { cleanup(); }
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
            _gate.Release();
        }
    }

    /// <summary>A relative path with no empty, dot or <c>.git</c> segments, control characters, backslashes or colons.</summary>
    internal static bool IsSafePath(string path) =>
        path.Length is > 0 and <= 512 && !path.Any(c => char.IsControl(c) || c is '\\' or ':')
        && !path.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.Equals(".git", StringComparison.OrdinalIgnoreCase));
}
