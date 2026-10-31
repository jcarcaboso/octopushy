using System.Text;

namespace Octopushy.Server.Git;

/// <summary>A file in a commit's tree: regular (100644), executable (100755), link (120000) or submodule (160000).</summary>
public sealed record TreeFile(string Path, string Mode, string Object, long? Size)
{
    public bool IsRegular => Mode is "100644" or "100755";
}

/// <summary>A file's text, or null text when it is binary, too large, or not a regular file.</summary>
public sealed record TreeFileContent(TreeFile File, string? Text);

/// <summary>New content for a path, or null content to delete it.</summary>
public sealed record DraftFileChange(string Path, string? Content);

/// <summary><c>added</c>, <c>modified</c> or <c>deleted</c>.</summary>
public sealed record ChangedPath(string Path, string Change);

public sealed record DraftUpdate(string Head, IReadOnlyList<string> Conflicts);

/// <summary>A publish candidate: <paramref name="Commit"/> on top of <paramref name="Remote"/>, or the conflicts that block it.</summary>
public sealed record PublishCandidate(string Remote, string? Commit, IReadOnlyList<string> Conflicts);

public sealed class DraftChangedException() : GitException("The draft changed since it was loaded. Reload it and make the edit again.");

/// <summary>Who a published commit is attributed to; the committer is always Octopushy.</summary>
public sealed record CommitAuthor(string Name, string Email);

public sealed partial class RepositoryMirror
{
    /// <summary>The largest file the editor reads or writes.</summary>
    public const int EditableFileLimit = 1024 * 1024;

    private static readonly IReadOnlySet<int> MergeConflicts = new HashSet<int> { 1 };

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Drafts live under their own namespace, so fetches with --prune never touch them and their
    // objects stay reachable until the draft is published or discarded.
    private static string DraftRef(Guid id) => $"refs/octopushy/drafts/{id:N}";

    /// <summary>Starts a draft at <paramref name="baseCommit"/>, which must already be fetched.</summary>
    public async Task CreateDraftAsync(Guid id, string baseCommit, CancellationToken ct)
    {
        CheckCommit(baseCommit);
        await _gate.WaitAsync(ct);
        try
        {
            await _git.RunAsync(["-C", _mirror, "rev-parse", "--verify", "--quiet", baseCommit + "^{commit}"], cancellationToken: ct);
            await _git.RunAsync(["-C", _mirror, "update-ref", DraftRef(id), baseCommit, new string('0', baseCommit.Length)], cancellationToken: ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteDraftAsync(Guid id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var (exists, _) = await _git.RunAcceptingAsync(["-C", _mirror, "rev-parse", "--verify", "--quiet", DraftRef(id)],
                MergeConflicts, cancellationToken: ct);
            if (exists == 0)
            {
                await _git.RunAsync(["-C", _mirror, "update-ref", "-d", DraftRef(id)], cancellationToken: ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Every file of <paramref name="commit"/>, ordered by path.</summary>
    public async Task<IReadOnlyList<TreeFile>> TreeAsync(string commit, CancellationToken ct)
    {
        CheckCommit(commit);
        var output = await _git.RunAsync(["-C", _mirror, "ls-tree", "-r", "-z", "--long", "--full-tree", commit], cancellationToken: ct);
        var files = new List<TreeFile>();
        foreach (var line in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            var fields = line[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            files.Add(new TreeFile(line[(tab + 1)..], fields[0], fields[2], long.TryParse(fields[3], out var size) ? size : null));
        }

        return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>A file of <paramref name="commit"/>, or null when it does not exist there.</summary>
    public async Task<TreeFileContent?> ReadFileAsync(string commit, string path, CancellationToken ct)
    {
        if (!IsSafePath(path))
        {
            throw new GitException("The path is not a valid repository path.");
        }

        var file = (await TreeAsync(commit, ct)).FirstOrDefault(f => f.Path == path);
        if (file is null)
        {
            return null;
        }

        if (!file.IsRegular || file.Size > EditableFileLimit)
        {
            return new(file, null);
        }

        var output = new MemoryStream();
        var error = await _git.RunToStreamAsync(["-C", _mirror, "cat-file", "blob", file.Object], output, cancellationToken: ct);
        if (error is not null)
        {
            throw new GitException(error);
        }

        try
        {
            var text = StrictUtf8.GetString(output.ToArray());
            return new(file, text.Contains('\0') ? null : text);
        }
        catch (DecoderFallbackException)
        {
            return new(file, null);
        }
    }

    /// <summary>The files that differ between two commits.</summary>
    public async Task<IReadOnlyList<ChangedPath>> ChangesAsync(string fromCommit, string toCommit, CancellationToken ct)
    {
        CheckCommit(fromCommit);
        CheckCommit(toCommit);
        var output = await _git.RunAsync(["-C", _mirror, "diff-tree", "-r", "-z", "--no-renames", "--no-commit-id", "--name-status",
            fromCommit, toCommit, "--"], cancellationToken: ct);
        var fields = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var changes = new List<ChangedPath>();
        for (var i = 0; i + 1 < fields.Length; i += 2)
        {
            changes.Add(new ChangedPath(fields[i + 1], fields[i] switch { "A" => "added", "D" => "deleted", _ => "modified" }));
        }

        return changes;
    }

    /// <summary>
    /// Records <paramref name="changes"/> as one commit on the draft, which must still be at
    /// <paramref name="expectedHead"/>. Existing files keep their mode; new files are 100644.
    /// Content is stored literally, without filters or line-ending conversion.
    /// </summary>
    public async Task<string> WriteDraftAsync(Guid id, string expectedHead, IReadOnlyList<DraftFileChange> changes, string message, CancellationToken ct)
    {
        CheckCommit(expectedHead);
        if (changes.Count is < 1 or > 64 || changes.Select(c => c.Path).Distinct(StringComparer.Ordinal).Count() != changes.Count)
        {
            throw new GitException("Send between 1 and 64 changes, each to a different path.");
        }

        foreach (var change in changes)
        {
            if (!IsSafePath(change.Path))
            {
                throw new GitException($"'{change.Path}' is not a valid repository path.");
            }

            if (change.Content is not null && StrictUtf8.GetByteCount(change.Content) > EditableFileLimit)
            {
                throw new GitException($"'{change.Path}' is larger than the editor's 1 MiB limit.");
            }
        }

        await _gate.WaitAsync(ct);
        var temporary = Path.Combine(_directory, "draft-" + Guid.NewGuid().ToString("N"));
        try
        {
            await CheckDraftHeadAsync(id, expectedHead, ct);
            var entries = (await TreeAsync(expectedHead, ct)).ToDictionary(f => f.Path, StringComparer.Ordinal);
            var deleted = changes.Where(c => c.Content is null).Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
            var remaining = entries.Keys.Where(p => !deleted.Contains(p)).ToList();
            var indexInfo = new StringBuilder();
            foreach (var change in changes)
            {
                entries.TryGetValue(change.Path, out var existing);
                if (existing is not null && !existing.IsRegular)
                {
                    throw new GitException($"'{change.Path}' is a symbolic link or submodule; the editor changes only regular files.");
                }

                if (change.Content is null)
                {
                    if (existing is null)
                    {
                        throw new GitException($"'{change.Path}' does not exist in the draft.");
                    }

                    indexInfo.Append($"0 {new string('0', existing.Object.Length)}\t{change.Path}\0");
                    continue;
                }

                if (existing is null && remaining.Any(p => p.StartsWith(change.Path + "/", StringComparison.Ordinal)
                    || change.Path.StartsWith(p + "/", StringComparison.Ordinal)))
                {
                    throw new GitException($"'{change.Path}' collides with an existing file or folder of the same name.");
                }

                var blob = (await _git.RunAsync(["-C", _mirror, "hash-object", "-w", "--no-filters", "--stdin"],
                    cancellationToken: ct, input: change.Content)).Trim();
                indexInfo.Append($"{existing?.Mode ?? "100644"} {blob}\t{change.Path}\0");
            }

            Directory.CreateDirectory(temporary);
            var environment = EditorEnvironment(temporary, null);
            await _git.RunAsync(["-C", _mirror, "read-tree", expectedHead], environment: environment, cancellationToken: ct);
            await _git.RunAsync(["-C", _mirror, "update-index", "-z", "--index-info"], environment: environment,
                cancellationToken: ct, input: indexInfo.ToString());
            var tree = (await _git.RunAsync(["-C", _mirror, "write-tree"], environment: environment, cancellationToken: ct)).Trim();
            if (tree == await TreeOfAsync(expectedHead, ct))
            {
                return expectedHead;
            }

            var commit = (await _git.RunAsync(["-C", _mirror, "commit-tree", tree, "-p", expectedHead, "-m", message],
                environment: environment, cancellationToken: ct)).Trim();
            await _git.RunAsync(["-C", _mirror, "update-ref", DraftRef(id), commit, expectedHead], cancellationToken: ct);
            return commit;
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, true);
            }

            _gate.Release();
        }
    }

    /// <summary>
    /// Moves the draft onto <paramref name="onto"/>. Files that conflict keep Git's conflict
    /// markers in the draft and are returned, so they can be resolved in the editor.
    /// </summary>
    public async Task<DraftUpdate> UpdateDraftAsync(Guid id, string expectedHead, string baseCommit, string onto, CancellationToken ct)
    {
        CheckCommit(expectedHead);
        CheckCommit(baseCommit);
        CheckCommit(onto);
        await _gate.WaitAsync(ct);
        try
        {
            await CheckDraftHeadAsync(id, expectedHead, ct);
            var (tree, conflicts) = await MergeAsync(baseCommit, onto, expectedHead, ct);
            var commit = (await _git.RunAsync(["-C", _mirror, "commit-tree", tree, "-p", onto, "-p", expectedHead,
                    "-m", $"Update draft to {onto[..12]}"],
                environment: EditorEnvironment(null, null), cancellationToken: ct)).Trim();
            await _git.RunAsync(["-C", _mirror, "update-ref", DraftRef(id), commit, expectedHead], cancellationToken: ct);
            return new(commit, conflicts);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Fetches the branch and builds the single commit that publishes the draft on top of it,
    /// merging when the branch has moved since <paramref name="baseCommit"/>. Nothing is pushed.
    /// </summary>
    public async Task<PublishCandidate> PreparePublishAsync(string url, string branch, GitCredential credential,
        string baseCommit, string draftHead, string message, CommitAuthor author, CancellationToken ct)
    {
        CheckCommit(baseCommit);
        CheckCommit(draftHead);
        var remote = await FetchAsync(url, branch, credential, ct);
        await _gate.WaitAsync(ct);
        try
        {
            var (tree, conflicts) = await MergeAsync(baseCommit, remote, draftHead, ct);
            if (conflicts.Count > 0)
            {
                return new(remote, null, conflicts);
            }

            if (tree == await TreeOfAsync(remote, ct))
            {
                throw new GitException("The draft makes no change to the current branch.");
            }

            var commit = (await _git.RunAsync(["-C", _mirror, "commit-tree", tree, "-p", remote, "-m", message],
                environment: EditorEnvironment(null, author), cancellationToken: ct)).Trim();
            return new(remote, commit, []);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Pushes <paramref name="commit"/> only if the branch is still at <paramref name="expectedRemote"/>; never forces.</summary>
    public async Task PushAsync(string url, string branch, GitCredential credential, string expectedRemote, string commit, CancellationToken ct)
    {
        ValidateUrl(url);
        CheckCommit(expectedRemote);
        CheckCommit(commit);
        if (!BranchPattern().IsMatch(branch))
        {
            throw new GitException($"'{branch}' is not a valid branch name.");
        }

        await _gate.WaitAsync(ct);
        try
        {
            var (environment, secrets, cleanup) = PrepareCredential(credential);
            try
            {
                await _git.RunAsync(["-C", _mirror, "push", "--quiet", "--no-verify", $"--force-with-lease=refs/heads/{branch}:{expectedRemote}",
                    url, $"{commit}:refs/heads/{branch}"], environment: environment, secrets: secrets, cancellationToken: ct);
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

    private async Task CheckDraftHeadAsync(Guid id, string expectedHead, CancellationToken ct)
    {
        var (exitCode, output) = await _git.RunAcceptingAsync(["-C", _mirror, "rev-parse", "--verify", "--quiet", DraftRef(id)],
            MergeConflicts, cancellationToken: ct);
        if (exitCode != 0 || output.Trim() != expectedHead)
        {
            throw new DraftChangedException();
        }
    }

    private async Task<string> TreeOfAsync(string commit, CancellationToken ct) =>
        (await _git.RunAsync(["-C", _mirror, "rev-parse", commit + "^{tree}"], cancellationToken: ct)).Trim();

    /// <summary>Merges <paramref name="theirs"/> into <paramref name="ours"/> without a working tree.</summary>
    private async Task<(string Tree, IReadOnlyList<string> Conflicts)> MergeAsync(string mergeBase, string ours, string theirs, CancellationToken ct)
    {
        if (ours == mergeBase)
        {
            return (await TreeOfAsync(theirs, ct), []);
        }

        var (exitCode, output) = await _git.RunAcceptingAsync(["-C", _mirror, "merge-tree", "--write-tree", "-z", "--name-only", "--no-messages",
            $"--merge-base={mergeBase}", ours, theirs], MergeConflicts, cancellationToken: ct);
        var fields = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var conflicts = exitCode == 0 ? [] : fields.Skip(1).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return (fields[0].Trim(), conflicts);
    }

    private static Dictionary<string, string> EditorEnvironment(string? indexDirectory, CommitAuthor? author)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_COMMITTER_NAME"] = "Octopushy editor",
            ["GIT_COMMITTER_EMAIL"] = "editor@octopushy.invalid",
            ["GIT_AUTHOR_NAME"] = author?.Name ?? "Octopushy editor",
            ["GIT_AUTHOR_EMAIL"] = author?.Email ?? "editor@octopushy.invalid",
        };
        if (indexDirectory is not null)
        {
            environment["GIT_INDEX_FILE"] = Path.Combine(indexDirectory, "index");
        }

        return environment;
    }
}
