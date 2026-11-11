using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Definitions;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Store;
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
    /// <summary>A service whose guest is not ready for it yet. Nothing is wrong with the service.</summary>
    public const string Waiting = "waiting";
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
public sealed partial class Views(OctopushyDb db, RevisionCache revisions, RepositoryMirror mirror, WorkflowsMirrors workflowMirrors, AgentConnections connections,
    TimeProvider clock)
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
        Waiting(run),
        run.ReasonDetail);

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
        // Held back before anything was queued: it may not start changing its guest yet.
        if (step?.Status == StepStatuses.Pending && run.Detail is not null)
        {
            return run.Detail;
        }

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
                (c["actions"] as JsonArray)?.Select(a => a?.GetValue<string>() ?? "").ToList() ?? [],
                (c["attributes"] as JsonArray)?.OfType<JsonObject>().Select(PlanAttribute).OfType<PlanAttributeDto>().ToList() ?? []))
            .ToList() ?? [];
        return new PlanDto(
            outputs["hasChanges"]?.GetValue<bool>() ?? changes.Count > 0,
            changes,
            new PlanCountsDto(Count("create"), Count("update"), Count("delete"), Count("replace")),
            outputs["planSha256"]?.GetValue<string>(),
            outputs["toolVersion"]?.GetValue<string>());
    }

    /// <summary>
    /// One VM attribute of a plan change, or null for one the agent should not have sent: only the names it
    /// allowlists, with scalar values, or a list of strings for tags. A sensitive one keeps its name and no value.
    /// </summary>
    private static PlanAttributeDto? PlanAttribute(JsonObject attribute)
    {
        if (attribute["name"] is not JsonValue named || !named.TryGetValue<string>(out var name) || !ShownAttribute().IsMatch(name))
        {
            return null;
        }

        bool Flag(string key) => attribute[key] is JsonValue flag && flag.TryGetValue<bool>(out var set) && set;
        if (Flag("sensitive"))
        {
            return new PlanAttributeDto(name, null, null, Flag("unknown"), true);
        }

        bool Shown(JsonNode? value) => value is null or JsonValue
            || (name == "tags" && value is JsonArray tags && tags.All(t => t is JsonValue tag && tag.GetValueKind() == JsonValueKind.String));
        var (before, after) = (attribute["before"], attribute["after"]);
        return Shown(before) && Shown(after) ? new PlanAttributeDto(name, before?.DeepClone(), after?.DeepClone(), Flag("unknown"), false) : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(cpu\.(cores|sockets)|memory\.dedicated|disk\.[a-z]+[0-9]+\.size|tags|network_device\.[0-9]+\.bridge)\z")]
    private static partial System.Text.RegularExpressions.Regex ShownAttribute();

    /// <summary>
    /// The typed view of a preview step's outputs. <paramref name="previousPrepared"/> is the run's baseline from its
    /// context: it is shown beside the preview, and is not part of what the agent saw or the approval names.
    /// </summary>
    public static ConfigurePreviewDto? ConfigurePreview(Step step, JsonNode? previousPrepared = null)
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
            manifest?["accessRotation"] is JsonObject rotation
                ? new AccessRotationDto(rotation["current"]?.GetValue<string>() ?? "?", rotation["candidate"]?.GetValue<string>() ?? "?", Strings(rotation["remove"]))
                : null,
            previousPrepared?.DeepClone());
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

        // Its desired work has no run yet, as when it waits for the image of its built template.
        if (resource.ProvisionWaiting is { } held)
        {
            return (GuestStatuses.Pending, held);
        }

        if (last is { Status: RunStatuses.Failed })
        {
            return (GuestStatuses.Failed, last.Detail);
        }

        return resource.SyncedInputsHash is not null && resource.SyncedInputsHash == resource.DesiredInputsHash
            ? (GuestStatuses.InSync, null)
            : (GuestStatuses.Pending, last is { Status: RunStatuses.Cancelled } ? last.Detail : "Not provisioned from the current definition yet.");
    }

    /// <summary>The head revision's templates, with what each built template's current key has: see docs/built-templates.md.</summary>
    public async Task<IReadOnlyList<TemplateDto>> TemplatesAsync(CancellationToken cancellationToken)
    {
        var head = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
        if (head is null)
            return [];
        var repository = await revisions.GetAsync(head, cancellationToken);
        var keys = repository.Templates.Values.Select(t => t.Build?.Key).OfType<string>().ToList();
        var builds = await db.ImageBuilds.AsNoTracking().Where(b => keys.Contains(b.Key)).ToDictionaryAsync(b => b.Key, cancellationToken);
        return repository.Templates.Values.OrderBy(t => t.Id, StringComparer.Ordinal).Select(t => new TemplateDto(
            t.Id,
            t.Os,
            t.Json["source"]?["build"] is null ? "download" : "built",
            t.Valid,
            t.AvailableOn,
            t.Build is not { } build ? null
                : builds.GetValueOrDefault(build.Key) is { } row
                    ? new TemplateBuildDto(build.Key, build.WorkflowLabel, build.WorkflowReference, build.Output, build.LockPath, row.Status, row.AttemptId,
                        row.Status == ImageBuildStatuses.Built ? row.Sha256 : null, row.Status == ImageBuildStatuses.Built ? row.Size : null,
                        row.Status == ImageBuildStatuses.Built ? row.FileName : null, row.Error, row.BuiltAt, row.LastUsedAt)
                    : new TemplateBuildDto(build.Key, build.WorkflowLabel, build.WorkflowReference, build.Output, build.LockPath, "not-built",
                        null, null, null, null, null, null, null))).ToList();
    }

    public async Task<RevisionDto?> LatestRevisionAsync(CancellationToken cancellationToken)
    {
        var revision = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
        return revision is null ? null : Revision(revision, true);
    }

    public static RevisionDto Revision(DefinitionRevision r, bool isCurrentSource) =>
        new(r.Id, r.Commit, r.Subject, r.Author, r.CommittedAt, r.LoadedAt, r.RepositoryValid, r.ErrorCount, r.WarningCount, isCurrentSource,
            r.WorkflowsRepository, r.WorkflowsDigest.Length == 0 ? null : r.WorkflowsDigest);

    /// <summary>
    /// Where the head revision's workflows come from, the releases recorded for a workflows repository with what is wrong
    /// with any of them, and what each guest and service resolved to.
    /// </summary>
    public async Task<WorkflowsSourceDto> WorkflowsAsync(RepositorySettings settings, CancellationToken cancellationToken)
    {
        var head = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
        LabRepository? repository = null;
        if (head is not null)
        {
            try
            {
                repository = await revisions.GetAsync(head, cancellationToken);
            }
            catch (GitException)
            {
            }
        }

        var resolutions = head is null ? [] : RevisionCache.Resolutions(head.Resolutions);
        var uses = new List<WorkflowUseDto>();
        var url = head?.WorkflowsRepository ?? settings.WorkflowsRepository;
        if (url is null)
        {
            foreach (var (address, kind, reference, workflow) in Uses(repository))
                uses.Add(new WorkflowUseDto(address, kind, reference.ToString(), workflow?.Version.ToString(), workflow is null ? null : head!.Commit,
                    workflow is null ? "Not resolved: see the resource's issues." : null, null));
            var own = (repository?.Workflows ?? []).GroupBy(w => w.Id).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new WorkflowReleasesDto(g.Key, g.OrderByDescending(w => w.Version)
                    .Select(w => new WorkflowReleaseDto(w.Version.ToString(), head!.Commit, null, w.Valid, false, null,
                        w.Valid ? null : "The workflow has errors: see the revision's issues."))
                    .ToList()))
                .ToList();
            return new WorkflowsSourceDto("local", null, null, null, own, [], uses);
        }

        var records = await db.WorkflowReleases.AsNoTracking().Where(r => r.Repository == url).ToListAsync(cancellationToken);
        IReadOnlySet<string> missing;
        try
        {
            missing = await workflowMirrors.For(url).MissingCommitsAsync(records.Select(r => r.Commit), cancellationToken);
        }
        catch (GitException)
        {
            missing = records.Select(r => r.Commit).ToHashSet(StringComparer.Ordinal);
        }

        var releases = records.GroupBy(r => r.WorkflowId).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new WorkflowReleasesDto(g.Key, g.OrderByDescending(r => r.Major).ThenByDescending(r => r.Minor)
                .Select(r => new WorkflowReleaseDto(r.Version, r.Commit, r.FirstSeenAt, r.Problem is null && !missing.Contains(r.Commit),
                    r.DeletedAt is not null, r.MovedTo, WorkflowReleaseBook.IssueOf(r, !missing.Contains(r.Commit))))
                .ToList()))
            .ToList();
        var issues = records.OrderBy(r => r.WorkflowId, StringComparer.Ordinal).ThenBy(r => r.Major).ThenBy(r => r.Minor)
            .Select(r => WorkflowReleaseBook.IssueOf(r, !missing.Contains(r.Commit)))
            .Where(i => i is not null && !i.Contains("is still used", StringComparison.Ordinal))
            .Select(i => i!)
            .ToList();
        foreach (var (address, kind, reference, _) in Uses(repository))
        {
            var resolved = resolutions.FirstOrDefault(r => r.Reference == reference.ToString());
            // A newer major is offered, never taken by itself: upgrading is a change to the reference.
            var newer = records.Where(r => r.WorkflowId == reference.Id && r.Major > reference.Major && r.Problem is null && !missing.Contains(r.Commit))
                .OrderByDescending(r => r.Major).ThenByDescending(r => r.Minor).FirstOrDefault();
            uses.Add(new WorkflowUseDto(address, kind, reference.ToString(), resolved?.Version, resolved?.Commit,
                resolved is null ? "Not resolved in the head revision." : resolved.Problem, newer?.Version));
        }

        return new WorkflowsSourceDto("repository", url, settings.WorkflowsLastFetchAt, settings.WorkflowsLastFetchError, releases, issues, uses);
    }

    private static IEnumerable<(string Address, string Kind, WorkflowReference Reference, WorkflowDefinition? Workflow)> Uses(LabRepository? repository) =>
        repository is null ? [] : repository.Guests.Select(g => (g.Address, ResourceKinds.Guest, g.WorkflowReference, g.Workflow))
            .Concat(repository.Services.Select(s => (s.Address, ResourceKinds.Service, s.WorkflowReference, s.Workflow)))
            .OrderBy(u => u.Address, StringComparer.Ordinal);

    public static InstallationTrustDto Trust(Resource installation, InstallationTrust? trust)
    {
        var endpoints = RepositoryTrust.DeclaredEndpoints(installation.Definition is null ? null : JsonNode.Parse(installation.Definition));
        // Trusted while observed only, and now asking to be managed: every endpoint needs trust again.
        var managementChanged = trust is not null && trust.ObservedOnly != installation.ObservedOnly;
        return new InstallationTrustDto(endpoints, endpoints.Where(e => managementChanged || trust?.Endpoints.Contains(e) != true).ToList(),
            trust?.TrustedAt, trust?.TrustedBy, managementChanged);
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
        var labs = resources.Where(r => r.Kind == ResourceKinds.Lab && r.RetiredAt == null).OrderBy(r => r.Address)
            .Select(lab => new LabDto(
                lab.Id,
                lab.Address,
                LabName(lab),
                lab.Valid,
                Issues(lab.Issues),
                resources.Where(r => r.Kind == ResourceKinds.Installation && r.ParentAddress == lab.Address && r.RetiredAt == null)
                    .OrderBy(r => r.Address)
                    .Select(i => InstallationSummary(i, resources, runs, observations, observing, trusts.GetValueOrDefault(i.Address)))
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
        GuestConfigurationDto? configure = null;
        if (latest is not null && resource.MissingSince is null)
        {
            var repository = await revisions.GetAsync(latest, cancellationToken);
            if (repository.Guest(resource.Address) is { } guest && repository.RepositoryValid)
            {
                var effective = PolicyResolver.For(repository, guest);
                policy = new PolicyDto(effective.Approval.Value, effective.Approval.SetBy, effective.Destructive.Value, effective.Destructive.SetBy,
                    PolicyResolver.ActionApprovals(guest).Select(a => new ActionApprovalDto(a.Action, a.Approval.Value, a.Approval.SetBy)).ToList());
                labels = PolicyResolver.AgentLabels(repository, guest);
                if (guest.Setup?.Configure is { } offered)
                {
                    var state = await db.GuestConfigurations.AsNoTracking().FirstOrDefaultAsync(c => c.ResourceId == id, cancellationToken);
                    var schedules = await SchedulesAsync(repository, guest, id, cancellationToken);
                    var followed = ConfigureInputs.FollowedSecretsOf(guest);
                    var usable = await FollowedSecrets.UsableAsync(db, followed, cancellationToken);
                    var started = FollowedSecrets.Parse(state?.SecretVersions);
                    var pending = state?.SecretVersions is null ? [] : FollowedSecrets.Changes(started, usable).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
                    configure = new GuestConfigurationDto(
                        guest.Workflow!.Label,
                        offered.Actions.Values.Select(a => new ConfigureActionDto(a.Id, a.Description, a.Destructive, a.OnChange)).ToList(),
                        state is { AppliedHash: not null } && state.AppliedHash == state.DesiredHash,
                        state?.AppliedAction,
                        state?.AppliedRevisionId is { } applied
                            ? await db.Revisions.AsNoTracking().Where(r => r.Id == applied).Select(r => r.Commit).FirstOrDefaultAsync(cancellationToken) : null,
                        state?.AppliedAt,
                        state?.Snapshot is not null,
                        schedules,
                        followed.Select(k => new FollowedSecretDto(k, usable[k], started.GetValueOrDefault(k), pending.Contains(k))).ToList(),
                        await GuestWork.OwnKeyRefusedAsync(db, id, guest, cancellationToken));
                }
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
            runs.Select(r => Summary(r, resource.Address, ResourceKinds.Guest)).ToList(),
            configure,
            await ManagedAccessAsync(id, cancellationToken),
            await ServicesOfAsync(resource, latest, cancellationToken),
            await ManagedHostKeyAsync(id, cancellationToken));
    }

    /// <summary>The guest's host key, when it is Octopushy's: the pin, and the version plans carry when that is another.</summary>
    private async Task<ManagedHostKeyDto?> ManagedHostKeyAsync(Guid id, CancellationToken cancellationToken)
    {
        var keys = await db.GuestHostKeys.AsNoTracking().Where(k => k.ResourceId == id).ToListAsync(cancellationToken);
        if (keys.Count == 0)
            return null;
        var name = GuestHostCredentials.Key(id);
        var carried = await db.StoreEntries.AsNoTracking().Where(e => e.Key == name).Select(e => (int?)e.CurrentVersion).FirstOrDefaultAsync(cancellationToken);
        static ManagedHostKeyVersionDto Of(GuestHostKey k) => new(k.Version, k.PublicKey, k.Fingerprint, k.CreatedAt, k.DeliveredAt);
        var pinned = keys.Where(k => k.DeliveredAt is not null).MaxBy(k => k.Version);
        var next = keys.Find(k => k.Version == carried);
        return new ManagedHostKeyDto(pinned is null ? null : Of(pinned), next is null || next.Version == pinned?.Version ? null : Of(next));
    }

    /// <summary>
    /// The command that sets up one host: the installation's own account and datastore, so what it sets up is what
    /// uploads use, and the script of the release this server is, so it matches.
    /// </summary>
    public static string SnippetsCommand(string publicKey, string user, string datastore)
    {
        var version = typeof(Views).Assembly.GetName().Version;
        var reference = version is null ? "main" : $"v{version.Major}.{version.Minor}.{version.Build}";
        return $"OCTOPUSHY_SNIPPETS_KEY={ShellQuote(publicKey)} OCTOPUSHY_SNIPPETS_USER={ShellQuote(user)} "
            + $"OCTOPUSHY_SNIPPETS_DATASTORE={ShellQuote(datastore)} "
            + $"bash -c \"$(curl -fsSL https://raw.githubusercontent.com/jcarcaboso/octopushy/{reference}/deploy/proxmox/snippets.sh)\"";
    }

    /// <summary>One word for a POSIX shell, whatever it holds: single quotes, with each one inside closed and reopened.</summary>
    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>An installation's snippet uploads, from its latest definition and the key Octopushy made for it.</summary>
    public async Task<SnippetAccessDto> SnippetAccessAsync(string address, JsonNode? definition, CancellationToken cancellationToken)
    {
        var key = await InstallationSnippetCredentials.FindAsync(db, address, cancellationToken);
        var snippets = definition?["snippets"] as JsonObject;
        var hosts = new List<SnippetHostDto>();
        foreach (var (host, node) in (snippets?["hosts"] as JsonObject) ?? [])
        {
            var store = node?["hostKey"]?["store"]?.GetValue<string>() ?? "";
            var endpoint = definition?["hosts"]?[host]?["endpoint"]?.GetValue<string>() ?? definition?["connection"]?["endpoint"]?.GetValue<string>();
            var sshd = node?["address"]?.GetValue<string>()
                ?? (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : "");
            hosts.Add(new SnippetHostDto(host, sshd, node?["port"]?.GetValue<int>() ?? 22, store,
                await db.StoreEntries.AsNoTracking().AnyAsync(e => e.Key == store && e.CurrentVersion > 0, cancellationToken)));
        }

        var datastore = snippets?["datastore"]?.GetValue<string>() ?? SnippetSettings.DefaultDatastore;
        var user = snippets?["user"]?.GetValue<string>() ?? SnippetSettings.DefaultUser;
        return new SnippetAccessDto(
            key?.PublicKey,
            key?.Fingerprint,
            key?.CreatedAt,
            snippets is not null,
            datastore,
            user,
            hosts,
            key is null ? null : SnippetsCommand(key.PublicKey, user, datastore));
    }

    /// <summary>The services the repository shows on a guest, and those it no longer does until they are dealt with.</summary>
    private async Task<IReadOnlyList<ServiceSummaryDto>> ServicesOfAsync(Resource guest, DefinitionRevision? latest, CancellationToken cancellationToken)
    {
        var services = await db.Resources.AsNoTracking()
            .Where(r => r.Kind == ResourceKinds.Service && r.ParentAddress == guest.Address && r.RetiredAt == null)
            .OrderBy(r => r.Address)
            .ToListAsync(cancellationToken);
        if (services.Count == 0)
            return [];
        var runs = await LatestRunsAsync(services.Select(s => s.Id).ToList(), cancellationToken);
        var ids = services.Select(s => s.Id).ToList();
        var states = await db.GuestConfigurations.AsNoTracking().Where(c => ids.Contains(c.ResourceId)).ToDictionaryAsync(c => c.ResourceId, cancellationToken);
        var summaries = new List<ServiceSummaryDto>();
        foreach (var service in services)
        {
            var (active, last) = runs.GetValueOrDefault(service.Id);
            summaries.Add(ServiceSummary(service, active, last, states.GetValueOrDefault(service.Id), await ServiceWaitingAsync(guest, service, latest, cancellationToken)));
        }

        return summaries;
    }

    /// <summary>What a service waits for on its guest, as <see cref="GuestWork.WaitingForAsync"/> says.</summary>
    private async Task<string?> ServiceWaitingAsync(Resource? guest, Resource service, DefinitionRevision? latest, CancellationToken cancellationToken)
    {
        if (guest is null || latest is null)
            return "Sync the current repository source first.";
        var repository = await revisions.GetAsync(latest, cancellationToken);
        return await GuestWork.WaitingForAsync(db, guest, repository, cancellationToken, repository.Service(service.Address));
    }

    private static ServiceSummaryDto ServiceSummary(Resource resource, Run? active, Run? last, GuestConfiguration? state, string? waiting)
    {
        var (status, detail) = ServiceStatus(resource, active, last, state, waiting);
        return new ServiceSummaryDto(resource.Id, resource.Address, resource.Address.Split('/')[^1], status, detail, resource.Valid,
            active is null ? null : Summary(active, resource.Address, resource.Kind),
            last is null ? null : Summary(last, resource.Address, resource.Kind),
            Workflow(resource));
    }

    /// <summary>What a guest or service names in its definition, and what the latest revision resolved that to.</summary>
    private static WorkflowDto? Workflow(Resource resource) =>
        resource.Definition is not null && JsonNode.Parse(resource.Definition)?["workflow"] is JsonValue reference && reference.TryGetValue<string>(out var text)
            ? new WorkflowDto(text, resource.Workflow)
            : null;

    public static (string Status, string? Detail) ServiceStatus(Resource resource, Run? active, Run? last, GuestConfiguration? state, string? waiting)
    {
        if (resource.RetiredAt is not null)
            return (GuestStatuses.Retired, null);
        if (resource.MissingSince is not null && active is null)
            return (GuestStatuses.Missing, "The folder was deleted. Nothing was removed: removing a service is not built yet, so what it deployed is still on the guest. Restore the folder to keep managing it.");
        if (!resource.Valid)
            return (GuestStatuses.Invalid, "The definition has errors.");
        if (active is not null && Waiting(active) is { } held)
            return (GuestStatuses.Queued, held);
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
            return (GuestStatuses.Failed, last.Detail);
        if (state?.AppliedHash is not null && state.AppliedHash == state.DesiredHash)
            return (GuestStatuses.InSync, null);
        return waiting is not null
            ? (GuestStatuses.Waiting, waiting)
            : (GuestStatuses.Pending, last is { Status: RunStatuses.Cancelled } ? last.Detail : "Not deployed from the current definition yet.");
    }

    public async Task<ServiceDetailDto?> ServiceAsync(Guid id, CancellationToken cancellationToken)
    {
        var resource = await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.Kind == ResourceKinds.Service, cancellationToken);
        if (resource is null)
            return null;

        var runs = await db.Runs.AsNoTracking().Include(r => r.Steps).ThenInclude(s => s.Attempts)
            .Where(r => r.ResourceId == id)
            .OrderByDescending(r => r.CreatedAt)
            .Take(30)
            .ToListAsync(cancellationToken);
        var guest = await db.Resources.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Address == resource.ParentAddress && r.Kind == ResourceKinds.Guest && r.RetiredAt == null, cancellationToken);
        var state = await db.GuestConfigurations.AsNoTracking().FirstOrDefaultAsync(c => c.ResourceId == id, cancellationToken);
        var latest = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
        var repository = latest is null || !latest.RepositoryValid ? null : await revisions.GetAsync(latest, cancellationToken);
        var (definition, owner) = repository?.Service(resource.Address) is { } service && repository.Guest(service.GuestAddress) is { } host
            ? (service, host) : default;
        ResolvedSetting<IReadOnlyList<string>>? labels = null;
        PolicyDto? policy = null;
        if (definition is not null)
        {
            var effective = PolicyResolver.For(repository!, owner!, definition);
            policy = new PolicyDto(effective.Approval.Value, effective.Approval.SetBy, effective.Destructive.Value, effective.Destructive.SetBy);
            labels = PolicyResolver.AgentLabels(repository!, owner!, definition);
        }

        var commits = await CommitsAsync([state?.AppliedRevisionId], cancellationToken);
        var active = runs.Where(r => !RunStatuses.IsTerminal(r.Status)).MinBy(r => r.CreatedAt);
        var last = runs.FirstOrDefault(r => RunStatuses.IsTerminal(r.Status));
        return new ServiceDetailDto(
            ServiceSummary(resource, active, last, state, await ServiceWaitingAsync(guest, resource, latest, cancellationToken)),
            guest?.Id,
            resource.ParentAddress,
            resource.DefinitionFile,
            latest is null || resource.MissingSince is not null ? null : await DefinitionTextAsync(latest.Commit, resource.DefinitionFile, cancellationToken),
            Issues(resource.Issues),
            policy,
            labels?.Value ?? [],
            labels?.SetBy,
            definition?.Stages.Select(s => new ServiceStageDto(s.Name, s.Definition.Executor)).ToList() ?? [],
            commits.GetValueOrDefault(state?.AppliedRevisionId ?? Guid.Empty),
            state?.AppliedAt,
            resource.MissingSince,
            resource.RetiredAt,
            runs.Select(r => Summary(r, resource.Address, ResourceKinds.Service)).ToList());
    }

    /// <summary>The version runs use: public, and all there is to show. The private half never reaches this layer.</summary>
    private async Task<ManagedAccessDto?> ManagedAccessAsync(Guid id, CancellationToken cancellationToken)
    {
        var entry = GuestAccessCredentials.Key(id);
        var current = await db.StoreEntries.AsNoTracking().Where(e => e.Key == entry).Select(e => (int?)e.CurrentVersion).FirstOrDefaultAsync(cancellationToken);
        var keys = await db.GuestAccessKeys.AsNoTracking().Where(k => k.ResourceId == id).OrderByDescending(k => k.Version).ToListAsync(cancellationToken);
        if (keys.Find(k => k.Version == current) is not { } key)
            return null;
        static string State(GuestAccessKey k, int? current) =>
            k.Version == current ? "in-use" : k.RemovedAt is not null ? "removed" : k.Version > current ? "candidate" : "old";
        return new ManagedAccessDto(key.PublicKey, key.Fingerprint, key.Version, key.ProvisionedAt, key.LastUsedAt, keys.Select(k => new ManagedAccessKeyDto(
            k.Version, k.Fingerprint, State(k, current), k.AuthorizedAt, k.VerifiedAt, k.PromotedAt, k.PromotedBy, k.PromotionEvidence, k.RemovedAt)).ToList());
    }

    private async Task<IReadOnlyList<GuestScheduleDto>> SchedulesAsync(LabRepository repository, GuestDefinition guest, Guid id, CancellationToken cancellationToken)
    {
        // An invalid guest can hold a time or a time zone that cannot be read, and its page still has to open.
        var declared = repository.IsValid(guest.Address) ? GuestSchedules.Of(guest) : [];
        if (declared.Count == 0)
            return [];
        var timezone = repository.Lab(guest.Lab)?.Timezone ?? "UTC";
        var zone = GuestSchedules.ZoneOf(timezone);
        var states = await db.GuestSchedules.AsNoTracking().Where(s => s.ResourceId == id).ToListAsync(cancellationToken);
        var runIds = states.Where(s => s.LastRunId is not null).Select(s => s.LastRunId!.Value).ToList();
        var runs = await db.Runs.AsNoTracking().Where(r => runIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, cancellationToken);
        return declared.Select(schedule =>
        {
            var state = states.Find(s => s.Action == schedule.Action && s.Cron == schedule.Cron);
            var run = state?.LastRunId is { } last ? runs.GetValueOrDefault(last) : null;
            return new GuestScheduleDto(
                schedule.Action,
                schedule.Cron,
                timezone,
                GuestSchedules.Next(schedule.Cron, zone, clock.GetUtcNow()),
                run?.Id,
                run?.Number,
                run?.Status,
                run?.Detail,
                state?.Note,
                state?.NoteAt);
        }).ToList();
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
            observation?["cluster"]?["name"]?.GetValue<string>(),
            await SnippetAccessAsync(resource.Address, definition, cancellationToken));
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
            .Where(r => r.Kind == ResourceKinds.Installation && r.RetiredAt == null && r.MissingSince == null && r.ObserveError != null)
            .OrderBy(r => r.Address)
            .ToListAsync(cancellationToken);
        var agents = await db.Agents.AsNoTracking().CountAsync(a => !a.Disabled, cancellationToken);
        return new OverviewDto(
            repository,
            counts,
            connections.All.Count,
            agents,
            attention.Select(r => Summary(r, addresses.GetValueOrDefault(r.ResourceId, ("?", "?")))).ToList(),
            observeErrors.Select(r => new ObserveErrorDto(r.Id, r.Address, InstallationName(r), r.ObserveError!, r.ObserveErrorAt)).ToList(),
            recent.Select(r => Summary(r, addresses.GetValueOrDefault(r.ResourceId, ("?", "?")))).ToList());
    }

    private InstallationSummaryDto InstallationSummary(
        Resource installation,
        IReadOnlyList<Resource> resources,
        IReadOnlyDictionary<Guid, (Run? Active, Run? Last)> runs,
        IReadOnlyDictionary<Guid, JsonObject> observations,
        IReadOnlyDictionary<Guid, string?> observing,
        InstallationTrust? trust)
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
        return new InstallationSummaryDto(
            installation.Id,
            installation.Address,
            InstallationName(installation),
            definition?["type"]?.GetValue<string>(),
            definition?["connection"]?["endpoint"]?.GetValue<string>(),
            installation.Valid,
            !installation.ObservedOnly,
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
            definition?["host"]?.GetValue<string>(),
            vmid,
            definition?["lifecycle"]?.GetValue<string>() ?? "active",
            status,
            detail,
            resource.Valid,
            observed is null ? null : ObservedGuest(observed, [resource]),
            active is null ? null : Summary(active, resource.Address, resource.Kind),
            last is null ? null : Summary(last, resource.Address, resource.Kind),
            Workflow(resource),
            definition?["type"] is JsonValue type && type.TryGetValue<string>(out var typeText) ? typeText : null);
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
            managed?.Id,
            g["kind"]?.GetValue<string>());
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
