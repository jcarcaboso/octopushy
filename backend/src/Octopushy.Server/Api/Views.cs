using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Definitions;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Api;

/// <summary>Guest statuses, derived from definitions, runs, and evidence rather than stored.</summary>
public static class GuestStatuses
{
    public const string InSync = "in-sync";
    public const string Pending = "pending";
    public const string Queued = "queued";
    public const string Running = "running";
    public const string AwaitingApproval = "awaiting-approval";
    public const string Blocked = "blocked";
    public const string Failed = "failed";
    public const string Uncertain = "uncertain";
    public const string Invalid = "invalid";
    public const string Missing = "missing";
    public const string Retired = "retired";
}

/// <summary>Builds the read models the API returns.</summary>
public sealed class Views(OctopushyDb db, RevisionCache revisions, RepositoryMirror mirror, AgentConnections connections)
{
    private const int DefinitionTextLimit = 256 * 1024;

    public static RunSummaryDto Summary(Run run, string address, string kind) => new(
        run.Id,
        run.Number,
        run.Operation,
        run.ResourceId,
        kind,
        address,
        run.Status,
        run.Detail,
        run.Reason,
        run.Commit,
        run.CreatedAt,
        run.StartedAt,
        run.FinishedAt,
        run.Steps.OrderBy(s => s.Ordinal).FirstOrDefault(s => !StepStatuses.IsDone(s.Status))?.Name,
        Waiting(run));

    public static RunSummaryDto Summary(Run run, (string Address, string Kind) resource) => Summary(run, resource.Address, resource.Kind);

    /// <summary>Why a run that has not finished is not making progress right now, or null when it is.</summary>
    public static string? Waiting(Run run)
    {
        if (run.Status == RunStatuses.Queued)
        {
            return run.Detail ?? "Waiting to start.";
        }

        if (run.Status != RunStatuses.Running)
        {
            return null;
        }

        var step = run.Steps.OrderBy(s => s.Ordinal).FirstOrDefault(s => !StepStatuses.IsDone(s.Status));
        if (step?.Status != StepStatuses.Queued)
        {
            return null;
        }

        return step.Attempts.Where(a => a.Status == AttemptStatuses.Queued).MaxBy(a => a.Number)?.WaitingReason ?? "Waiting to be assigned to an agent.";
    }

    /// <summary>The typed view of a plan step's outputs.</summary>
    public static PlanDto? Plan(Step step)
    {
        if (step.Name != StepNames.Plan || step.Outputs is null || JsonNode.Parse(step.Outputs) is not JsonObject outputs)
        {
            return null;
        }

        var counts = outputs["counts"];
        int Count(string name) => counts?[name]?.GetValue<int>() ?? 0;
        var changes = (outputs["changes"] as JsonArray)?.OfType<JsonObject>()
            .Select(c => new PlanChangeDto(
                c["address"]?.GetValue<string>() ?? "?",
                c["type"]?.GetValue<string>(),
                c["name"]?.GetValue<string>(),
                (c["actions"] as JsonArray)?.Select(a => a?.GetValue<string>() ?? "").ToList() ?? []))
            .ToList() ?? [];
        return new PlanDto(
            outputs["hasChanges"]?.GetValue<bool>() ?? changes.Count > 0,
            changes,
            new PlanCountsDto(Count("create"), Count("update"), Count("delete"), Count("replace")),
            outputs["planSha256"]?.GetValue<string>(),
            outputs["toolVersion"]?.GetValue<string>());
    }

    public static (string Status, string? Detail) GuestStatus(Resource resource, Run? active, Run? last)
    {
        if (resource.RetiredAt is not null)
        {
            return (GuestStatuses.Retired, null);
        }

        if (resource.MissingSince is not null)
        {
            return (GuestStatuses.Missing, "The folder disappeared without the guest being retired. Restore it, or retire the guest.");
        }

        if (!resource.Valid)
        {
            return (GuestStatuses.Invalid, "The definition has errors.");
        }

        if (active is not null && Waiting(active) is { } waiting)
        {
            return (GuestStatuses.Queued, waiting);
        }

        if (active is not null)
        {
            return (active.Status switch
            {
                RunStatuses.Queued => GuestStatuses.Queued,
                RunStatuses.AwaitingApproval => GuestStatuses.AwaitingApproval,
                RunStatuses.Blocked => GuestStatuses.Blocked,
                RunStatuses.Uncertain => GuestStatuses.Uncertain,
                _ => GuestStatuses.Running,
            }, active.Detail);
        }

        if (last is { Status: RunStatuses.Failed })
        {
            return (GuestStatuses.Failed, last.Detail);
        }

        return resource.SyncedInputsHash is not null && resource.SyncedInputsHash == resource.DesiredInputsHash
            ? (GuestStatuses.InSync, null)
            : (GuestStatuses.Pending, last is { Status: RunStatuses.Cancelled } ? last.Detail : "Not provisioned from the current definition yet.");
    }

    public async Task<RevisionDto?> LatestRevisionAsync(CancellationToken cancellationToken)
    {
        var revision = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
        return revision is null ? null : Revision(revision, true);
    }

    public static RevisionDto Revision(DefinitionRevision r, bool isCurrentSource) =>
        new(r.Id, r.Commit, r.Subject, r.Author, r.CommittedAt, r.LoadedAt, r.RepositoryValid, r.ErrorCount, r.WarningCount, r.TrustedAt, r.TrustedBy, isCurrentSource);

    public static IReadOnlyList<IssueDto> Issues(string json) =>
        JsonSerializer.Deserialize<List<DefinitionIssue>>(json, InventoryUpdater.IssueJson)?
            .Select(i => new IssueDto(i.Severity == IssueSeverity.Error ? "error" : "warning", i.File, i.Line, i.Message, i.Address))
            .ToList() ?? [];

    public async Task<InventoryDto> InventoryAsync(CancellationToken cancellationToken)
    {
        var resources = await db.Resources.AsNoTracking().Where(r => r.RetiredAt == null || r.Kind == ResourceKinds.Guest).ToListAsync(cancellationToken);
        var runs = await LatestRunsAsync(resources.Select(r => r.Id).ToList(), cancellationToken);
        var observations = await LatestObservationsAsync(cancellationToken);
        var labs = resources.Where(r => r.Kind == ResourceKinds.Lab && r.RetiredAt == null).OrderBy(r => r.Address)
            .Select(lab => new LabDto(
                lab.Id,
                lab.Address,
                LabName(lab),
                lab.Valid,
                Issues(lab.Issues),
                resources.Where(r => r.Kind == ResourceKinds.Installation && r.ParentAddress == lab.Address && r.RetiredAt == null)
                    .OrderBy(r => r.Address)
                    .Select(i => InstallationSummary(i, resources, runs, observations))
                    .ToList()))
            .ToList();
        return new InventoryDto(await LatestRevisionAsync(cancellationToken), labs);
    }

    public async Task<GuestDetailDto?> GuestAsync(Guid id, CancellationToken cancellationToken)
    {
        var resource = await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.Kind == ResourceKinds.Guest, cancellationToken);
        if (resource is null)
        {
            return null;
        }

        var runs = await db.Runs.AsNoTracking().Include(r => r.Steps).ThenInclude(s => s.Attempts)
            .Where(r => r.ResourceId == id)
            .OrderByDescending(r => r.CreatedAt)
            .Take(30)
            .ToListAsync(cancellationToken);
        var installation = await db.Resources.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Address == resource.ParentAddress && r.RetiredAt == null, cancellationToken);
        var observations = await LatestObservationsAsync(cancellationToken);
        var summary = GuestSummary(resource, runs, installation is null ? null : observations.GetValueOrDefault(installation.Id));

        var latest = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
        PolicyDto? policy = null;
        ResolvedSetting<IReadOnlyList<string>>? labels = null;
        string? text = null;
        if (latest is not null && resource.MissingSince is null)
        {
            var repository = await revisions.GetAsync(latest.Commit, cancellationToken);
            if (repository.Guest(resource.Address) is { } guest && repository.RepositoryValid)
            {
                var effective = PolicyResolver.For(repository, guest);
                policy = new PolicyDto(effective.Approval.Value, effective.Approval.SetBy, effective.Destructive.Value, effective.Destructive.SetBy);
                labels = PolicyResolver.AgentLabels(repository, guest);
            }

            text = await DefinitionTextAsync(latest.Commit, resource.DefinitionFile, cancellationToken);
        }

        var commits = await CommitsAsync([resource.DesiredRevisionId, resource.CheckedRevisionId, resource.AppliedRevisionId], cancellationToken);
        return new GuestDetailDto(
            summary,
            resource.DefinitionFile,
            text,
            Issues(resource.Issues),
            policy,
            labels?.Value ?? [],
            labels?.SetBy,
            resource.BoundVmid is { } vmid ? new BindingDto(resource.BoundInstallation!, vmid, resource.BoundAt) : null,
            commits.GetValueOrDefault(resource.DesiredRevisionId ?? Guid.Empty),
            commits.GetValueOrDefault(resource.CheckedRevisionId ?? Guid.Empty),
            resource.CheckedAt,
            commits.GetValueOrDefault(resource.AppliedRevisionId ?? Guid.Empty),
            resource.AppliedAt,
            resource.VerifiedAt,
            resource.SyncedInputsHash is not null && resource.SyncedInputsHash == resource.DesiredInputsHash,
            resource.Outputs is null ? null : JsonNode.Parse(resource.Outputs),
            resource.MissingSince,
            resource.RetiredAt,
            runs.Select(r => Summary(r, resource.Address, ResourceKinds.Guest)).ToList());
    }

    public async Task<InstallationDetailDto?> InstallationAsync(Guid id, CancellationToken cancellationToken)
    {
        var resource = await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.Kind == ResourceKinds.Installation, cancellationToken);
        if (resource is null)
        {
            return null;
        }

        var resources = await db.Resources.AsNoTracking()
            .Where(r => r.Id == id || (r.ParentAddress == resource.Address && r.Kind == ResourceKinds.Guest))
            .ToListAsync(cancellationToken);
        var runs = await LatestRunsAsync(resources.Select(r => r.Id).ToList(), cancellationToken);
        var observations = await LatestObservationsAsync(cancellationToken);
        var summary = InstallationSummary(resource, resources, runs, observations);
        var observation = observations.GetValueOrDefault(id);
        var guests = resources.Where(r => r.Kind == ResourceKinds.Guest && r.RetiredAt == null).ToList();
        var observedGuests = observation?["guests"] is JsonArray observed
            ? observed.OfType<JsonObject>().Select(g => ObservedGuest(g, guests)).OrderBy(g => g.Vmid).ToList()
            : [];

        var latest = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
        var text = latest is null || resource.MissingSince is not null ? null : await DefinitionTextAsync(latest.Commit, resource.DefinitionFile, cancellationToken);
        var definition = resource.Definition is null ? null : JsonNode.Parse(resource.Definition);
        var installationRuns = await db.Runs.AsNoTracking().Include(r => r.Steps).ThenInclude(s => s.Attempts)
            .Where(r => r.ResourceId == id)
            .OrderByDescending(r => r.CreatedAt)
            .Take(20)
            .ToListAsync(cancellationToken);
        return new InstallationDetailDto(
            summary,
            resource.DefinitionFile,
            text,
            Issues(resource.Issues),
            definition?["connection"]?["token"]?["store"]?.GetValue<string>(),
            definition?["connection"]?["tls"]?["verify"]?.GetValue<bool>() ?? (definition is null ? null : true),
            observedGuests,
            observation?["cluster"]?["name"]?.GetValue<string>(),
            installationRuns.Select(r => Summary(r, resource.Address, ResourceKinds.Installation)).ToList());
    }

    public async Task<OverviewDto> OverviewAsync(RepositoryDto repository, CancellationToken cancellationToken)
    {
        var guests = await db.Resources.AsNoTracking().Where(r => r.Kind == ResourceKinds.Guest && r.RetiredAt == null).ToListAsync(cancellationToken);
        var runs = await LatestRunsAsync(guests.Select(g => g.Id).ToList(), cancellationToken);
        var statuses = guests.Select(g =>
        {
            var (active, last) = runs.GetValueOrDefault(g.Id);
            return GuestStatus(g, active, last).Status;
        }).ToList();
        int Count(string status) => statuses.Count(s => s == status);
        var counts = new OverviewCounts(
            guests.Count,
            Count(GuestStatuses.InSync),
            Count(GuestStatuses.Pending),
            Count(GuestStatuses.Running) + Count(GuestStatuses.Queued),
            Count(GuestStatuses.AwaitingApproval),
            Count(GuestStatuses.Blocked),
            Count(GuestStatuses.Failed),
            Count(GuestStatuses.Uncertain),
            Count(GuestStatuses.Invalid),
            Count(GuestStatuses.Missing));

        var addresses = await db.Resources.AsNoTracking().ToDictionaryAsync(r => r.Id, r => (r.Address, r.Kind), cancellationToken);
        var attention = await db.Runs.AsNoTracking().Include(r => r.Steps).ThenInclude(s => s.Attempts)
            .Where(r => r.Status == RunStatuses.AwaitingApproval || r.Status == RunStatuses.Blocked || r.Status == RunStatuses.Uncertain)
            .OrderBy(r => r.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);
        var recent = await db.Runs.AsNoTracking().Include(r => r.Steps).ThenInclude(s => s.Attempts)
            .OrderByDescending(r => r.CreatedAt)
            .Take(15)
            .ToListAsync(cancellationToken);
        var agents = await db.Agents.AsNoTracking().CountAsync(a => !a.Disabled, cancellationToken);
        return new OverviewDto(
            repository,
            counts,
            connections.All.Count,
            agents,
            attention.Select(r => Summary(r, addresses.GetValueOrDefault(r.ResourceId, ("?", "?")))).ToList(),
            recent.Select(r => Summary(r, addresses.GetValueOrDefault(r.ResourceId, ("?", "?")))).ToList());
    }

    private InstallationSummaryDto InstallationSummary(
        Resource installation,
        IReadOnlyList<Resource> resources,
        IReadOnlyDictionary<Guid, (Run? Active, Run? Last)> runs,
        IReadOnlyDictionary<Guid, JsonObject> observations)
    {
        var definition = installation.Definition is null ? null : JsonNode.Parse(installation.Definition);
        var observation = observations.GetValueOrDefault(installation.Id);
        var guests = resources
            .Where(r => r.Kind == ResourceKinds.Guest && r.ParentAddress == installation.Address
                        && (r.RetiredAt == null || r.Definition != null && JsonNode.Parse(r.Definition)?["lifecycle"]?.GetValue<string>() == "retired"))
            .GroupBy(r => r.Address)
            .Select(g => g.OrderBy(r => r.RetiredAt.HasValue).First())
            .OrderBy(r => r.Address)
            .ToList();
        var guestSummaries = guests.Select(g =>
        {
            var (active, last) = runs.GetValueOrDefault(g.Id);
            return GuestSummary(g, [.. new[] { active, last }.OfType<Run>()], observation);
        }).ToList();

        var declaredHosts = (definition?["hosts"] as JsonObject)?.Select(p => (p.Key, Limit: p.Value?["limits"]?["concurrentOperations"]?.GetValue<int>())).ToList() ?? [];
        var observedHosts = (observation?["hosts"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var hosts = declaredHosts.Select(h =>
            {
                var o = observedHosts.FirstOrDefault(x => x["name"]?.GetValue<string>() == h.Key);
                return Host(h.Key, true, o, h.Limit);
            })
            .Concat(observedHosts.Where(o => declaredHosts.All(h => h.Key != o["name"]?.GetValue<string>()))
                .Select(o => Host(o["name"]?.GetValue<string>() ?? "?", false, o, null)))
            .ToList();
        var managedVmids = guests.Where(g => g.RetiredAt == null).Select(g => Vmid(g)).OfType<int>().ToHashSet();
        var unmanaged = (observation?["guests"] as JsonArray)?.OfType<JsonObject>()
            .Count(g => g["vmid"]?.GetValue<int>() is { } vmid && !managedVmids.Contains(vmid) && g["template"]?.GetValue<bool>() != true) ?? 0;
        var (activeRun, lastRun) = runs.GetValueOrDefault(installation.Id);
        var troubled = (activeRun ?? lastRun) is { Status: RunStatuses.Failed or RunStatuses.Blocked } run ? run : null;
        return new InstallationSummaryDto(
            installation.Id,
            installation.Address,
            definition?["name"]?.GetValue<string>() ?? installation.Address.Split('/')[^1],
            definition?["type"]?.GetValue<string>(),
            definition?["connection"]?["endpoint"]?.GetValue<string>(),
            installation.Valid,
            Issues(installation.Issues).Count(i => i.Severity == "error"),
            observation?["observedAt"]?.GetValue<DateTimeOffset>(),
            troubled?.Detail,
            troubled?.Status,
            troubled?.Id,
            observation?["version"]?.GetValue<string>(),
            hosts,
            guestSummaries,
            unmanaged);
    }

    private static GuestSummaryDto GuestSummary(Resource resource, IReadOnlyList<Run> runs, JsonObject? observation)
    {
        var ordered = runs.Where(r => r.Operation != Operations.Observe).OrderByDescending(r => r.CreatedAt).ToList();
        var active = ordered.FirstOrDefault(r => !RunStatuses.IsTerminal(r.Status));
        var last = ordered.FirstOrDefault(r => RunStatuses.IsTerminal(r.Status));
        var (status, detail) = GuestStatus(resource, active, last);
        var definition = resource.Definition is null ? null : JsonNode.Parse(resource.Definition);
        var vmid = Vmid(resource);
        var observed = vmid is null
            ? null
            : (observation?["guests"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(g => g["vmid"]?.GetValue<int>() == vmid);
        return new GuestSummaryDto(
            resource.Id,
            resource.Address,
            resource.Address.Split('/')[^1],
            definition?["host"]?.GetValue<string>(),
            vmid,
            definition?["lifecycle"]?.GetValue<string>() ?? "active",
            status,
            detail,
            resource.Valid,
            observed is null ? null : ObservedGuest(observed, [resource]),
            active is null ? null : Summary(active, resource.Address, resource.Kind),
            last is null ? null : Summary(last, resource.Address, resource.Kind));
    }

    private static int? Vmid(Resource resource) =>
        resource.BoundVmid ?? (resource.Definition is null ? null : JsonNode.Parse(resource.Definition)?["vmid"]?.GetValue<int>());

    private static ObservedGuestDto ObservedGuest(JsonObject g, IReadOnlyList<Resource> guests)
    {
        var vmid = g["vmid"]?.GetValue<int>() ?? 0;
        var managed = guests.FirstOrDefault(r => Vmid(r) == vmid);
        return new ObservedGuestDto(
            vmid,
            g["name"]?.GetValue<string>(),
            g["host"]?.GetValue<string>(),
            g["status"]?.GetValue<string>(),
            g["template"]?.GetValue<bool>() ?? false,
            (g["tags"] as JsonArray)?.Select(t => t?.GetValue<string>() ?? "").ToList() ?? [],
            g["maxCpu"]?.GetValue<double>(),
            g["maxMemoryBytes"]?.GetValue<long>(),
            g["uptimeSeconds"]?.GetValue<long>(),
            managed?.Address,
            managed?.Id);
    }

    private static ObservedHostDto Host(string name, bool declared, JsonObject? o, int? limit) => new(
        name,
        declared,
        o?["online"]?.GetValue<bool>(),
        o?["cpu"]?.GetValue<double>(),
        o?["maxCpu"]?.GetValue<double>(),
        o?["memoryBytes"]?.GetValue<long>(),
        o?["maxMemoryBytes"]?.GetValue<long>(),
        o?["uptimeSeconds"]?.GetValue<long>(),
        limit);

    private static string LabName(Resource lab) =>
        (lab.Definition is null ? null : JsonNode.Parse(lab.Definition)?["name"]?.GetValue<string>()) ?? lab.Address;

    private async Task<Dictionary<Guid, (Run? Active, Run? Last)>> LatestRunsAsync(List<Guid> resourceIds, CancellationToken cancellationToken)
    {
        var active = await db.Runs.AsNoTracking().Include(r => r.Steps).ThenInclude(s => s.Attempts)
            .Where(r => resourceIds.Contains(r.ResourceId)
                        && r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled)
            .ToListAsync(cancellationToken);
        var lastIds = await db.Runs.AsNoTracking()
            .Where(r => resourceIds.Contains(r.ResourceId)
                        && (r.Status == RunStatuses.Succeeded || r.Status == RunStatuses.Failed || r.Status == RunStatuses.Cancelled))
            .GroupBy(r => r.ResourceId)
            .Select(g => g.OrderByDescending(r => r.CreatedAt).Select(r => r.Id).First())
            .ToListAsync(cancellationToken);
        var last = await db.Runs.AsNoTracking().Include(r => r.Steps).ThenInclude(s => s.Attempts).Where(r => lastIds.Contains(r.Id)).ToListAsync(cancellationToken);
        return resourceIds.ToDictionary(
            id => id,
            id => (active.Where(r => r.ResourceId == id).MinBy(r => r.CreatedAt), last.FirstOrDefault(r => r.ResourceId == id)));
    }

    private async Task<Dictionary<Guid, JsonObject>> LatestObservationsAsync(CancellationToken cancellationToken)
    {
        var latest = await db.Observations.AsNoTracking()
            .GroupBy(o => o.ResourceId)
            .Select(g => g.OrderByDescending(o => o.ObservedAt).First())
            .ToListAsync(cancellationToken);
        var result = new Dictionary<Guid, JsonObject>();
        foreach (var observation in latest)
        {
            if (JsonNode.Parse(observation.Data) is JsonObject data)
            {
                data["observedAt"] = observation.ObservedAt;
                result[observation.ResourceId] = data;
            }
        }

        return result;
    }

    private async Task<Dictionary<Guid, string>> CommitsAsync(IEnumerable<Guid?> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.OfType<Guid>().Distinct().ToList();
        return await db.Revisions.AsNoTracking().Where(r => wanted.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Commit, cancellationToken);
    }

    private async Task<string?> DefinitionTextAsync(string commit, string? file, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return null;
        }

        try
        {
            var snapshot = await mirror.SnapshotAsync(commit, cancellationToken);
            var path = Path.GetFullPath(Path.Combine(snapshot, file));
            if (!path.StartsWith(snapshot + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
            {
                return null;
            }

            var info = new FileInfo(path);
            return info.Length > DefinitionTextLimit ? null : await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (GitException)
        {
            return null;
        }
    }
}
