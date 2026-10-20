using System.Collections.Concurrent;
using Octopushy.Definitions;

namespace Octopushy.Server.Git;

/// <summary>Parsed lab repositories by commit, read from their snapshots on demand.</summary>
public sealed class RevisionCache(RepositoryMirror mirror)
{
    private const int Capacity = 8;

    private readonly ConcurrentDictionary<string, (LabRepository Repository, long Used)> _entries = new(StringComparer.Ordinal);
    private long _clock;

    public async Task<LabRepository> GetAsync(string commit, CancellationToken cancellationToken)
    {
        if (_entries.TryGetValue(commit, out var cached))
        {
            _entries[commit] = (cached.Repository, Interlocked.Increment(ref _clock));
            return cached.Repository;
        }

        var snapshot = await mirror.SnapshotAsync(commit, cancellationToken);
        var repository = LabRepositoryLoader.Load(snapshot);
        _entries[commit] = (repository, Interlocked.Increment(ref _clock));
        while (_entries.Count > Capacity)
        {
            var oldest = _entries.MinBy(e => e.Value.Used).Key;
            _entries.TryRemove(oldest, out _);
        }

        return repository;
    }
}
