using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Definitions.Schemas;
using Octopushy.Definitions.Yaml;
using Octopushy.Server.Api;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;

namespace Octopushy.Server.Changes;

/// <summary>
/// Changes made in the UI. Each is one commit Octopushy writes to the lab repository through the editor's publish path: a
/// private draft ref at the commit the form was loaded from, the edit written there, merged onto the branch, validated, and
/// pushed with a lease. It is compare-and-swap on the files it touches: if any of them differs on the branch from the
/// commit the form was loaded from, nothing is committed. Then the repository is synced at once, and the change is followed
/// through the revision and the runs it proposes. See docs/changes-from-the-ui.md.
/// </summary>
public sealed partial class ChangeService(
    RunEngine engine,
    RepositoryMirror mirror,
    RevisionCache revisions,
    WorkflowsMirrors workflowMirrors,
    WorkflowCatalog catalog,
    RepositorySync sync,
    IOptions<OctopushyOptions> options,
    TimeProvider clock,
    ILogger<ChangeService> logger)
{
    /// <summary>What a change acts on, read at the commit its form was loaded from.</summary>
    private sealed record Target(Resource Resource, string Kind, string Address, string File, string Folder, string Text, LabRepository Repository,
        RepositorySettings Settings);

    /// <summary>What a form for the resource starts from, or null when there is no such guest or service.</summary>
    public async Task<ChangeFormDto?> FormAsync(OctopushyDb db, Guid resourceId, CancellationToken ct)
    {
        var resource = await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == resourceId
            && (r.Kind == ResourceKinds.Guest || r.Kind == ResourceKinds.Service), ct);
        if (resource is null)
            return null;
        var name = resource.Address.Split('/')[^1];
        var settings = await db.RepositorySettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct);
        var head = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(ct);
        if (settings?.HeadCommit is null || head is null)
            return Empty(resource, name, "Sync the lab repository first.");
        LabRepository repository;
        try
        {
            repository = await revisions.GetAsync(head, ct);
        }
        catch (GitException e)
        {
            return Empty(resource, name, $"The head revision cannot be read: {e.Message}");
        }

        var (file, json, reference, workflow) = Definition(repository, resource);
        var blocked = BlockedReason(resource, repository, file is not null, await RecordedObservedOnlyAsync(db, resource, ct));
        if (file is null || json is null)
            return Empty(resource, name, blocked ?? "The definition is not in the head revision.");
        var text = (await mirror.ReadFileAsync(head.Commit, file, ct))?.Text;
        var upgrade = reference is null ? null : await catalog.NewerMajorAsync(db, head.WorkflowsRepository, reference, ct);
        var fieldIssues = workflow?.Schema is { } schema
            ? SchemaCatalog.Evaluate(schema, Instance(resource.Kind, json)).Select(e => new FieldIssueDto(e.Pointer, e.Message)).ToList()
            : [];
        return new ChangeFormDto(resource.Id, resource.Kind, resource.Address, name, head.Commit, file, text,
            json["workflow"]?.ToString(), workflow is null ? null : WorkflowCatalog.Version(workflow, head.Commit),
            resource.Kind == ResourceKinds.Guest ? json["hardware"]?.DeepClone() : null,
            resource.Kind == ResourceKinds.Guest ? json["network"]?.DeepClone() : null,
            json["inputs"]?.DeepClone(), blocked ?? (text is null ? $"{file} is not a text file Octopushy can edit." : null),
            resource.Kind == ResourceKinds.Guest, upgrade, fieldIssues);

        static ChangeFormDto Empty(Resource resource, string name, string reason) => new(resource.Id, resource.Kind, resource.Address, name,
            null, null, null, null, null, null, null, null, reason, false, null, []);
    }

    /// <summary>What committing <paramref name="request"/> would do, with nothing committed.</summary>
    public async Task<ChangePreviewDto> PreviewAsync(OctopushyDb db, ChangeRequest request, CancellationToken ct)
    {
        var target = await TargetAsync(db, request, ct);
        var id = Guid.CreateVersion7();
        var composed = Compose(request, id, target, await mirror.TreeAsync(request.BaseCommit, ct));
        await mirror.CreateDraftAsync(id, request.BaseCommit, ct);
        try
        {
            var head = await mirror.WriteDraftAsync(id, request.BaseCommit, composed.Changes, composed.Message, ct);
            var diff = await mirror.DiffAsync(request.BaseCommit, head, ct);
            var releases = WorkflowReleaseBook.Of(db, workflowMirrors);
            var validation = await RepositoryEditor.ValidateAsync(mirror, revisions, releases, request.BaseCommit, head, ct);
            var fieldIssues = await FieldIssuesAsync(composed, head, validation.NewIssues, releases, ct);
            string? refused = null;
            if (target.Settings.HeadCommit is { } synced && synced != request.BaseCommit
                && (await mirror.ChangesAsync(request.BaseCommit, synced, ct)).FirstOrDefault(c => composed.Touches(c.Path)) is { } moved)
                refused = Stale(composed, moved.Path, synced);
            // The change's id is given when it is committed; this one only made the candidate.
            var message = composed.Message.Replace(id.ToString(), "<given when committed>", StringComparison.Ordinal);
            return new ChangePreviewDto(composed.Kind, composed.Address, composed.Subject, message, request.BaseCommit, composed.Files,
                diff, validation.Valid, validation.NewIssues.Concat(validation.FormatIssues).ToList(), fieldIssues, refused);
        }
        finally
        {
            await mirror.DeleteDraftAsync(id, CancellationToken.None);
        }
    }

    /// <summary>
    /// Commits <paramref name="request"/> as <paramref name="actor"/>, syncs at once, and returns the change; or why it was
    /// refused, with nothing committed.
    /// </summary>
    public async Task<(ChangeDto? Change, ChangeRefusedDto? Refused)> CommitAsync(ChangeRequest request, string actor, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        var (committed, refused) = await engine.ExecuteAsync(async (db, services) =>
        {
            Target? target = null;
            ComposedChange? composed = null;
            ResourceChange? change = null;
            try
            {
                target = await TargetAsync(db, request, ct);
                composed = Compose(request, id, target, await mirror.TreeAsync(request.BaseCommit, ct));
                // On record before anything is pushed: whatever happens to this request after a push, the branch never moves
                // without a change that says why, and the commit each attempt pushes is recorded before it is pushed.
                change = Record(id, target, composed, request, actor);
                change.StoreKeys = GeneratedValues.StoreKeysOf(composed);
                change.Status = ChangeStatuses.Committing;
                change.Detail = "Pushing its commit.";
                db.Changes.Add(change);
                await db.SaveChangesAsync(ct);
                var commit = await PushAsync(db, services, target.Settings, target.Address, composed, change, request.BaseCommit, actor, null, ct);
                // The push happened: what follows is recorded even if the request is gone by now.
                change.Commit = commit;
                change.Status = ChangeStatuses.Committed;
                change.Detail = "Waiting for the sync.";
                change.UpdatedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(CancellationToken.None);
                return ((ResourceChange?)change, (ChangeRefusedDto?)null);
            }
            catch (ChangeRefusal refusal)
            {
                if (target is null)
                    return ((ResourceChange?)null, (ChangeRefusedDto?)new ChangeRefusedDto(null, refusal.Reason, refusal.Message, refusal.Issues, refusal.FieldIssues));
                if (change is null)
                {
                    change = Record(id, target, composed, request, actor);
                    db.Changes.Add(change);
                }

                // Refused before any push: nothing was committed, for good. After a push was attempted, the branch may still
                // hold one of its commits, so the change is left for reconciliation and for a person to dismiss.
                var attempted = change.CandidateCommits.Count > 0;
                change.Status = !attempted ? ChangeStatuses.Refused : refusal.Unverified ? ChangeStatuses.Uncertain : ChangeStatuses.Unknown;
                change.Detail = !attempted ? refusal.Message
                    : $"{refusal.Message} A push was attempted: each sync looks for its commit on the branch, until it is found or the change is dismissed.";
                change.UpdatedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(CancellationToken.None);
                return ((ResourceChange?)null, (ChangeRefusedDto?)refusal.Dto(id, change.Status));
            }
        }, ct);
        if (committed is null)
            return (null, refused);

        try
        {
            await sync.SyncNowAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The commit is on the branch: the next poll reads it, and the change follows it then.
            logger.LogWarning(e, "Syncing after change {Change} failed; the next poll picks it up.", committed.Id);
        }

        return (await GetAsync(committed.Id, ct), null);
    }

    /// <summary>A change, its status brought up to date from its revision and runs.</summary>
    public async Task<ChangeDto?> GetAsync(Guid id, CancellationToken ct) =>
        await engine.ExecuteAsync(async (db, _) =>
        {
            var change = await db.Changes.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (change is null)
                return null;
            await ChangeTracker.RefreshAsync(db, mirror, revisions, change, clock.GetUtcNow(), ct);
            await db.SaveChangesAsync(ct);
            return await DtoAsync(db, change, ct);
        }, ct);

    /// <summary>The newest changes of a resource, or of every resource, each brought up to date.</summary>
    public async Task<IReadOnlyList<ChangeDto>> ListAsync(Guid? resourceId, int limit, CancellationToken ct) =>
        await engine.ExecuteAsync(async (db, _) =>
        {
            var changes = await db.Changes.Where(c => resourceId == null || c.ResourceId == resourceId)
                .OrderByDescending(c => c.CreatedAt).Take(Math.Clamp(limit, 1, 100)).ToListAsync(ct);
            var result = new List<ChangeDto>();
            foreach (var change in changes)
            {
                await ChangeTracker.RefreshAsync(db, mirror, revisions, change, clock.GetUtcNow(), ct);
                result.Add(await DtoAsync(db, change, ct));
            }

            await db.SaveChangesAsync(ct);
            return (IReadOnlyList<ChangeDto>)result;
        }, ct);

    /// <summary>
    /// Pushes the change, compare-and-swap on the files it touches, and returns the commit that landed. Every attempt's commit is
    /// recorded on the change before it is pushed. A commit of an earlier attempt that the branch already reaches is success,
    /// checked before anything is retried or refused, so a push whose acknowledgement was lost is never taken for a stale form.
    /// </summary>
    /// <param name="check">Asked of each candidate before it is validated, such as whether a new guest's VMID is still free.</param>
    private async Task<string> PushAsync(OctopushyDb db, IServiceProvider services, RepositorySettings settings, string address, ComposedChange composed,
        ResourceChange change, string baseCommit, string actor, Func<PublishCandidate, Task>? check, CancellationToken ct)
    {
        var id = change.Id;
        await mirror.CreateDraftAsync(id, baseCommit, ct);
        try
        {
            var head = await mirror.WriteDraftAsync(id, baseCommit, composed.Changes, composed.Message, ct);
            var releases = WorkflowReleaseBook.Of(db, workflowMirrors);
            try
            {
                var credential = await RepositoryEditor.CredentialAsync(services.GetRequiredService<VariableStore>(), settings, ct);
                var candidate = await RepositoryEditor.PushDraftAsync(mirror, settings, credential, baseCommit, head, composed.Message,
                    RepositoryEditor.AuthorOf(actor), async candidate =>
                    {
                        // An earlier attempt landed, though its push reported failure: that is this change's commit.
                        if (await ChangeReconciler.LandedAsync(mirror, change, candidate.Remote, ct) is { } landed)
                            throw new ChangeLanded(landed);
                        // Compare-and-swap: only what this change touches has to be as the form saw it; other commits are merged.
                        if (candidate.Remote != baseCommit
                            && (await mirror.ChangesAsync(baseCommit, candidate.Remote, ct)).FirstOrDefault(c => composed.Touches(c.Path)) is { } moved)
                            throw new ChangeRefusal(ChangeRefusal.Reasons.Stale, Stale(composed, moved.Path, candidate.Remote));
                        if (candidate.Unchanged || candidate.Conflicts.Count > 0)
                            throw new ChangeRefusal(ChangeRefusal.Reasons.Stale, $"{composed.Name} changed since the form was loaded; reload it.");
                        // The form's commit said managed; what is pushed is judged by what it would be merged into, and by the server's record.
                        if (await ObservedOnlyAsync(db, address, candidate.Commit!, releases, ct) is { } observed)
                            throw new ChangeRefusal(ChangeRefusal.Reasons.ObservedOnly, observed);
                        if (check is not null)
                            await check(candidate);
                        var validation = await RepositoryEditor.ValidateAsync(mirror, revisions, releases, candidate.Remote, candidate.Commit!, ct);
                        if (!validation.Valid)
                        {
                            var issues = validation.NewIssues.Concat(validation.FormatIssues).ToList();
                            var first = issues.Where(i => i.Severity == "error").Take(3).Select(i => i.Message);
                            throw new ChangeRefusal(ChangeRefusal.Reasons.Invalid, $"The change does not validate: {string.Join("; ", first)}",
                                issues, await FieldIssuesAsync(composed, candidate.Commit!, validation.NewIssues, releases, ct));
                        }

                        // Recorded before this attempt's push: whatever happens next, the branch reaching it is how it is found.
                        change.CandidateCommits = [.. change.CandidateCommits, candidate.Commit!];
                        change.UpdatedAt = clock.GetUtcNow();
                        await db.SaveChangesAsync(CancellationToken.None);
                    }, ct);
                return candidate.Commit!;
            }
            catch (ChangeLanded landed)
            {
                return landed.Commit;
            }
            catch (GitException e)
            {
                var message = $"Pushing to {settings.Branch} failed: {e.Message} Octopushy needs a repository credential that can push, "
                    + "such as a deploy key with write access.";
                if (change.CandidateCommits.Count == 0)
                    throw new ChangeRefusal(ChangeRefusal.Reasons.PushFailed, message);
                // A push reported as failed may still have landed: the branch says, if it can be read.
                var (checkedBranch, landed) = await CheckBranchAsync(settings, services, change);
                if (landed is not null)
                    return landed;
                throw new ChangeRefusal(ChangeRefusal.Reasons.PushFailed, message, unverified: !checkedBranch);
            }
        }
        finally
        {
            // Never on the request's token: a cancelled request must not leave its draft ref behind.
            await mirror.DeleteDraftAsync(id, CancellationToken.None);
        }
    }

    /// <summary>Thrown from an attempt that finds an earlier attempt's commit on the branch.</summary>
    private sealed class ChangeLanded(string commit) : Exception("An earlier attempt landed.")
    {
        public string Commit { get; } = commit;
    }

    /// <summary>Fetches the branch and finds the change's commit on it: whether the branch could be read, and the commit.</summary>
    private async Task<(bool Checked, string? Landed)> CheckBranchAsync(RepositorySettings settings, IServiceProvider services, ResourceChange change)
    {
        try
        {
            var credential = await RepositoryEditor.CredentialAsync(services.GetRequiredService<VariableStore>(), settings, CancellationToken.None);
            var head = await mirror.FetchAsync(settings.Url!, settings.Branch, credential, CancellationToken.None);
            return (true, await ChangeReconciler.LandedAsync(mirror, change, head, CancellationToken.None));
        }
        catch (GitException)
        {
            return (false, null);
        }
    }

    /// <summary>
    /// Dismisses a change whose push was attempted and is unsettled, as <paramref name="actor"/>: Octopushy stops waiting for it,
    /// and that is all. A push was attempted, so it is never "not committed" by this; one of its commits that arrives later still
    /// moves it on. Refused while one of its commits is on the branch.
    /// </summary>
    public async Task<ChangeDto> DismissAsync(Guid id, string actor, CancellationToken ct) =>
        await engine.ExecuteAsync(async (db, services) =>
        {
            var change = await db.Changes.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new RunException("There is no such change.");
            if (change.Status is not (ChangeStatuses.Uncertain or ChangeStatuses.Unknown))
                throw new RunException($"Only an uncertain or unknown change can be dismissed; this one is {change.Status}.");
            var settings = await db.RepositorySettings.AsNoTracking().SingleAsync(s => s.Id == 1, ct);
            var (checkedBranch, landed) = await CheckBranchAsync(settings, services, change);
            if (!checkedBranch)
                throw new RunException($"{settings.Branch} cannot be read now, so whether this change landed cannot be checked. Try again later.");
            if (landed is not null)
                throw new RunException($"Its commit {landed[..10]} is on {settings.Branch}: it was committed. Sync the repository to follow it.");
            var now = clock.GetUtcNow();
            change.Status = ChangeStatuses.Dismissed;
            change.DismissedBy = actor;
            change.DismissedAt = now;
            change.UpdatedAt = now;
            change.Detail = $"Dismissed by {actor}: none of its commits is on {settings.Branch} now, so Octopushy stopped waiting for it. This is "
                + "not proof that nothing was committed: each sync still looks for its commits, and one that arrives is followed.";
            await db.SaveChangesAsync(ct);
            // A dismissal alone never removes what a deploy generated: one of its commits may still land. Each sync removes it
            // under the usual guards, against the head it has just fetched.
            return await DtoAsync(db, change, ct);
        }, ct);

    /// <summary>
    /// Every store key a definition file at the branch as last fetched references, or null when it cannot be read. The mirror
    /// fetches the branch before every push, so this is at least as new as that push saw.
    /// </summary>
    private async Task<IReadOnlySet<string>?> HeadReferencedKeysAsync(OctopushyDb db, CancellationToken ct)
    {
        var settings = await db.RepositorySettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct);
        if (settings is null)
            return null;
        try
        {
            var head = await mirror.BranchHeadAsync(settings.Branch, ct) ?? settings.HeadCommit;
            return head is null ? null : GeneratedValues.ReferencedKeys(await mirror.SnapshotAsync(head, ct));
        }
        catch (GitException)
        {
            return null;
        }
    }

    /// <summary>
    /// Why the installation over <paramref name="address"/> is observed only, by the server's own record of it or by its
    /// definition in <paramref name="candidate"/>, the commit that would be pushed; null when neither says so.
    /// </summary>
    private async Task<string?> ObservedOnlyAsync(OctopushyDb db, string address, string candidate,
        Func<string, CancellationToken, Task<IReadOnlyList<WorkflowRelease>>> releases, CancellationToken ct)
    {
        var installation = RepositoryTrust.InstallationAddress(address);
        var recorded = await db.Resources.AsNoTracking().AnyAsync(r => r.Kind == ResourceKinds.Installation && r.Address == installation
            && r.RetiredAt == null && r.ObservedOnly, ct);
        var repository = (await revisions.ResolveAsync(candidate, releases, ct)).Repository;
        var declared = SafetyBoundary.ObservedOnly(repository.Resource(installation)?.Json) || repository.Installation(installation) is { Managed: false };
        return recorded || declared ? ObservedOnlyMessage(installation) : null;
    }

    private static string ObservedOnlyMessage(string installation) =>
        $"{installation} is observed only (manage: false): Octopushy changes nothing there, so nothing under it can be changed here.";

    private static string Stale(ComposedChange composed, string path, string remote) => ChangeKinds.IsCreate(composed.Kind)
        ? $"{composed.Folder} was created on the branch meanwhile: {path} is there at {remote[..Math.Min(10, remote.Length)]}. Choose another name."
        : $"{composed.Name} changed since the form was loaded: {path} is different at {remote[..Math.Min(10, remote.Length)]}. Reload it and make the change again.";

    private ResourceChange Record(Guid id, Target target, ComposedChange? composed, ChangeRequest request, string actor)
    {
        var now = clock.GetUtcNow();
        return new ResourceChange
        {
            Id = id,
            Kind = composed?.Kind ?? (request.Kind is ChangeKinds.Remove or ChangeKinds.Upgrade ? request.Kind : ChangeKinds.Edit),
            ResourceKind = target.Kind,
            ResourceId = target.Resource.Id,
            Address = target.Address,
            Author = actor,
            BaseCommit = request.BaseCommit,
            Message = composed?.Subject ?? $"{target.Address.Split('/')[^1]}: {request.Kind}",
            Files = composed?.Files.ToList() ?? [target.File],
            Edited = composed?.Edited?.ToJsonString(),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static ComposedChange Compose(ChangeRequest request, Guid id, Target target, IReadOnlyList<TreeFile> tree) =>
        ChangeComposer.Compose(request, id, target.Kind, target.Address, target.File, target.Folder, target.Text, tree);

    /// <summary>The resource a change acts on and its definition at the change's base commit, or a refusal saying why it cannot be changed.</summary>
    private async Task<Target> TargetAsync(OctopushyDb db, ChangeRequest request, CancellationToken ct)
    {
        if (request.BaseCommit is null || !CommitPattern().IsMatch(request.BaseCommit))
            throw new ChangeRefusal(ChangeRefusal.Reasons.Stale, "The form does not say which commit it was loaded from; reload it.");
        var resource = await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.ResourceId
            && (r.Kind == ResourceKinds.Guest || r.Kind == ResourceKinds.Service), ct)
            ?? throw new ChangeRefusal(ChangeRefusal.Reasons.Unavailable, "There is no such guest or service.");
        var settings = await db.RepositorySettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct);
        if (settings?.Url is null || settings.HeadCommit is null)
            throw new ChangeRefusal(ChangeRefusal.Reasons.Unavailable, "Connect the lab repository and wait for its first sync.");
        LabRepository repository;
        try
        {
            repository = (await revisions.ResolveAsync(request.BaseCommit, WorkflowReleaseBook.Of(db, workflowMirrors), ct)).Repository;
        }
        catch (GitException)
        {
            throw new ChangeRefusal(ChangeRefusal.Reasons.Stale, $"The commit the form was loaded from, {request.BaseCommit[..10]}, is not known here; reload it.");
        }

        var (file, _, _, _) = Definition(repository, resource);
        var recorded = await RecordedObservedOnlyAsync(db, resource, ct);
        if (BlockedReason(resource, repository, file is not null, recorded) is { } blocked)
            throw new ChangeRefusal(!options.Value.RepositoryEditor.PublishEnabled ? ChangeRefusal.Reasons.Disabled
                : recorded ? ChangeRefusal.Reasons.ObservedOnly : Reason(resource, repository), blocked);
        var text = (await mirror.ReadFileAsync(request.BaseCommit, file!, ct))?.Text
            ?? throw new ChangeRefusal(ChangeRefusal.Reasons.Unsupported, $"{file} is not a text file Octopushy can edit.");
        var folder = resource.Kind == ResourceKinds.Guest ? repository.Guest(resource.Address)!.Directory : repository.Service(resource.Address)!.Directory;
        return new Target(resource, resource.Kind, resource.Address, file!, folder, text, repository, settings);
    }

    /// <summary>Why the resource cannot be changed from a form now, or null.</summary>
    private static Task<bool> RecordedObservedOnlyAsync(OctopushyDb db, Resource resource, CancellationToken ct)
    {
        var installation = RepositoryTrust.InstallationAddress(resource.Address);
        return db.Resources.AsNoTracking().AnyAsync(r => r.Kind == ResourceKinds.Installation && r.Address == installation && r.RetiredAt == null
            && r.ObservedOnly, ct);
    }

    private string? BlockedReason(Resource resource, LabRepository repository, bool defined, bool recordedObservedOnly)
    {
        if (!options.Value.RepositoryEditor.PublishEnabled)
            return "Changes from the UI are commits to the lab repository, and committing is off. Set Octopushy__RepositoryEditor__PublishEnabled=true, "
                + "with a repository credential that can push, such as a deploy key with write access.";
        var installation = RepositoryTrust.InstallationAddress(resource.Address);
        if (recordedObservedOnly || repository.Installation(installation) is { Managed: false })
            return ObservedOnlyMessage(installation);
        if (resource.RetiredAt is not null)
            return "This guest is retired.";
        if (resource.MissingSince is not null || !defined)
            return "The folder is deleted in the repository; restore it to change it.";
        return null;
    }

    private static string Reason(Resource resource, LabRepository repository) =>
        repository.Installation(RepositoryTrust.InstallationAddress(resource.Address)) is { Managed: false }
            ? ChangeRefusal.Reasons.ObservedOnly
            : ChangeRefusal.Reasons.Unavailable;

    private static (string? File, JsonObject? Json, WorkflowReference? Reference, WorkflowDefinition? Workflow) Definition(LabRepository repository, Resource resource)
    {
        if (resource.Kind == ResourceKinds.Guest && repository.Guest(resource.Address) is { } guest)
            return (guest.File, guest.Json, guest.WorkflowReference, guest.Workflow);
        if (resource.Kind == ResourceKinds.Service && repository.Service(resource.Address) is { } service)
            return (service.File, service.Json, service.WorkflowReference, service.Workflow);
        // A definition that does not parse is still a file of the resource: it can be fixed from its form.
        var entry = repository.Resource(resource.Address);
        return (entry?.File ?? resource.DefinitionFile, entry?.Json, null, null);
    }

    /// <summary>What a workflow's inputs schema validates for a resource: a guest's hardware, network and inputs, a service's inputs.</summary>
    private static JsonObject Instance(string kind, JsonObject json) => kind == ResourceKinds.Guest
        ? new JsonObject
        {
            ["hardware"] = json["hardware"]?.DeepClone(),
            ["network"] = json["network"]?.DeepClone(),
            ["inputs"] = json["inputs"]?.DeepClone() ?? new JsonObject(),
        }
        : new JsonObject { ["inputs"] = json["inputs"]?.DeepClone() ?? new JsonObject() };

    /// <summary>
    /// The candidate's problems placed on form fields: the resolved workflow's schema errors, at their pointers, and every
    /// other new issue in the edited file at the field on its line.
    /// </summary>
    private async Task<IReadOnlyList<FieldIssueDto>> FieldIssuesAsync(ComposedChange composed, string candidate, IReadOnlyList<IssueDto> newIssues,
        Func<string, CancellationToken, Task<IReadOnlyList<WorkflowRelease>>> releases, CancellationToken ct)
    {
        if (composed.NewText is null || composed.Desired is null)
            return [];
        var repository = (await revisions.ResolveAsync(candidate, releases, ct)).Repository;
        var workflow = composed.ResourceKind == ResourceKinds.Guest ? repository.Guest(composed.Address)?.Workflow : repository.Service(composed.Address)?.Workflow;
        var issues = new List<FieldIssueDto>();
        if (workflow?.Schema is { } schema)
            issues.AddRange(SchemaCatalog.Evaluate(schema, Instance(composed.ResourceKind, composed.Desired)).Select(e => new FieldIssueDto(e.Pointer, e.Message)));
        IReadOnlyDictionary<string, int> lines;
        try
        {
            lines = YamlDocumentReader.Read(composed.NewText).Lines;
        }
        catch (YamlReadException)
        {
            lines = new Dictionary<string, int>();
        }

        // The schema's own errors are placed above, precisely; the loader reports them again by line.
        foreach (var issue in newIssues.Where(i => i.File == composed.DefinitionFile && !i.Message.Contains("(workflow ", StringComparison.Ordinal)))
        {
            var at = issue.Line is { } line ? lines.Where(l => l.Value == line).Select(l => l.Key).ToList() : [];
            issues.Add(new FieldIssueDto(CommonPrefix(at), issue.Message));
        }

        return issues.Distinct().ToList();
    }

    /// <summary>The deepest pointer every pointer in <paramref name="pointers"/> is under: the field a line of YAML holds.</summary>
    private static string CommonPrefix(IReadOnlyList<string> pointers)
    {
        if (pointers.Count == 0)
            return "";
        var parts = pointers.Select(p => p.Split('/')).ToList();
        var common = new List<string>();
        for (var i = 0; i < parts.Min(p => p.Length); i++)
        {
            if (parts.Any(p => p[i] != parts[0][i]))
                break;
            common.Add(parts[0][i]);
        }

        return string.Join('/', common);
    }

    private static async Task<ChangeDto> DtoAsync(OctopushyDb db, ResourceChange change, CancellationToken ct)
    {
        var settings = await db.RepositorySettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct);
        var runs = await db.Runs.AsNoTracking().Include(r => r.Steps).ThenInclude(s => s.Attempts).AsSplitQuery()
            .Where(r => change.RunIds.Contains(r.Id)).OrderBy(r => r.CreatedAt).ToListAsync(ct);
        var generated = await db.GeneratedValues.AsNoTracking().Where(v => v.ChangeId == change.Id).OrderBy(v => v.CreatedAt).ToListAsync(ct);
        return new ChangeDto(change.Id, change.Kind, change.ResourceKind, change.ResourceId, change.Address, change.Author, change.Message,
            change.BaseCommit, change.Commit, change.Commit is null ? null : CommitUrl(settings?.Url, change.Commit), change.RevisionId,
            change.Status, change.Detail, change.Files, runs.Select(r => Views.Summary(r, change.Address, change.ResourceKind)).ToList(),
            change.CreatedAt, change.SyncedAt, change.UpdatedAt, change.SupersededBy,
            change.SupersededBy is null ? null : CommitUrl(settings?.Url, change.SupersededBy), change.DismissedBy, change.DismissedAt,
            ChangeStatuses.IsFinal(change.Status), ChangeStatuses.IsNotCommitted(change), change.Vmid, change.PreApproval,
            change.PreApprovalDetail, generated.Select(v => new GeneratedValueStateDto(v.Secret, v.Key, v.Generator, v.State, v.CleanupPending,
                v.Detail, v.CleanupError)).ToList(), generated.Count(v => v.CleanupPending));
    }

    /// <summary>The commit's page when the repository is on GitHub, reached over HTTPS or SSH.</summary>
    public static string? CommitUrl(string? repository, string commit)
    {
        if (repository is null || GitHub().Match(repository.Trim()) is not { Success: true } match)
            return null;
        return $"https://github.com/{match.Groups["owner"].Value}/{match.Groups["name"].Value}/commit/{commit}";
    }

    [GeneratedRegex(@"^(?:https://(?:[^@/]+@)?github\.com/|ssh://git@github\.com/|git@github\.com:)(?<owner>[A-Za-z0-9-]+)/(?<name>[A-Za-z0-9._-]+?)(?:\.git)?/?$")]
    private static partial Regex GitHub();

    [GeneratedRegex("^([0-9a-f]{40}|[0-9a-f]{64})$")]
    private static partial Regex CommitPattern();
}
