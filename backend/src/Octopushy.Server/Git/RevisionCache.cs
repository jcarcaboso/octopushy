using System.Collections.Concurrent;
using System.Text.Json;
using Octopushy.Definitions;
using Octopushy.Server.Data;

namespace Octopushy.Server.Git;

/// <summary>
/// A config commit read with its workflows: what each reference resolved to, and the digest that, with the commit, is the
/// revision's identity. Workflows kept in the lab repository resolve to nothing outside it, so their digest is empty.
/// </summary>
public sealed record ResolvedRevision(LabRepository Repository, string? WorkflowsRepository, IReadOnlyList<ResolvedWorkflow> Resolutions, string Digest)
{
    public string ResolutionsJson => RevisionCache.Serialize(Resolutions);
}

/// <summary>
/// Parsed lab repositories by revision, read from their snapshots on demand. A revision is its config commit and what its
/// workflow references resolved to, so the same commit with other releases is another entry, and a revision is always read
/// again with the releases it recorded, never with newer ones.
/// </summary>
public sealed class RevisionCache(RepositoryMirror mirror, WorkflowsMirrors workflows)
{
    private const int Capacity = 8;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, (LabRepository Repository, long Used)> _entries = new(StringComparer.Ordinal);
    private long _clock;

    public static string Serialize(IReadOnlyList<ResolvedWorkflow> resolutions) => JsonSerializer.Serialize(resolutions, Json);

    public static IReadOnlyList<ResolvedWorkflow> Resolutions(string json) =>
        JsonSerializer.Deserialize<List<ResolvedWorkflow>>(json, Json) ?? [];

    /// <summary>A revision as it was recorded.</summary>
    public Task<LabRepository> GetAsync(DefinitionRevision revision, CancellationToken cancellationToken) =>
        LoadAsync(revision.Commit, revision.WorkflowsRepository, Resolutions(revision.Resolutions), revision.WorkflowsDigest, cancellationToken);

    /// <summary>
    /// A config commit, with its references resolved against the releases <paramref name="releasesOf"/> gives for the
    /// workflows repository its <c>octopushy.yaml</c> names, as a sync would read it now.
    /// </summary>
    public async Task<ResolvedRevision> ResolveAsync(string commit, Func<string, CancellationToken, Task<IReadOnlyList<WorkflowRelease>>> releasesOf,
        CancellationToken cancellationToken)
    {
        var snapshot = await mirror.SnapshotAsync(commit, cancellationToken);
        if (WorkflowsSetting.Read(snapshot).Repository is not { } repository)
            return new(await LoadAsync(commit, null, [], "", cancellationToken), null, [], "");
        var resolutions = WorkflowResolver.ResolveAll(WorkflowReferences.In(snapshot), await releasesOf(repository, cancellationToken), repository);
        var digest = WorkflowResolver.Digest(repository, resolutions);
        return new(await LoadAsync(commit, repository, resolutions, digest, cancellationToken), repository, resolutions, digest);
    }

    private async Task<LabRepository> LoadAsync(string commit, string? repository, IReadOnlyList<ResolvedWorkflow> resolutions, string digest,
        CancellationToken cancellationToken)
    {
        var key = $"{commit}|{digest}";
        if (_entries.TryGetValue(key, out var cached))
        {
            _entries[key] = (cached.Repository, Interlocked.Increment(ref _clock));
            return cached.Repository;
        }

        var snapshot = await mirror.SnapshotAsync(commit, cancellationToken);
        var complete = true;
        LabRepository loaded;
        if (repository is null)
        {
            loaded = LabRepositoryLoader.Load(snapshot);
        }
        else
        {
            var mirrored = workflows.For(repository);
            var trees = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var release in resolutions.Where(r => r.Problem is null).Select(r => r.Commit!).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    trees[release] = await mirrored.SnapshotAsync(release, cancellationToken);
                }
                catch (GitException)
                {
                    // Read as it is, saying which release could not be read, and not kept: it may be readable next time.
                    complete = false;
                }
            }

            loaded = LabRepositoryLoader.Load(snapshot, new GitTagWorkflowSource(repository, resolutions, trees));
        }

        if (!complete)
            return loaded;
        _entries[key] = (loaded, Interlocked.Increment(ref _clock));
        while (_entries.Count > Capacity)
        {
            var oldest = _entries.MinBy(e => e.Value.Used).Key;
            _entries.TryRemove(oldest, out _);
        }

        return loaded;
    }
}
