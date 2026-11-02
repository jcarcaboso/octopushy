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
    /// <summary>A service whose guest is not ready for it yet. Nothing has failed.</summary>
    public const string Waiting = "waiting";
    /// <summary>A cluster whose members did not pass the checks of its latest run.</summary>
    public const string VerificationFailed = "verification-failed";
}

/// <summary>Builds the read models the API returns.</summary>
public sealed class Views(OctopushyDb db, RevisionCache revisions, RepositoryMirror mirror, AgentConnections connections, RunFactory factory)
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

    /// <summary>The typed view of a preview step's outputs.</summary>
    public static ConfigurePreviewDto? ConfigurePreview(Step step)
    {
        if (step.Name != StepNames.Preview || step.Outputs is null || JsonNode.Parse(step.Outputs) is not JsonObject outputs)
        {
            return null;
        }

        static List<string> Strings(JsonNode? node) => (node as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").ToList() ?? [];
        var manifest = outputs["manifest"];
        return new ConfigurePreviewDto(
            outputs["action"]?.GetValue<string>() ?? "?",
            outputs["playbook"]?.GetValue<string>() ?? "?",
            manifest?["commit"]?.GetValue<string>(),
            manifest?["image"]?.GetValue<string>(),
            manifest?["host"]?.GetValue<string>(),
            outputs["toolVersion"]?.GetValue<string>(),
            manifest?["destructive"]?.GetValue<bool>() ?? true,
            Strings(outputs["tasks"]),
            manifest?["changedFiles"] is JsonArray ? Strings(manifest["changedFiles"]) : null,
            manifest?["vars"]?.DeepClone(),
            (manifest?["secrets"] as JsonObject)?.Select(p => new ConfigureSecretDto(p.Key, p.Value?.GetValue<string>() ?? "")).ToList() ?? [],
            outputs["prepared"]?.DeepClone(),
            Strings(outputs["hostKeyFingerprints"]),
            outputs["planSha256"]?.GetValue<string>(),
            manifest?["ports"]?.DeepClone());
    }

    public static (string Status, string? Detail) GuestStatus(Resource resource, Run? active, Run? last)
    {
        if (resource.RetiredAt is not null)
        {
            return (GuestStatuses.Retired, null);
        }

        // A deleted guest with a proposed removal shows the removal's progress.
        if (resource.MissingSince is not null && active is null)
        {
            return (GuestStatuses.Missing, "The folder was deleted. Octopushy proposes removing the guest; "
                + "Plan now proposes it again. Restore the folder to keep the guest.");
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

    /// <summary>A service's status, from its own runs: neither its guest's failures nor a sibling's come into it.</summary>
    public static (string Status, string? Detail) ServiceStatus(Resource resource, GuestConfiguration? configuration, Run? active, Run? last, string? waiting)
    {
        if (resource.RetiredAt is not null)
            return (GuestStatuses.Retired, null);
        if (resource.MissingSince is not null && active is null)
            return (GuestStatuses.Missing, "The folder was deleted. Restore it to keep the service. Forget stops tracking it, and changes nothing on the guest.");
        if (!resource.Valid && resource.MissingSince is null)
            return (GuestStatuses.Invalid, "Its definition or its guest's has errors.");
        if (active is not null && Waiting(active) is { } queued)
            return (GuestStatuses.Queued, queued);
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

        if (waiting is not null)
            return (GuestStatuses.Waiting, waiting);
        if (last is { Status: RunStatuses.Failed })
            return (GuestStatuses.Failed, last.Detail);
        return configuration is { AppliedHash: not null } && configuration.AppliedHash == configuration.DesiredHash
            ? (GuestStatuses.InSync, null)
            : (GuestStatuses.Pending, last is { Status: RunStatuses.Cancelled } ? last.Detail : "Not deployed from the current definition yet.");
    }

    private static ServiceSummaryDto ServiceSummary(Resource resource, GuestConfiguration? configuration, Run? active, Run? last, string? waiting)
    {
        var (status, detail) = ServiceStatus(resource, configuration, active, last, waiting);
        var definition = resource.Definition is null ? null : JsonNode.Parse(resource.Definition);
        return new ServiceSummaryDto(
            resource.Id,
            resource.Address,
            resource.Address.Split('/')[^1],
            definition?["deploy"]?["recipe"]?.GetValue<string>(),
            status,
            detail,
            resource.Valid,
            definition?["ports"]?.DeepClone(),
            active is null ? null : Summary(active, resource.Address, resource.Kind),
            last is null ? null : Summary(last, resource.Address, resource.Kind));
    }

    /// <summary>The services of each of these guests, by guest address, as the inventory and the guest page show them.</summary>
    private async Task<Dictionary<string, List<ServiceSummaryDto>>> ServicesOfAsync(IReadOnlyCollection<Resource> guests, LabRepository? repository, CancellationToken cancellationToken)
    {
        var addresses = guests.Select(g => g.Address).ToList();
        var services = await db.Resources.AsNoTracking()
            .Where(r => r.Kind == ResourceKinds.Service && r.RetiredAt == null && r.ParentAddress != null && addresses.Contains(r.ParentAddress))
            .OrderBy(r => r.Address).ToListAsync(cancellationToken);
        var result = new Dictionary<string, List<ServiceSummaryDto>>(StringComparer.Ordinal);
        if (services.Count == 0)
            return result;
        var runs = await LatestRunsAsync(services.Select(s => s.Id).ToList(), cancellationToken);
        var ids = services.Select(s => s.Id).ToList();
        var configurations = await db.GuestConfigurations.AsNoTracking().Where(c => ids.Contains(c.ResourceId)).ToDictionaryAsync(c => c.ResourceId, cancellationToken);
        foreach (var guest in guests.Where(g => services.Any(s => s.ParentAddress == g.Address)))
        {
            var waiting = repository is null ? "Sync the current repository source first." : await GuestWork.ServicesWaitForAsync(db, guest, repository, cancellationToken);
            result[guest.Address] = services.Where(s => s.ParentAddress == guest.Address).Select(s =>
            {
                var (active, last) = runs.GetValueOrDefault(s.Id);
                return ServiceSummary(s, configurations.GetValueOrDefault(s.Id), active, last, waiting);
            }).ToList();
        }

        return result;
    }

    public async Task<ServiceDetailDto?> ServiceAsync(Guid id, CancellationToken cancellationToken)
    {
        var resource = await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.Kind == ResourceKinds.Service, cancellationToken);
        if (resource is null)
            return null;

        var runs = await db.Runs.AsNoTracking().Include(r => r.Steps).ThenInclude(s => s.Attempts)
            .Where(r => r.ResourceId == id).OrderByDescending(r => r.CreatedAt).Take(30).ToListAsync(cancellationToken);
        var guest = await db.Resources.AsNoTracking()
            .Where(r => r.Address == resource.ParentAddress && r.Kind == ResourceKinds.Guest).OrderBy(r => r.RetiredAt.HasValue).FirstOrDefaultAsync(cancellationToken);
        var head = await HeadRepositoryAsync(cancellationToken);
        var configuration = await db.GuestConfigurations.AsNoTracking().FirstOrDefaultAsync(c => c.ResourceId == id, cancellationToken);
        var waiting = guest is null || head is null ? null : await GuestWork.ServicesWaitForAsync(db, guest, head.Repository, cancellationToken);
        PolicyDto? policy = null;
        ResolvedSetting<IReadOnlyList<string>>? labels = null;
        var actions = new List<ConfigureActionDto>();
        var removal = false;
        if (head?.Repository.Service(resource.Address) is { } service && head.Repository.RepositoryValid)
        {
            var effective = PolicyResolver.For(head.Repository, service);
            policy = new PolicyDto(effective.Approval.Value, effective.Approval.SetBy, effective.Destructive.Value, effective.Destructive.SetBy);
            labels = PolicyResolver.AgentLabels(head.Repository, service);
            if (head.Repository.Recipes.GetValueOrDefault(service.Recipe)?.Configure is { } offered)
            {
                actions = offered.Actions.Values.Where(a => !a.Removal).Select(a => new ConfigureActionDto(a.Id, a.Description, a.Destructive, a.OnChange)).ToList();
                removal = offered.Removal is not null;
            }
        }
        else if (resource.MissingSince is not null)
        {
            // Its definition is gone from the head, and what removes it is in the one last applied.
            removal = await ServiceRemoval.DefinedAsync(db, revisions, resource, configuration, cancellationToken) is { Action: not null };
        }

        return new ServiceDetailDto(
            ServiceSummary(resource, configuration, runs.FirstOrDefault(r => !RunStatuses.IsTerminal(r.Status)), runs.FirstOrDefault(r => RunStatuses.IsTerminal(r.Status)), waiting),
            guest?.Id,
            resource.ParentAddress ?? "",
            resource.DefinitionFile,
            head is null ? null : await DefinitionTextAsync(head.Commit, resource.DefinitionFile, cancellationToken),
            Issues(resource.Issues),
            policy,
            labels?.Value ?? [],
            labels?.SetBy,
            actions,
            removal,
            configuration?.AppliedAction,
            configuration?.AppliedRevisionId is { } applied
                ? await db.Revisions.AsNoTracking().Where(r => r.Id == applied).Select(r => r.Commit).FirstOrDefaultAsync(cancellationToken) : null,
            configuration?.AppliedAt,
            resource.MissingSince,
            resource.RetiredAt,
            runs.Select(r => Summary(r, resource.Address, ResourceKinds.Service)).ToList(),
            await GuestTrustAsync(resource, await ConfigureReleaseAsync(resource, cancellationToken), cancellationToken));
    }

    public async Task<RevisionDto?> LatestRevisionAsync(CancellationToken cancellationToken)
    {
        var revision = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
        return revision is null ? null : Revision(revision, true);
    }

    public static RevisionDto Revision(DefinitionRevision r, bool isCurrentSource) =>
        new(r.Id, r.Commit, r.Subject, r.Author, r.CommittedAt, r.LoadedAt, r.RepositoryValid, r.ErrorCount, r.WarningCount, isCurrentSource);

    public static InstallationTrustDto Trust(Resource installation, InstallationTrust? trust)
    {
        var endpoints = RepositoryTrust.DeclaredEndpoints(installation.Definition is null ? null : JsonNode.Parse(installation.Definition));
        return new InstallationTrustDto(endpoints, endpoints.Where(e => trust?.Endpoints.Contains(e) != true).ToList(), trust?.TrustedAt, trust?.TrustedBy);
    }

    public static IReadOnlyList<IssueDto> Issues(string json) =>
        JsonSerializer.Deserialize<List<DefinitionIssue>>(json, InventoryUpdater.IssueJson)?
            .Select(i => new IssueDto(i.Severity == IssueSeverity.Error ? "error" : "warning", i.File, i.Line, i.Message, i.Address))
            .ToList() ?? [];

    public async Task<InventoryDto> InventoryAsync(CancellationToken cancellationToken)
    {
        var resources = await db.Resources.AsNoTracking().Where(r => r.RetiredAt == null || r.Kind == ResourceKinds.Guest).ToListAsync(cancellationToken);
        var runs = await LatestRunsAsync(resources.Select(r => r.Id).ToList(), cancellationToken);
        var observations = await LatestObservationsAsync(cancellationToken);
        var observing = await ObservingAsync(cancellationToken);
        var trusts = await db.InstallationTrusts.AsNoTracking().ToDictionaryAsync(t => t.Address, cancellationToken);
        var clusterTrusts = await db.ClusterTrusts.AsNoTracking().ToDictionaryAsync(t => t.Address, cancellationToken);
        var guarded = (await db.StoreEntries.AsNoTracking().Where(e => e.Protected).Select(e => e.Key).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var head = await HeadRepositoryAsync(cancellationToken);
        var services = await ServicesOfAsync(resources.Where(r => r.Kind == ResourceKinds.Guest && r.RetiredAt == null).ToList(), head?.Repository, cancellationToken);
        var labs = resources.Where(r => r.Kind == ResourceKinds.Lab && r.RetiredAt == null).OrderBy(r => r.Address)
            .Select(lab => new LabDto(
                lab.Id,
                lab.Address,
                LabName(lab),
                lab.Valid,
                Issues(lab.Issues),
                resources.Where(r => r.Kind == ResourceKinds.Installation && r.ParentAddress == lab.Address && r.RetiredAt == null)
                    .OrderBy(r => r.Address)
                    .Select(i => InstallationSummary(i, resources, runs, observations, observing, trusts.GetValueOrDefault(i.Address), services))
                    .ToList(),
                resources.Where(r => r.Kind == ResourceKinds.Cluster && r.ParentAddress == lab.Address && r.RetiredAt == null)
                    .OrderBy(r => r.Address)
                    .Select(c =>
                    {
                        var (active, last) = runs.GetValueOrDefault(c.Id);
                        return ClusterSummary(c, resources, head, new[] { active, last }.OfType<Run>().ToList(), active, last, observations.GetValueOrDefault(c.Id), clusterTrusts.GetValueOrDefault(c.Address), runs, guarded);
                    })
                    .ToList()))
            .ToList();
        return new InventoryDto(await LatestRevisionAsync(cancellationToken), labs);
    }

    public async Task<ClusterDetailDto?> ClusterAsync(Guid id, CancellationToken cancellationToken)
    {
        var resource = await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.Kind == ResourceKinds.Cluster, cancellationToken);
        if (resource is null)
        {
            return null;
        }

        var runs = await db.Runs.AsNoTracking().Include(r => r.Steps).ThenInclude(s => s.Attempts)
            .Where(r => r.ResourceId == id)
            .OrderByDescending(r => r.CreatedAt)
            .Take(30)
            .ToListAsync(cancellationToken);
        var head = await HeadRepositoryAsync(cancellationToken);
        var guests = await db.Resources.AsNoTracking().Where(r => r.Kind == ResourceKinds.Guest && r.RetiredAt == null).ToListAsync(cancellationToken);
        var guestRuns = await LatestRunsAsync(guests.Select(g => g.Id).ToList(), cancellationToken);
        var observation = (await LatestObservationsAsync(cancellationToken)).GetValueOrDefault(id);
        var trust = await db.ClusterTrusts.AsNoTracking().FirstOrDefaultAsync(t => t.Address == resource.Address, cancellationToken);
        var active = runs.FirstOrDefault(r => !RunStatuses.IsTerminal(r.Status));
        var last = runs.FirstOrDefault(r => RunStatuses.IsTerminal(r.Status));
        var guarded = (await db.StoreEntries.AsNoTracking().Where(e => e.Protected).Select(e => e.Key).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var summary = ClusterSummary(resource, guests, head, runs, active, last, observation, trust, guestRuns, guarded);

        PolicyDto? policy = null;
        ResolvedSetting<IReadOnlyList<string>>? labels = null;
        if (head?.Repository.Cluster(resource.Address) is { } cluster && head.Repository.RepositoryValid && resource.MissingSince is null)
        {
            var effective = PolicyResolver.For(head.Repository, cluster);
            policy = new PolicyDto(effective.Approval.Value, effective.Approval.SetBy, effective.Destructive.Value, effective.Destructive.SetBy);
            labels = PolicyResolver.AgentLabels(head.Repository, cluster);
        }

        var text = head is null || resource.MissingSince is not null ? null : await DefinitionTextAsync(head.Commit, resource.DefinitionFile, cancellationToken);
        var commits = await CommitsAsync([resource.DesiredRevisionId, resource.CheckedRevisionId, resource.AppliedRevisionId], cancellationToken);
        var bindings = await db.ClusterBindings.AsNoTracking().Where(b => b.ClusterId == id && b.DetachedAt == null).ToListAsync(cancellationToken);
        var definition = resource.MissingSince is not null ? null : head?.Repository.Cluster(resource.Address);
        var declared = definition?.Members.Select(m => m.Guest).ToHashSet(StringComparer.Ordinal) ?? [];
        return new ClusterDetailDto(
            summary,
            resource.DefinitionFile,
            text,
            Issues(resource.Issues),
            policy,
            labels?.Value ?? [],
            labels?.SetBy,
            commits.GetValueOrDefault(resource.DesiredRevisionId ?? Guid.Empty),
            commits.GetValueOrDefault(resource.CheckedRevisionId ?? Guid.Empty),
            resource.CheckedAt,
            commits.GetValueOrDefault(resource.AppliedRevisionId ?? Guid.Empty),
            resource.AppliedAt,
            LatestCheck(resource, runs, observation),
            resource.MissingSince,
            runs.Select(r => Summary(r, resource.Address, ResourceKinds.Cluster)).ToList(),
            bindings.Where(b => !declared.Contains(b.Guest)).OrderBy(b => b.Guest, StringComparer.Ordinal)
                .Select(b => new ClusterLeavingDto(b.GuestId, b.Guest, b.Address, b.PeerName, b.UnconfirmedAt is not null)).ToList());
    }

    /// <summary>
    /// The newest thing known about the members, from a run or the background. A check that
    /// failed replaces older results, even with nothing to say about each member: older
    /// results are not what anyone should read while the latest check says otherwise.
    /// </summary>
    private static JsonObject? LatestCheck(Resource resource, IEnumerable<Run> runs, JsonObject? observation)
    {
        var step = runs.SelectMany(r => r.Steps)
            .Where(s => s.Name == StepNames.VerifyCluster && s.Status is StepStatuses.Succeeded or StepStatuses.Failed && s.FinishedAt is not null)
            .MaxBy(s => s.FinishedAt);
        var observedAt = observation?["observedAt"]?.GetValue<DateTimeOffset>();
        JsonObject? check = null;
        DateTimeOffset? at = null;
        if (step is not null)
        {
            check = (step.Outputs is null ? null : JsonNode.Parse(step.Outputs)) as JsonObject ?? [];
            check["ok"] = step.Status == StepStatuses.Succeeded;
            if (step.Status == StepStatuses.Failed)
                check["error"] = step.Detail;
            check["source"] = "run";
            at = step.FinishedAt;
        }

        if (observation?.ContainsKey("members") == true && observedAt >= (at ?? DateTimeOffset.MinValue))
        {
            check = observation.DeepClone().AsObject();
            check.Remove("observedAt");
            check["source"] = "background";
            at = observedAt;
        }

        if (resource.ObserveError is { } error && resource.ObserveErrorAt is { } failedAt && failedAt > (at ?? DateTimeOffset.MinValue))
        {
            check = new JsonObject { ["ok"] = false, ["error"] = error, ["members"] = new JsonArray(), ["source"] = "background" };
            at = failedAt;
        }

        if (check is not null)
        {
            check["checkedAt"] = at;
            check["members"] ??= new JsonArray();
        }

        return check;
    }

    /// <summary>What the latest revision declares for a cluster's trust, or null when it cannot be built.</summary>
    public async Task<ClusterInputs?> ClusterInputsAsync(Resource resource, CancellationToken cancellationToken)
    {
        var head = await HeadRepositoryAsync(cancellationToken);
        return head is null ? null : Inputs(resource, head);
    }

    private static ClusterInputs? Inputs(Resource resource, HeadRepository head) =>
        resource.Valid && resource.MissingSince is null && head.Repository.IsValid(resource.Address) && head.Repository.Cluster(resource.Address) is { } cluster
            ? ClusterInputs.Build(head.Repository, cluster, head.Commit)
            : null;

    private sealed record HeadRepository(string Commit, LabRepository Repository);

    private async Task<HeadRepository?> HeadRepositoryAsync(CancellationToken cancellationToken)
    {
        var latest = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
        return latest is null ? null : new HeadRepository(latest.Commit, await revisions.GetAsync(latest.Commit, cancellationToken));
    }

    public static ClusterTrustDto ClusterTrust(ClusterInputs? inputs, ClusterTrust? trust, Resource resource, bool passwordProtected)
    {
        var endpoints = inputs?.Endpoints ?? [];
        var seen = ClusterCertificates.Parse(resource.MemberCertificates);
        var trusted = ClusterCertificates.Parse(trust?.Certificates);
        return new ClusterTrustDto(
            endpoints,
            endpoints.Where(e => trust?.Endpoints.Contains(e) != true).ToList(),
            inputs?.CodeDigest,
            inputs is not null && trust is not null && trust.CodeDigest != inputs.CodeDigest,
            trust?.TrustedAt,
            trust?.TrustedBy,
            endpoints.Select(e => new ClusterCertificateDto(
                e,
                seen.GetValueOrDefault(e)?.Sha256,
                trusted.GetValueOrDefault(e)?.Sha256,
                trusted.TryGetValue(e, out var known) && seen.GetValueOrDefault(e)?.Sha256 is { } sha256 && known.Sha256 == sha256)).ToList(),
            passwordProtected);
    }

    private static ClusterSummaryDto ClusterSummary(
        Resource resource,
        IReadOnlyList<Resource> resources,
        HeadRepository? head,
        IReadOnlyList<Run> clusterRuns,
        Run? active,
        Run? last,
        JsonObject? observation,
        ClusterTrust? trust,
        IReadOnlyDictionary<Guid, (Run? Active, Run? Last)> runs,
        IReadOnlySet<string> guarded)
    {
        var definition = resource.Definition is null ? null : JsonNode.Parse(resource.Definition);
        var (status, detail) = GuestStatus(resource, active, last);
        if (status == GuestStatuses.Missing)
        {
            detail = "cluster.yaml was deleted. The cluster keeps running and nothing was removed, but Octopushy no longer manages it. Restore the file to manage it again.";
        }
        else if (active is null && status is GuestStatuses.Failed or GuestStatuses.InSync)
        {
            // The newest check decides, whoever ran it, so the page never reads as two answers.
            var verifyFailed = last?.Steps.Any(s => s.Name == StepNames.VerifyCluster && s.Status == StepStatuses.Failed) == true;
            var check = LatestCheck(resource, clusterRuns, observation);
            if (check?["ok"]?.GetValue<bool>() == false)
            {
                status = GuestStatuses.VerificationFailed;
                detail = check["error"]?.GetValue<string>() ?? detail;
            }
            else if (check is not null && verifyFailed)
            {
                (status, detail) = (GuestStatuses.InSync, null);
            }
        }

        var inputs = head is null ? null : Inputs(resource, head);
        var addresses = inputs?.Members.ToDictionary(m => m.Guest) ?? [];
        var members = ((definition?["members"] as JsonArray)?.OfType<JsonObject>() ?? []).Select(m =>
        {
            var guest = m["guest"]!.GetValue<string>();
            var full = guest.Count(c => c == '/') == 1 ? $"{resource.Lab}/{guest}" : guest;
            var match = resources.FirstOrDefault(r => r.Kind == ResourceKinds.Guest && r.Address == full && r.RetiredAt is null);
            string? memberStatus = null;
            if (match is not null)
            {
                var (memberActive, memberLast) = runs.GetValueOrDefault(match.Id);
                memberStatus = GuestStatus(match, memberActive, memberLast).Status;
            }

            var known = addresses.GetValueOrDefault(full);
            return new ClusterMemberDto(full, m["role"]!.GetValue<string>(), known?.Address, match?.Id, memberStatus, known?.CodeDigest, known?.Execution.DeepClone());
        }).ToList();
        return new ClusterSummaryDto(
            resource.Id,
            resource.Address,
            resource.Address.Split('/')[^1],
            definition?["type"]?.GetValue<string>(),
            definition?["settings"]?["clusterDomain"]?.GetValue<string>(),
            status,
            detail,
            resource.Valid,
            Issues(resource.Issues).Count(i => i.Severity == "error"),
            members,
            active is null ? null : Summary(active, resource.Address, resource.Kind),
            last is null ? null : Summary(last, resource.Address, resource.Kind),
            ClusterTrust(inputs, trust, resource, inputs is not null && guarded.Contains(inputs.Cluster.Credentials.Key)),
            resource.VerifiedAt,
            observation?["observedAt"]?.GetValue<DateTimeOffset>(),
            resource.ObserveError,
            resource.ObserveErrorAt);
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
        var head = await HeadRepositoryAsync(cancellationToken);
        var services = await ServicesOfAsync([resource], head?.Repository, cancellationToken);
        var summary = GuestSummary(resource, runs, installation is null ? null : observations.GetValueOrDefault(installation.Id))
            with { Services = services.GetValueOrDefault(resource.Address) ?? [] };

        var latest = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
        PolicyDto? policy = null;
        ResolvedSetting<IReadOnlyList<string>>? labels = null;
        string? text = null;
        GuestConfigurationDto? configure = null;
        if (latest is not null && resource.MissingSince is null)
        {
            var repository = await revisions.GetAsync(latest.Commit, cancellationToken);
            if (repository.Guest(resource.Address) is { } guest && repository.RepositoryValid)
            {
                var effective = PolicyResolver.For(repository, guest);
                policy = new PolicyDto(effective.Approval.Value, effective.Approval.SetBy, effective.Destructive.Value, effective.Destructive.SetBy);
                labels = PolicyResolver.AgentLabels(repository, guest);
                if (guest.SetupRecipe is { } recipe && repository.Recipes.TryGetValue(recipe, out var definition) && definition.Configure is { } offered)
                {
                    var state = await db.GuestConfigurations.AsNoTracking().FirstOrDefaultAsync(c => c.ResourceId == id, cancellationToken);
                    configure = new GuestConfigurationDto(
                        recipe,
                        offered.Actions.Values.Select(a => new ConfigureActionDto(a.Id, a.Description, a.Destructive, a.OnChange)).ToList(),
                        state is { AppliedHash: not null } && state.AppliedHash == state.DesiredHash,
                        state?.AppliedAction,
                        state?.AppliedRevisionId is { } applied
                            ? await db.Revisions.AsNoTracking().Where(r => r.Id == applied).Select(r => r.Commit).FirstOrDefaultAsync(cancellationToken) : null,
                        state?.AppliedAt,
                        state?.Snapshot is not null);
                }
            }

            text = await DefinitionTextAsync(latest.Commit, resource.DefinitionFile, cancellationToken);
        }

        var commits = await CommitsAsync([resource.DesiredRevisionId, resource.CheckedRevisionId, resource.AppliedRevisionId], cancellationToken);
        var trust = await GuestTrustAsync(resource, await GuestReleaseAsync(resource, cancellationToken), cancellationToken);
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
            runs.Select(r => Summary(r, resource.Address, ResourceKinds.Guest)).ToList(),
            GuestDiskUsageEndpoints.View(resource,
                await db.GuestDiskUsages.AsNoTracking().FirstOrDefaultAsync(d => d.ResourceId == id, cancellationToken)),
            trust,
            StatefulGuard.Declared(resource),
            await StatefulGuard.ArmedAsync(db, resource, cancellationToken),
            configure,
            await GuestTrustAsync(resource, await ConfigureReleaseAsync(resource, cancellationToken), cancellationToken));
    }

    /// <summary>
    /// What trusting a guest's setup or a service covers, as declared now: the digest a run of it would
    /// carry, and the protected keys it binds. Null when none can be built, which leaves nothing to trust.
    /// </summary>
    public async Task<GuestRelease?> ConfigureReleaseAsync(Resource resource, CancellationToken cancellationToken)
    {
        // A missing service's Valid says how it last stood, and its removal is built from a revision that was valid.
        if (resource.RetiredAt is not null || !resource.Valid && resource.MissingSince is null)
            return null;
        ConfigureInputs inputs;
        if (resource.MissingSince is not null)
        {
            // A removal is built from the revision last applied, so that is what it would have to be trusted for.
            if (resource.Kind != ResourceKinds.Service)
                return null;
            var configuration = await db.GuestConfigurations.AsNoTracking().FirstOrDefaultAsync(c => c.ResourceId == resource.Id, cancellationToken);
            if (await ServiceRemoval.DefinedAsync(db, revisions, resource, configuration, cancellationToken) is not { } defined)
                return null;
            inputs = ConfigureInputs.Build(defined.Repository, defined.Service);
        }
        else if (await HeadRepositoryAsync(cancellationToken) is not { } head || !head.Repository.IsValid(resource.Address))
            return null;
        else if (head.Repository.Service(resource.Address) is { } service)
            inputs = ConfigureInputs.Build(head.Repository, service);
        else if (head.Repository.Guest(resource.Address) is { SetupRecipe: not null } guest)
            inputs = ConfigureInputs.Build(head.Repository, guest);
        else
            return null;
        var guarded = (await db.StoreEntries.AsNoTracking().Where(e => e.Protected).Select(e => e.Key).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var keys = inputs.SecretVars.Values.Concat(inputs.SecretFiles.Values).Append(inputs.SshKey).Append(inputs.HostKey)
            .Select(r => r.Key).Where(guarded.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new GuestRelease(inputs.CodeDigest, inputs.Execution, keys, null, null);
    }


    /// <summary>What trusting a guest covers, as declared now, or null when the guest cannot be built from the repository.</summary>
    public sealed record GuestRelease(string Digest, JsonObject Execution, IReadOnlyList<string> Keys, string? Refusal, string? Cluster);

    public async Task<GuestRelease?> GuestReleaseAsync(Resource resource, CancellationToken cancellationToken)
    {
        // A deleted guest's Valid says how it last stood, and its removal is built from a revision that was valid.
        if (resource.Kind != ResourceKinds.Guest || resource.RetiredAt is not null || !resource.Valid && resource.MissingSince is null
            || await ReleaseRepositoryAsync(resource, cancellationToken) is not { } head
            || !head.Repository.IsValid(resource.Address) || head.Repository.Guest(resource.Address) is not { } guest)
        {
            return null;
        }

        ReleaseBinding binding;
        IReadOnlyDictionary<string, string> secrets;
        try
        {
            (binding, secrets) = factory.Release(head.Repository, guest, resource, new DefinitionRevision { Commit = head.Commit });
        }
        catch (Exception e) when (e is RunException or InvalidOperationException)
        {
            // A guest no run can be built for has nothing to trust; its page must still load.
            return null;
        }

        var guarded = (await db.StoreEntries.AsNoTracking().Where(e => e.Protected).Select(e => e.Key).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var keys = secrets.Values.Where(guarded.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        // A backend's environment reaches OpenTofu during init, before there is a plan to review: no grant fits that.
        var refusal = secrets.FirstOrDefault(s => keys.Contains(s.Value) && s.Key.StartsWith(SecretNames.BackendPrefix, StringComparison.Ordinal)) is { Key: not null } bound
            ? $"{guest.Address} gives the protected secret {bound.Value} to its state backend as {bound.Key[SecretNames.BackendPrefix.Length..]}, and that is never released, whatever was granted. Give the backend a store entry of its own."
            : null;
        return new GuestRelease(binding.Digest, binding.Execution, keys, refusal, head.Repository.ClusterOf(resource.Address)?.Address);
    }

    /// <summary>
    /// The revision a run of this guest would be built from. A guest whose folder was deleted has a removal built from
    /// the revision last applied, so what trusting it covers is that, or the removal could never be trusted.
    /// </summary>
    private async Task<HeadRepository?> ReleaseRepositoryAsync(Resource resource, CancellationToken cancellationToken)
    {
        if (resource.MissingSince is null)
            return await HeadRepositoryAsync(cancellationToken);
        var eligible = RepositoryTrust.EligibleRevisions(db);
        var lastDefined = await eligible.AsNoTracking().FirstOrDefaultAsync(r => r.Id == resource.AppliedRevisionId, cancellationToken)
            ?? await db.Revisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == resource.LastRevisionId, cancellationToken);
        return lastDefined is null ? null : new HeadRepository(lastDefined.Commit, await revisions.GetAsync(lastDefined.Commit, cancellationToken));
    }

    public async Task<GuestTrustDto> GuestTrustAsync(Resource resource, GuestRelease? release, CancellationToken cancellationToken)
    {
        if (release is null)
        {
            return new GuestTrustDto([], null, null, false, null, null, null, null);
        }

        var grants = release.Keys.Count == 0 ? [] : await db.SecretGrants.AsNoTracking()
            .Where(g => g.ResourceId == resource.Id && release.Keys.Contains(g.Key)).ToListAsync(cancellationToken);
        var current = grants.Where(g => g.CodeDigest == release.Digest).ToList();
        var trusted = release.Keys.Count > 0 && release.Keys.All(k => current.Any(g => g.Key == k));
        var latest = trusted ? current.MaxBy(g => g.GrantedAt) : null;
        return new GuestTrustDto(release.Keys, release.Digest, release.Execution.DeepClone(), trusted, release.Cluster, release.Refusal, latest?.GrantedAt, latest?.GrantedBy);
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
        var trust = await db.InstallationTrusts.AsNoTracking().FirstOrDefaultAsync(t => t.Address == resource.Address, cancellationToken);
        var summary = InstallationSummary(resource, resources, runs, observations, await ObservingAsync(cancellationToken), trust);
        var observation = observations.GetValueOrDefault(id);
        var guests = resources.Where(r => r.Kind == ResourceKinds.Guest && r.RetiredAt == null).ToList();
        var observedGuests = observation?["guests"] is JsonArray observed
            ? observed.OfType<JsonObject>().Select(g => ObservedGuest(g, guests)).OrderBy(g => g.Vmid).ToList()
            : [];

        var latest = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
        var text = latest is null || resource.MissingSince is not null ? null : await DefinitionTextAsync(latest.Commit, resource.DefinitionFile, cancellationToken);
        var definition = resource.Definition is null ? null : JsonNode.Parse(resource.Definition);
        return new InstallationDetailDto(
            summary,
            resource.DefinitionFile,
            text,
            Issues(resource.Issues),
            definition?["connection"]?["token"]?["store"]?.GetValue<string>(),
            definition?["connection"]?["tls"]?["verify"]?.GetValue<bool>() ?? (definition is null ? null : true),
            observedGuests,
            observation?["cluster"]?["name"]?.GetValue<string>());
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
        var observeErrors = await db.Resources.AsNoTracking()
            .Where(r => (r.Kind == ResourceKinds.Installation || r.Kind == ResourceKinds.Cluster) && r.RetiredAt == null && r.MissingSince == null && r.ObserveError != null)
            .OrderBy(r => r.Address)
            .ToListAsync(cancellationToken);
        var agents = await db.Agents.AsNoTracking().CountAsync(a => !a.Disabled, cancellationToken);
        return new OverviewDto(
            repository,
            counts,
            connections.All.Count,
            agents,
            attention.Select(r => Summary(r, addresses.GetValueOrDefault(r.ResourceId, ("?", "?")))).ToList(),
            observeErrors.Select(r => new ObserveErrorDto(r.Id, r.Address, InstallationName(r), r.ObserveError!, r.ObserveErrorAt, r.Kind)).ToList(),
            recent.Select(r => Summary(r, addresses.GetValueOrDefault(r.ResourceId, ("?", "?")))).ToList());
    }

    private InstallationSummaryDto InstallationSummary(
        Resource installation,
        IReadOnlyList<Resource> resources,
        IReadOnlyDictionary<Guid, (Run? Active, Run? Last)> runs,
        IReadOnlyDictionary<Guid, JsonObject> observations,
        IReadOnlyDictionary<Guid, string?> observing,
        InstallationTrust? trust,
        IReadOnlyDictionary<string, List<ServiceSummaryDto>>? services = null)
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
            return GuestSummary(g, [.. new[] { active, last }.OfType<Run>()], observation) with { Services = services?.GetValueOrDefault(g.Address) };
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
        return new InstallationSummaryDto(
            installation.Id,
            installation.Address,
            InstallationName(installation),
            definition?["type"]?.GetValue<string>(),
            definition?["connection"]?["endpoint"]?.GetValue<string>(),
            installation.Valid,
            Issues(installation.Issues).Count(i => i.Severity == "error"),
            observation?["observedAt"]?.GetValue<DateTimeOffset>(),
            installation.ObserveError,
            installation.ObserveErrorAt,
            observing.ContainsKey(installation.Id),
            observing.GetValueOrDefault(installation.Id),
            observation?["version"]?.GetValue<string>(),
            hosts,
            guestSummaries,
            unmanaged,
            Trust(installation, trust));
    }

    private static GuestSummaryDto GuestSummary(Resource resource, IReadOnlyList<Run> runs, JsonObject? observation)
    {
        var ordered = runs.OrderByDescending(r => r.CreatedAt).ToList();
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
            definition?["type"]?.GetValue<string>() ?? GuestTypes.Vm,
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
            g["kind"]?.GetValue<string>(),
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

    private static string InstallationName(Resource installation) =>
        (installation.Definition is null ? null : JsonNode.Parse(installation.Definition)?["name"]?.GetValue<string>()) ?? installation.Address.Split('/')[^1];

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

    /// <summary>The installations with an observation under way, and why it waits when it has no agent yet.</summary>
    private async Task<Dictionary<Guid, string?>> ObservingAsync(CancellationToken cancellationToken) =>
        (await db.Attempts.AsNoTracking()
            .Where(a => a.ResourceId != null
                        && (a.Status == AttemptStatuses.Queued || a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running))
            .Select(a => new { Id = a.ResourceId!.Value, a.Status, a.WaitingReason })
            .ToListAsync(cancellationToken))
        .DistinctBy(a => a.Id)
        .ToDictionary(
            a => a.Id,
            a => a.Status == AttemptStatuses.Queued ? a.WaitingReason ?? "Waiting to be assigned to an agent." : null);

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
