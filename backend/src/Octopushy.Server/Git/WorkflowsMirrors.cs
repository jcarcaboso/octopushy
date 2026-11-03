using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Options;

namespace Octopushy.Server.Git;

/// <summary>
/// One bare mirror per workflows repository, next to the lab repository's: <c>git/workflows/&lt;id&gt;/mirror.git</c>, where the id
/// is derived from the repository's URL, so two repositories never share tags.
/// </summary>
public sealed class WorkflowsMirrors(GitCli git, IOptions<OctopushyOptions> options)
{
    private readonly ConcurrentDictionary<string, RepositoryMirror> _mirrors = new(StringComparer.Ordinal);

    public RepositoryMirror For(string repository) =>
        _mirrors.GetOrAdd(repository, url => RepositoryMirror.At(git, Path.Combine(options.Value.DataDirectory, "git", "workflows", Id(url))));

    public static string Id(string repository) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(repository)))[..16];
}

/// <summary>
/// The records of a workflows repository's releases. A tag <c>&lt;id&gt;/v&lt;M&gt;.&lt;m&gt;</c> becomes a release the first time it is seen,
/// at the commit it points to then, and that commit is the release for good:
/// <list type="bullet">
/// <item>a tag that later points elsewhere is recorded as moved, and not followed;</item>
/// <item>a deleted tag stays a release while its commit is in the mirror, where a ref of Octopushy's own keeps it;</item>
/// <item>a commit whose <c>workflow.yaml</c> does not declare the tag's version is recorded as an invalid release, and never resolved to.</item>
/// </list>
/// </summary>
public static class WorkflowReleaseBook
{
    /// <summary>Records what <paramref name="tags"/> show, fetched just now from <paramref name="repository"/> into <paramref name="mirror"/>.</summary>
    public static async Task RecordAsync(OctopushyDb db, string repository, RepositoryMirror mirror, IReadOnlyList<TagTarget> tags,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var records = await db.WorkflowReleases.Where(r => r.Repository == repository).ToListAsync(cancellationToken);
        var present = new HashSet<WorkflowReleaseRecord>();
        foreach (var target in tags)
        {
            if (WorkflowTag.Parse(target.Name) is not { } tag)
                continue;
            var record = records.Find(r => r.WorkflowId == tag.WorkflowId && r.Major == tag.Version.Major && r.Minor == tag.Version.Minor);
            if (record is null)
            {
                var file = await mirror.ReadFileAsync(target.Commit, tag.File, cancellationToken);
                record = new WorkflowReleaseRecord
                {
                    Repository = repository,
                    WorkflowId = tag.WorkflowId,
                    Major = tag.Version.Major,
                    Minor = tag.Version.Minor,
                    Commit = target.Commit,
                    FirstSeenAt = now,
                    Problem = file is { Text: null }
                        ? $"{tag.File} is not a text file Octopushy reads at the tagged commit."
                        : WorkflowResolver.CheckRelease(tag, file?.Text),
                };
                db.WorkflowReleases.Add(record);
                records.Add(record);
            }
            else if (target.Commit == record.Commit)
            {
                (record.MovedTo, record.MovedAt) = (null, null);
            }
            else if (record.MovedTo != target.Commit)
            {
                (record.MovedTo, record.MovedAt) = (target.Commit, now);
            }

            record.DeletedAt = null;
            present.Add(record);
        }

        foreach (var record in records.Where(r => !present.Contains(r)))
        {
            record.DeletedAt ??= now;
        }

        // Each release's own commit stays in the mirror whatever its tag does, as long as the mirror has it.
        var missing = await mirror.MissingCommitsAsync(records.Select(r => r.Commit), cancellationToken);
        await mirror.PinAsync(records.Where(r => !missing.Contains(r.Commit)).Select(r => (r.Tag, r.Commit)).ToList(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The recorded releases of <paramref name="repository"/>, as resolution takes them.</summary>
    public static async Task<IReadOnlyList<WorkflowRelease>> LoadAsync(OctopushyDb db, string repository, RepositoryMirror mirror, CancellationToken cancellationToken)
    {
        var records = await db.WorkflowReleases.AsNoTracking().Where(r => r.Repository == repository).ToListAsync(cancellationToken);
        var missing = await mirror.MissingCommitsAsync(records.Select(r => r.Commit), cancellationToken);
        return records.Select(r => new WorkflowRelease(r.WorkflowId, new WorkflowVersion(r.Major, r.Minor), r.Commit, r.Problem, !missing.Contains(r.Commit)))
            .ToList();
    }

    /// <summary>The recorded releases of any workflows repository, for <see cref="RevisionCache.ResolveAsync"/>.</summary>
    public static Func<string, CancellationToken, Task<IReadOnlyList<WorkflowRelease>>> Of(OctopushyDb db, WorkflowsMirrors mirrors) =>
        (repository, cancellationToken) => LoadAsync(db, repository, mirrors.For(repository), cancellationToken);

    /// <summary>What is wrong with a release, as the repository page shows it; null when nothing is.</summary>
    public static string? IssueOf(WorkflowReleaseRecord record, bool available)
    {
        if (record.Problem is { } problem)
            return $"The release {record.Tag} at {WorkflowResolver.Short(record.Commit)} is invalid and is never used: {problem}";
        if (record.MovedTo is { } moved)
            return $"{record.Tag} was moved from {WorkflowResolver.Short(record.Commit)} to {WorkflowResolver.Short(moved)}; the original commit is used. Moved tags are not followed.";
        if (!available)
            return $"{record.Tag} was deleted, and its commit {WorkflowResolver.Short(record.Commit)} is no longer in the mirror: references that resolve to it are invalid.";
        if (record.DeletedAt is not null)
            return $"{record.Tag} was deleted; its recorded commit {WorkflowResolver.Short(record.Commit)} is still used.";
        return null;
    }
}
