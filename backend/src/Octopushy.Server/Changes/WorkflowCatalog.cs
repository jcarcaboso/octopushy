using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Git;

namespace Octopushy.Server.Changes;

/// <summary>
/// One version of a workflow as a form needs it: what it manages, its inputs schema and its <c>ui</c> hints, served as they
/// are written. <see cref="Usable"/> is false for a release that is never resolved to, and <see cref="Issue"/> says why.
/// </summary>
public sealed record WorkflowVersionDto(
    string Id,
    string Version,
    string? Commit,
    bool Usable,
    string? Issue,
    string? Manages,
    string? Description,
    JsonNode? Inputs,
    JsonNode? Ui,
    IReadOnlyList<string> Secrets);

/// <summary>A workflow and its versions, newest first.</summary>
public sealed record WorkflowSummaryDto(string Id, string? Manages, string? Description, IReadOnlyList<WorkflowVersionDto> Versions);

/// <summary>
/// The workflows guests and services can name: from <c>local</c>, the lab repository's own <c>workflows/</c> at the head
/// revision, or from <c>repository</c>, the releases of the workflows repository the head names.
/// </summary>
public sealed record WorkflowCatalogDto(string Source, string? Repository, IReadOnlyList<WorkflowSummaryDto> Workflows);

/// <summary>
/// Reads workflows for forms: each release's <c>workflow.yaml</c> and inputs schema at the commit it recorded. A release
/// never changes, so what is read is kept.
/// </summary>
public sealed class WorkflowCatalog(RevisionCache revisions, WorkflowsMirrors mirrors)
{
    private readonly ConcurrentDictionary<(string Repository, string Commit, string Id), WorkflowDefinition?> _read = new();

    public async Task<WorkflowCatalogDto> ListAsync(OctopushyDb db, string? id, CancellationToken ct)
    {
        var head = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(ct);
        var settings = await db.RepositorySettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct);
        var url = head?.WorkflowsRepository ?? settings?.WorkflowsRepository;
        if (url is null)
        {
            LabRepository? repository = null;
            if (head is not null)
            {
                try { repository = await revisions.GetAsync(head, ct); }
                catch (GitException) { }
            }

            var local = (repository?.Workflows ?? []).Where(w => id is null || w.Id == id).GroupBy(w => w.Id).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => Summary(g.Key, g.OrderByDescending(w => w.Version)
                    .Select(w => Version(w, head!.Commit, w.Valid ? null : "The workflow has errors: see the revision's issues.")).ToList()))
                .ToList();
            return new WorkflowCatalogDto("local", null, local);
        }

        var records = await db.WorkflowReleases.AsNoTracking().Where(r => r.Repository == url && (id == null || r.WorkflowId == id)).ToListAsync(ct);
        var missing = await MissingAsync(url, records, ct);
        var workflows = new List<WorkflowSummaryDto>();
        foreach (var group in records.GroupBy(r => r.WorkflowId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var versions = new List<WorkflowVersionDto>();
            foreach (var record in group.OrderByDescending(r => r.Major).ThenByDescending(r => r.Minor))
                versions.Add(await ReleaseAsync(url, record, !missing.Contains(record.Commit), ct));
            workflows.Add(Summary(group.Key, versions));
        }

        return new WorkflowCatalogDto("repository", url, workflows);
    }

    /// <summary>The newest usable release of a major after <paramref name="reference"/>'s, offered as an upgrade; null when there is none.</summary>
    public async Task<WorkflowVersionDto?> NewerMajorAsync(OctopushyDb db, string? repository, WorkflowReference reference, CancellationToken ct)
    {
        if (repository is null)
            return null;
        var records = await db.WorkflowReleases.AsNoTracking()
            .Where(r => r.Repository == repository && r.WorkflowId == reference.Id && r.Major > reference.Major && r.Problem == null)
            .ToListAsync(ct);
        var missing = await MissingAsync(repository, records, ct);
        var newest = records.Where(r => !missing.Contains(r.Commit)).OrderByDescending(r => r.Major).ThenByDescending(r => r.Minor).FirstOrDefault();
        return newest is null ? null : await ReleaseAsync(repository, newest, true, ct);
    }

    /// <summary>A workflow already resolved by a revision, as a form shows it.</summary>
    public static WorkflowVersionDto Version(WorkflowDefinition workflow, string? commit, string? issue = null) => new(
        workflow.Id,
        workflow.Version.ToString(),
        workflow.Commit ?? commit,
        workflow.Valid && issue is null,
        issue,
        workflow.Manages,
        workflow.Description,
        workflow.SchemaJson?.DeepClone(),
        workflow.Ui?.DeepClone(),
        workflow.Secrets);

    private async Task<WorkflowVersionDto> ReleaseAsync(string repository, WorkflowReleaseRecord record, bool available, CancellationToken ct)
    {
        var issue = WorkflowReleaseBook.IssueOf(record, available);
        if (record.Problem is not null || !available)
            return new WorkflowVersionDto(record.WorkflowId, record.Version, record.Commit, false, issue, null, null, null, null, []);
        var workflow = await ReadAsync(repository, record.Commit, record.WorkflowId, ct);
        return workflow is null
            ? new WorkflowVersionDto(record.WorkflowId, record.Version, record.Commit, false,
                $"workflows/{record.WorkflowId}/workflow.yaml could not be read at {record.Commit[..10]}.", null, null, null, null, [])
            : Version(workflow, record.Commit, workflow.Valid ? null : issue ?? "The workflow has errors at this release.");
    }

    private async Task<WorkflowDefinition?> ReadAsync(string repository, string commit, string id, CancellationToken ct)
    {
        if (_read.TryGetValue((repository, commit, id), out var known))
            return known;
        string tree;
        try
        {
            tree = await mirrors.For(repository).SnapshotAsync(commit, ct);
        }
        catch (GitException)
        {
            return null;
        }

        var workflow = new WorkflowReader(tree).ReadOne(id);
        _read[(repository, commit, id)] = workflow;
        return workflow;
    }

    private async Task<IReadOnlySet<string>> MissingAsync(string repository, IReadOnlyList<WorkflowReleaseRecord> records, CancellationToken ct)
    {
        try
        {
            return await mirrors.For(repository).MissingCommitsAsync(records.Select(r => r.Commit), ct);
        }
        catch (GitException)
        {
            return records.Select(r => r.Commit).ToHashSet(StringComparer.Ordinal);
        }
    }

    private static WorkflowSummaryDto Summary(string id, IReadOnlyList<WorkflowVersionDto> versions)
    {
        var newest = versions.FirstOrDefault(v => v.Usable) ?? versions.FirstOrDefault();
        return new WorkflowSummaryDto(id, newest?.Manages, newest?.Description, versions);
    }
}
