using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Octopushy.Definitions;
using Octopushy.Definitions.Yaml;
using Octopushy.Server.Agents;
using Octopushy.Server.Api;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;

namespace Octopushy.Server.Changes;

/// <summary>The values a deploy can have Octopushy generate for a secret, as a workflow requests them in <c>ui.secrets</c>.</summary>
public static class SecretGenerators
{
    /// <summary>A one-time enrollment token for an agent, made as the Agents page makes one.</summary>
    public const string AgentEnrollmentToken = "agent-enrollment-token";

    /// <summary>A random password, URL-safe, 32 characters unless the hint gives a <c>length</c> from 16 to 128.</summary>
    public const string RandomPassword = "random-password";

    public static bool IsKnown(string generator) => generator is AgentEnrollmentToken or RandomPassword;
}

/// <summary>
/// The workflow wizard's deploys: a new guest's or service's folder written as one commit, through the same path as every
/// other change. The wizard picks a workflow, an installation and host, fills the workflow's inputs schema, chooses or
/// generates the secrets, reviews the exact file, and deploys. See docs/changes-from-the-ui.md.
/// </summary>
public sealed partial class ChangeService
{
    /// <summary>The smallest VMID Proxmox gives a guest, and where the next free one is looked for unless a workflow says otherwise.</summary>
    public const int VmidFloor = 100;

    private const int MaxVmid = 999_999_999;

    /// <summary>Where a deploy writes, and the workflow version its reference resolves to now.</summary>
    private sealed record DeployTarget(
        string Kind,
        string ResourceKind,
        string Address,
        string Name,
        string Folder,
        string File,
        string Installation,
        LabRepository Repository,
        RepositorySettings Settings,
        WorkflowReference Reference,
        WorkflowVersionDto Workflow,
        string Release,
        IReadOnlyList<string> AgentLabels);

    /// <summary>An installation as one commit defines it: what its VMIDs are checked against at that commit.</summary>
    private sealed record InstallationAt(string Address, string Folder, IReadOnlyCollection<string> Hosts, string Type, ConnectionDefinition Connection)
    {
        public static InstallationAt Of(InstallationDefinition installation) =>
            new(installation.Address, FolderOf(installation), installation.Hosts.Keys.ToList(), installation.Type, installation.Connection);
    }

    /// <summary>A value to generate at commit, into <paramref name="Key"/>.</summary>
    private sealed record Generation(string Secret, string Key, string Generator, string Detail, string? TokenName, IReadOnlyList<string> Labels, int Length);

    public async Task<DeployTargetsDto> TargetsAsync(OctopushyDb db, CancellationToken ct)
    {
        var settings = await db.RepositorySettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct);
        var head = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(ct);
        if (settings?.HeadCommit is null || head is null)
            return new DeployTargetsDto(null, "Connect the lab repository and wait for its first sync.", [], []);
        LabRepository repository;
        try
        {
            repository = await revisions.GetAsync(head, ct);
        }
        catch (GitException e)
        {
            return new DeployTargetsDto(null, $"The head revision cannot be read: {e.Message}", [], []);
        }

        var blocked = options.Value.RepositoryEditor.PublishEnabled ? null
            : "A deploy is a commit to the lab repository, and committing is off. Set Octopushy__RepositoryEditor__PublishEnabled=true, "
              + "with a repository credential that can push, such as a deploy key with write access.";
        var installations = new List<DeployInstallationDto>();
        foreach (var installation in repository.Installations.OrderBy(i => i.Address, StringComparer.Ordinal))
        {
            var observation = await ObservationAsync(db, installation.Address, ct);
            var observedHosts = (observation?.Data["hosts"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var hosts = installation.Hosts.Keys.Order(StringComparer.Ordinal)
                .Select(h => new DeployHostDto(h, true, observedHosts.Any(o => o["name"]?.ToString() == h),
                    HostStatus(observedHosts.FirstOrDefault(o => o["name"]?.ToString() == h))))
                .Concat(observedHosts.Select(o => o["name"]?.ToString()).OfType<string>().Where(h => !installation.Hosts.ContainsKey(h)).Distinct()
                    .Select(h => new DeployHostDto(h, false, true, HostStatus(observedHosts.First(o => o["name"]?.ToString() == h)))))
                .ToList();
            var guests = repository.Guests.Where(g => g.InstallationAddress == installation.Address).OrderBy(g => g.Id, StringComparer.Ordinal)
                .Select(g => new DeployGuestDto(g.Address, g.Id, g.WorkflowReference.ToString(), repository.IsValid(g.Address))).ToList();
            // Observed only by its definition, or by the server's record of it, which outlives a definition that stops saying so.
            var managed = installation.Managed && !await RecordedObservedOnlyAsync(db, installation.Address, ct);
            var resourceId = await InstallationIdAsync(db, installation.Address, ct);
            installations.Add(new DeployInstallationDto(installation.Address, installation.Lab, installation.DisplayName, installation.Type,
                managed, managed ? null : ObservedOnly(installation.Address), hosts, guests, LabLabels(repository, installation.Lab),
                observation?.ObservedAt, resourceId,
                managed ? await ObservationProblemAsync(db, InstallationAt.Of(installation), ct) : null));
        }

        return new DeployTargetsDto(head.Commit, blocked, installations, repository.Templates.Keys.Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>The next free VMID on an installation at or above <paramref name="from"/>, or null when there is no such installation.</summary>
    public async Task<NextVmidDto?> NextVmidAsync(OctopushyDb db, string installation, int? from, CancellationToken ct)
    {
        var head = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(ct);
        if (head is null)
            return null;
        var repository = await revisions.GetAsync(head, ct);
        if (repository.Installation(installation) is not { } defined)
            return null;
        var floor = Math.Clamp(from ?? VmidFloor, VmidFloor, MaxVmid);
        try
        {
            var taken = await TakenVmidsAsync(db, InstallationAt.Of(defined), head.Commit, null, ct);
            return new NextVmidDto(installation, NextFree(taken, floor), floor);
        }
        catch (ChangeRefusal unknown)
        {
            // Without a reliable picture of the installation, no number is offered.
            return new NextVmidDto(installation, null, floor, unknown.Message);
        }
    }

    /// <summary>What deploying <paramref name="request"/> would commit, with nothing committed and nothing generated.</summary>
    public async Task<ChangePreviewDto> PreviewDeployAsync(OctopushyDb db, DeployRequest request, CancellationToken ct)
    {
        var target = await DeployTargetAsync(db, request, ct);
        var id = Guid.CreateVersion7();
        var (composed, generations) = await ComposeDeployAsync(db, request, id, target, ct);
        await mirror.CreateDraftAsync(id, request.BaseCommit, ct);
        try
        {
            var head = await mirror.WriteDraftAsync(id, request.BaseCommit, composed.Changes, composed.Message, ct);
            var diff = await mirror.DiffAsync(request.BaseCommit, head, ct);
            var releases = WorkflowReleaseBook.Of(db, workflowMirrors);
            var validation = await RepositoryEditor.ValidateAsync(mirror, revisions, releases, request.BaseCommit, head, ct);
            var fieldIssues = await FieldIssuesAsync(composed, head, validation.NewIssues, releases, ct);
            string? refused = null;
            int? next = null;
            if (target.Settings.HeadCommit is { } synced && synced != request.BaseCommit
                && (await mirror.ChangesAsync(request.BaseCommit, synced, ct)).FirstOrDefault(c => composed.Touches(c.Path)) is { } moved)
                refused = Stale(composed, moved.Path, synced);
            if (refused is null && target.ResourceKind == ResourceKinds.Guest)
            {
                try
                {
                    await CheckVmidAsync(db, target, request.Vmid!.Value, target.Settings.HeadCommit ?? request.BaseCommit, null, null, ct);
                }
                catch (ChangeRefusal taken)
                {
                    (refused, next) = (taken.Message, taken.NextVmid);
                    fieldIssues = [.. fieldIssues, .. taken.FieldIssues];
                }
            }

            var message = composed.Message.Replace(id.ToString(), "<given when committed>", StringComparison.Ordinal);
            return new ChangePreviewDto(composed.Kind, composed.Address, composed.Subject, message, request.BaseCommit, composed.Files, diff,
                validation.Valid, validation.NewIssues.Concat(validation.FormatIssues).ToList(), fieldIssues, refused,
                generations.Select(g => new GeneratedValueDto(g.Secret, g.Key, g.Generator, g.Detail)).ToList(), next,
                target.Release, ReviewDigest(target, composed));
        }
        finally
        {
            await mirror.DeleteDraftAsync(id, ct);
        }
    }

    /// <summary>
    /// Commits a deploy as <paramref name="actor"/>: checks the VMID again, records the change as <c>committing</c> with the
    /// store keys it will fill, generates the values it asks for, pushes the new folder, and syncs at once. A deploy refused
    /// before its push removes what it generated at once; one whose push was attempted is settled by <see cref="ChangeReconciler"/>
    /// or a person, and <see cref="GeneratedValues"/> removes what it generated once it is final and nothing at the head uses it.
    /// </summary>
    public async Task<(ChangeDto? Change, ChangeRefusedDto? Refused)> DeployAsync(DeployRequest request, string actor, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        var (committed, refused) = await engine.ExecuteAsync(async (db, services) =>
        {
            DeployTarget? target = null;
            ComposedChange? composed = null;
            ResourceChange? change = null;
            try
            {
                target = await DeployTargetAsync(db, request, ct);
                (composed, var generations) = await ComposeDeployAsync(db, request, id, target, ct);
                var deployed = target;
                CheckReviewed(request, deployed, composed);
                if (deployed.ResourceKind == ResourceKinds.Guest)
                    await CheckVmidAsync(db, deployed, request.Vmid!.Value, deployed.Settings.HeadCommit ?? request.BaseCommit, null, null, ct);
                // On record before anything is generated or pushed, with each value it will make: whatever happens to this
                // request, the branch never moves, and no value is left in the store, without a change that says why.
                change = RecordDeploy(id, deployed, composed, request, actor);
                change.StoreKeys = GeneratedValues.StoreKeysOf(composed);
                change.Status = ChangeStatuses.Committing;
                change.Detail = "Pushing its commit.";
                db.Changes.Add(change);
                var now = clock.GetUtcNow();
                var values = generations.Select(g => new GeneratedValue
                {
                    Id = Guid.CreateVersion7(), ChangeId = id, Secret = g.Secret, Key = g.Key, Generator = g.Generator,
                    StoreDescription = $"Generated at deploy for {deployed.Address} ({g.Generator}, change {id}).", CreatedAt = now, UpdatedAt = now,
                }).ToList();
                db.GeneratedValues.AddRange(values);
                await db.SaveChangesAsync(ct);
                await GenerateAsync(db, services, generations, values, ct);
                var releases = WorkflowReleaseBook.Of(db, workflowMirrors);
                Func<PublishCandidate, Task> check = async candidate =>
                {
                    // At the commit this attempt would land: what resolves there must be what was reviewed, and the VMID
                    // must still be free on the installation as that commit defines it.
                    var landing = (await revisions.ResolveAsync(candidate.Commit!, releases, ct)).Repository;
                    await CheckLandingAsync(request, deployed, landing, candidate.Commit!, ct);
                    if (deployed.ResourceKind == ResourceKinds.Guest)
                        await CheckVmidAsync(db, deployed, request.Vmid!.Value, candidate.Remote, landing, id, ct);
                };
                var commit = await PushAsync(db, services, deployed.Settings, deployed.Address, composed, change, request.BaseCommit, actor, check, ct);
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
                if (target is null || composed is null)
                    return ((ResourceChange?)null, (ChangeRefusedDto?)refusal.Dto());
                if (change is null)
                {
                    change = RecordDeploy(id, target, composed, request, actor);
                    db.Changes.Add(change);
                }

                // Refused before any push: nothing was committed, for good, and what it generated goes. After a push was
                // attempted, one of its commits may still land: the change is left for reconciliation, or for a person to dismiss.
                var attempted = change.CandidateCommits.Count > 0;
                change.Status = !attempted ? ChangeStatuses.Refused : refusal.Unverified ? ChangeStatuses.Uncertain : ChangeStatuses.Unknown;
                change.Detail = !attempted ? refusal.Message
                    : $"{refusal.Message} A push was attempted: each sync looks for its commit on the branch, until it is found or the change is dismissed.";
                if (!attempted)
                {
                    change.PreApproval = null;
                    change.PreApprovalDetail = null;
                }

                change.UpdatedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(CancellationToken.None);
                // Refused for good: what it generated goes now, under the same guards as at every sync.
                if (!attempted && await HeadReferencedKeysAsync(db, CancellationToken.None) is { } referenced)
                    await GeneratedValues.CleanUpAsync(db, mirror, referenced, logger, clock.GetUtcNow(), id);
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
            logger.LogWarning(e, "Syncing after deploy {Change} failed; the next poll picks it up.", committed.Id);
        }

        return (await GetAsync(committed.Id, ct), null);
    }

    private ResourceChange RecordDeploy(Guid id, DeployTarget target, ComposedChange composed, DeployRequest request, string actor)
    {
        var now = clock.GetUtcNow();
        return new ResourceChange
        {
            Id = id,
            Kind = target.Kind,
            ResourceKind = target.ResourceKind,
            ResourceId = null,
            Address = target.Address,
            Author = actor,
            BaseCommit = request.BaseCommit,
            Message = composed.Subject,
            Files = composed.Files.ToList(),
            Vmid = target.ResourceKind == ResourceKinds.Guest ? request.Vmid : null,
            PreApproval = request.ApproveAtDeploy ? PreApprovals.Requested : null,
            PreApprovalDetail = request.ApproveAtDeploy ? "The first plan is approved at deploy if it only creates." : null,
            // What a later revision has to keep for this deploy to be the one applied: the new definition as written.
            Edited = composed.Edited?.ToJsonString(),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Where a deploy writes, or a refusal saying why it cannot be deployed there.</summary>
    private async Task<DeployTarget> DeployTargetAsync(OctopushyDb db, DeployRequest request, CancellationToken ct)
    {
        if (!options.Value.RepositoryEditor.PublishEnabled)
            throw new ChangeRefusal(ChangeRefusal.Reasons.Disabled, "A deploy is a commit to the lab repository, and committing is off. "
                + "Set Octopushy__RepositoryEditor__PublishEnabled=true, with a repository credential that can push.");
        if (request.BaseCommit is null || !CommitPattern().IsMatch(request.BaseCommit))
            throw new ChangeRefusal(ChangeRefusal.Reasons.Stale, "The wizard does not say which commit it was loaded from; reload it.");
        var name = request.Name?.Trim() ?? "";
        if (!IdPattern().IsMatch(name))
            throw new ChangeRefusal(ChangeRefusal.Reasons.Invalid,
                $"'{name}' is not a valid name: lowercase letters, digits and hyphens, starting and ending with a letter or digit, up to 63 characters.",
                fieldIssues: [new FieldIssueDto("/name", "Lowercase letters, digits and hyphens, starting and ending with a letter or digit.")]);
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
            throw new ChangeRefusal(ChangeRefusal.Reasons.Stale, $"The commit the wizard was loaded from, {request.BaseCommit[..10]}, is not known here; reload it.");
        }

        string resourceKind, address, folder, file, installationAddress;
        if (request.Kind == ChangeKinds.CreateGuest)
        {
            var installation = repository.Installation(request.Parent?.Trim() ?? "")
                ?? throw new ChangeRefusal(ChangeRefusal.Reasons.Unavailable, $"There is no installation {request.Parent} in the repository.",
                    fieldIssues: [new FieldIssueDto("/parent", "Choose an installation.")]);
            if (!installation.Managed || await RecordedObservedOnlyAsync(db, installation.Address, ct))
                throw new ChangeRefusal(ChangeRefusal.Reasons.ObservedOnly, ObservedOnly(installation.Address),
                    fieldIssues: [new FieldIssueDto("/parent", ObservedOnly(installation.Address))]);
            if (string.IsNullOrWhiteSpace(request.Host))
                throw new ChangeRefusal(ChangeRefusal.Reasons.Invalid, "Choose the host the guest runs on.", fieldIssues: [new FieldIssueDto("/host", "Choose a host.")]);
            if (!installation.Hosts.ContainsKey(request.Host))
                throw new ChangeRefusal(ChangeRefusal.Reasons.Invalid,
                    $"{request.Host} is not a host of {installation.Address}: declare it under hosts in {installation.File} first.",
                    fieldIssues: [new FieldIssueDto("/host", $"Not declared in {installation.File}.")]);
            if (request.Vmid is not (>= VmidFloor and <= MaxVmid))
                throw new ChangeRefusal(ChangeRefusal.Reasons.Invalid, $"A VMID is a number from {VmidFloor} to {MaxVmid}.",
                    fieldIssues: [new FieldIssueDto("/vmid", $"A number from {VmidFloor}.")]);
            if (request.Template is { } template && !repository.Templates.ContainsKey(template))
                throw new ChangeRefusal(ChangeRefusal.Reasons.Invalid, $"There is no template {template} in the repository.",
                    fieldIssues: [new FieldIssueDto("/template", "Choose a template the repository defines.")]);
            resourceKind = ResourceKinds.Guest;
            installationAddress = installation.Address;
            address = $"{installation.Address}/{name}";
            folder = $"{Path.GetDirectoryName(installation.File)!.Replace('\\', '/')}/guests/{name}";
            file = $"{folder}/guest.yaml";
        }
        else if (request.Kind == ChangeKinds.CreateService)
        {
            var guest = repository.Guest(request.Parent?.Trim() ?? "")
                ?? throw new ChangeRefusal(ChangeRefusal.Reasons.Unavailable, $"There is no guest {request.Parent} in the repository.",
                    fieldIssues: [new FieldIssueDto("/parent", "Choose a guest.")]);
            if (repository.Installation(guest.InstallationAddress) is { Managed: false } || await RecordedObservedOnlyAsync(db, guest.InstallationAddress, ct))
                throw new ChangeRefusal(ChangeRefusal.Reasons.ObservedOnly, ObservedOnly(guest.InstallationAddress));
            if (guest.Retired)
                throw new ChangeRefusal(ChangeRefusal.Reasons.Unavailable, $"{guest.Address} is retired: deploy the service on another guest.");
            if (request.Host is not null || request.Vmid is not null || request.Template is not null || request.Hardware is not null || request.Network is not null
                || request.AccessUser is not null || request.HostKey is not null)
                throw new ChangeRefusal(ChangeRefusal.Reasons.Invalid, "A service has inputs and secrets only: no host, VMID, template, hardware, network or access.");
            resourceKind = ResourceKinds.Service;
            installationAddress = guest.InstallationAddress;
            address = $"{guest.Address}/{name}";
            folder = $"{guest.Directory}/services/{name}";
            file = $"{folder}/service.yaml";
        }
        else
        {
            throw new ChangeRefusal(ChangeRefusal.Reasons.Unsupported, "A deploy creates a guest or a service.");
        }

        var tree = await mirror.TreeAsync(request.BaseCommit, ct);
        if (tree.Any(f => f.Path.StartsWith(folder + "/", StringComparison.Ordinal)) || repository.Resource(address) is not null)
            throw new ChangeRefusal(ChangeRefusal.Reasons.Exists, $"{folder} is already in the repository: choose another name.",
                fieldIssues: [new FieldIssueDto("/name", $"{name} is already there.")]);

        // The wizard deploys VMs and services; a container guest is written in Git, so a container workflow is refused here.
        var (reference, workflow, release) = await ResolveWorkflowAsync(db, request.Workflow,
            resourceKind == ResourceKinds.Guest ? WorkflowKinds.Vm : WorkflowKinds.Service, ct);
        var lab = address.Split('/')[0];
        return new DeployTarget(request.Kind, resourceKind, address, name, folder, file, installationAddress, repository,
            settings, reference, workflow, release, LabLabels(repository, lab));
    }

    /// <summary>The release a deploy's reference resolves to now, as the wizard offered it.</summary>
    private async Task<(WorkflowReference, WorkflowVersionDto, string)> ResolveWorkflowAsync(OctopushyDb db, string? text, string manages, CancellationToken ct)
    {
        var reference = WorkflowReference.Parse(text?.Trim());
        if (reference is null)
            throw new ChangeRefusal(ChangeRefusal.Reasons.Invalid, "Name a workflow with its version: <id>@<major> or <id>@<major>.<minor>.",
                fieldIssues: [new FieldIssueDto("/workflow", "Name a workflow with its version: <id>@<major> or <id>@<major>.<minor>.")]);
        var listed = await catalog.ListAsync(db, reference.Id, ct);
        var version = listed.Workflows.SelectMany(w => w.Versions)
            .Where(v => v.Usable && WorkflowVersion.TryParse(v.Version, out var parsed) && reference.Matches(parsed))
            .OrderByDescending(v => WorkflowVersion.TryParse(v.Version, out var parsed) ? parsed : default)
            .FirstOrDefault()
            ?? throw new ChangeRefusal(ChangeRefusal.Reasons.Invalid, $"No usable release of {reference.Id} matches {reference}.",
                fieldIssues: [new FieldIssueDto("/workflow", $"No usable release matches {reference}.")]);
        if (version.Manages != manages)
            throw new ChangeRefusal(ChangeRefusal.Reasons.Invalid, $"{reference} manages {version.Manages ?? "nothing"}, not a {manages}.",
                fieldIssues: [new FieldIssueDto("/workflow", $"It manages {version.Manages}, not a {manages}.")]);
        // From a workflows repository a release is its tag's commit, which never moves, modules included; a workflow kept in the lab
        // repository is what its runs take at the head revision the catalog read: its folder and every stage's root.
        var release = listed.Source == "repository"
            ? ReleaseIdentity(version.Id, version.Version, version.Commit, null)
            : LocalRelease(await LocalWorkflowAsync(db, version, ct));
        return (reference, version, release);
    }

    /// <summary>The new definition as one file, and the values its secrets have generated at commit.</summary>
    private async Task<(ComposedChange, IReadOnlyList<Generation>)> ComposeDeployAsync(OctopushyDb db, DeployRequest request, Guid id, DeployTarget target,
        CancellationToken ct)
    {
        var guest = target.ResourceKind == ResourceKinds.Guest;
        var comments = new Dictionary<string, string>();
        var document = new JsonObject { ["schemaVersion"] = 1 };
        if (guest)
        {
            document["type"] = "vm";
            document["host"] = request.Host;
            document["vmid"] = request.Vmid;
            comments["vmid"] = $"free on {target.Installation} when deployed";
            if (request.Template is { } template)
                document["template"] = template;
        }

        var reference = target.Reference;
        document["workflow"] = reference.ToString();
        comments["workflow"] = reference.Minor is null
            ? $"the newest {reference.Major}.x; {reference.Id}@{target.Workflow.Version} pins this one"
            : $"pinned; {reference.Id}@{reference.Major} follows the newest {reference.Major}.x";
        foreach (var (section, value) in new[] { ("hardware", request.Hardware), ("network", request.Network), ("inputs", request.Inputs) })
        {
            if (!guest && section != "inputs" || value is null)
                continue;
            var normalized = YamlEditor.Normalize(value)!.AsObject();
            if (normalized.Count > 0)
                document[section] = normalized;
        }

        var generations = await SecretsAsync(db, request, target, document, comments, ct);
        if (guest && (request.AccessUser is not null || request.HostKey is not null))
        {
            var access = new JsonObject();
            if (!string.IsNullOrWhiteSpace(request.AccessUser))
                access["user"] = request.AccessUser.Trim();
            if (!string.IsNullOrWhiteSpace(request.HostKey))
            {
                CheckKey(request.HostKey.Trim(), "/hostKey");
                access["hostKey"] = new JsonObject { ["store"] = request.HostKey.Trim() };
                comments["access.hostKey"] = "a plain entry: set it from the console after the first boot";
            }

            if (access.Count > 0)
                document["access"] = access;
        }

        var header = new List<string>
        {
            $"{target.Name}: deployed from {reference.Id}@{target.Workflow.Version} with Octopushy's workflow wizard.",
        };
        if (target.Workflow.Description is { Length: > 0 } description)
            header.Add(description.ReplaceLineEndings(" "));
        string text;
        try
        {
            text = YamlWriter.Write(document, comments, header);
        }
        catch (YamlEditException e)
        {
            throw new ChangeRefusal(ChangeRefusal.Reasons.Unsupported, e.Message);
        }

        var where = guest ? $"{target.Installation}, host {request.Host}, VMID {request.Vmid}" : target.Address[..target.Address.LastIndexOf('/')];
        var subject = ChangeComposer.Truncate($"{target.Name}: deploy {reference} on {where}", 200);
        var details = generations.Select(g => $"{g.Secret}: generated, {g.Detail}, in {g.Key}").ToList();
        var composed = new ComposedChange(target.Kind, target.ResourceKind, target.Address, target.Name, target.File,
            [new Git.DraftFileChange(target.File, text)], [target.File], target.Folder, subject, ChangeComposer.Message(subject, details, id), text, document,
            Written(document));
        return (composed, generations);
    }

    /// <summary>
    /// The definition's <c>secrets</c>: each one the workflow declares needs a store key or a generated value, and a value is
    /// generated only where the workflow's <c>ui.secrets</c> asks for that generator.
    /// </summary>
    private async Task<IReadOnlyList<Generation>> SecretsAsync(OctopushyDb db, DeployRequest request, DeployTarget target, JsonObject document,
        Dictionary<string, string> comments, CancellationToken ct)
    {
        var hints = target.Workflow.Ui?["secrets"] as JsonObject;
        var known = target.Workflow.Secrets.Concat(hints?.Select(p => p.Key) ?? []).ToHashSet(StringComparer.Ordinal);
        var chosen = request.Secrets ?? new Dictionary<string, SecretChoice>();
        foreach (var name in target.Workflow.Secrets.Where(n => !chosen.ContainsKey(n)))
            throw new ChangeRefusal(ChangeRefusal.Reasons.Invalid, $"{target.Reference.Id} needs the secret {name}: choose a store key for it, or have one generated.",
                fieldIssues: [new FieldIssueDto($"/secrets/{name}", "Choose a store key, or have one generated.")]);
        // A secret the workflow needs only with some inputs, such as a feature that is selected.
        var instance = new JsonObject
        {
            ["hardware"] = request.Hardware?.DeepClone(),
            ["network"] = request.Network?.DeepClone(),
            ["inputs"] = request.Inputs?.DeepClone(),
        };
        foreach (var (name, hint) in hints ?? [])
        {
            if (chosen.ContainsKey(name) || RequiredWhen(hint?["requiredWhen"], instance) is not { } condition)
                continue;
            throw new ChangeRefusal(ChangeRefusal.Reasons.Invalid, $"{target.Reference.Id} needs the secret {name} {condition}: choose a store key for it.",
                fieldIssues: [new FieldIssueDto($"/secrets/{name}", $"Required {condition}.")]);
        }
        var secrets = new JsonObject();
        var generations = new List<Generation>();
        foreach (var (name, choice) in chosen.OrderBy(p => target.Workflow.Secrets.Contains(p.Key) ? 0 : 1).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            var pointer = $"/secrets/{name}";
            if (!known.Contains(name) || !ExtraVarPattern().IsMatch(name))
                throw new ChangeRefusal(ChangeRefusal.Reasons.Invalid, $"{target.Reference.Id} takes no secret {name}.", fieldIssues: [new FieldIssueDto(pointer, "Not a secret of this workflow.")]);
            if (choice.Store is not null == choice.Generate is not null)
                throw new ChangeRefusal(ChangeRefusal.Reasons.Invalid, $"For {name}, choose a store key or a generated value, not both.",
                    fieldIssues: [new FieldIssueDto(pointer, "A store key or a generated value.")]);
            if (choice.Store is { } store)
            {
                CheckKey(store.Trim(), pointer);
                secrets[name] = new JsonObject { ["store"] = store.Trim() };
                continue;
            }

            var requested = hints?[name]?["generate"]?.ToString();
            if (choice.Generate != requested || !SecretGenerators.IsKnown(choice.Generate!))
                throw new ChangeRefusal(ChangeRefusal.Reasons.Invalid,
                    requested is null ? $"{target.Reference.Id} does not ask for {name} to be generated: choose a store key."
                        : $"{target.Reference.Id} asks for {name} to be generated as {requested}, not {choice.Generate}.",
                    fieldIssues: [new FieldIssueDto(pointer, "This workflow does not generate it that way.")]);
            var key = $"{(target.ResourceKind == ResourceKinds.Guest ? "guests" : "services")}/{target.Address}/{name}";
            if (await db.StoreEntries.AnyAsync(e => e.Key == key, ct))
                throw new ChangeRefusal(ChangeRefusal.Reasons.Exists,
                    $"The store already has {key}, perhaps from an earlier guest of that name. Choose it as an existing key, or delete it under Variables first.",
                    fieldIssues: [new FieldIssueDto(pointer, $"{key} is already in the store.")]);
            Generation generation;
            if (choice.Generate == SecretGenerators.AgentEnrollmentToken)
            {
                var labels = (choice.Labels ?? target.AgentLabels).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().Order(StringComparer.Ordinal).ToList();
                if (labels.FirstOrDefault(l => !EnrollmentTokens.IsLabel(l)) is { } bad)
                    throw new ChangeRefusal(ChangeRefusal.Reasons.Invalid, $"'{bad}' is not a label: lowercase letters, digits, '.', '_' and '-'.",
                        fieldIssues: [new FieldIssueDto(pointer, "Labels are lowercase letters, digits, '.', '_' and '-'.")]);
                var agent = string.IsNullOrWhiteSpace(choice.Name) ? target.Name : choice.Name.Trim();
                var hours = Math.Clamp(options.Value.AgentChannel.GeneratedEnrollmentTokenMinutes, 5, EnrollmentTokens.MaxMinutes) / 60.0;
                generation = new Generation(name, key, choice.Generate, $"a one-time enrollment token for an agent named {agent}, labels "
                    + $"[{string.Join(", ", labels)}], valid for {hours.ToString("0.#", CultureInfo.InvariantCulture)} hours", agent, labels, 0);
            }
            else
            {
                var length = hints?[name]?["length"] is JsonValue l && l.TryGetValue<int>(out var n) ? Math.Clamp(n, 16, 128) : 32;
                generation = new Generation(name, key, choice.Generate!, $"a random password of {length} characters", null, [], length);
            }

            generations.Add(generation);
            secrets[name] = new JsonObject { ["store"] = key };
            comments[$"secrets.{name}"] = $"generated at deploy: {(generation.Generator == SecretGenerators.AgentEnrollmentToken ? "one-time enrollment token" : "random password")}";
        }

        if (secrets.Count > 0)
        {
            document["secrets"] = secrets;
            comments["secrets"] = "values are in the variable store, never in Git";
        }

        return generations;
    }

    /// <summary>
    /// Makes each value and records what it owns in the same save that makes it: the enrollment token with its id, the store
    /// entry with the version and description it wrote. A key that is in the store already is never written over: the deploy is
    /// refused. Nothing here is logged or returned.
    /// </summary>
    private async Task GenerateAsync(OctopushyDb db, IServiceProvider services, IReadOnlyList<Generation> generations, IReadOnlyList<GeneratedValue> values,
        CancellationToken ct)
    {
        var store = services.GetRequiredService<VariableStore>();
        foreach (var (generation, value) in generations.Zip(values))
        {
            if (await db.StoreEntries.AnyAsync(e => e.Key == generation.Key, ct))
                throw new ChangeRefusal(ChangeRefusal.Reasons.Exists,
                    $"The store already has {generation.Key}: a generated value never replaces one. Choose it as an existing key, or delete it under Variables first.",
                    fieldIssues: [new FieldIssueDto($"/secrets/{generation.Secret}", $"{generation.Key} is already in the store.")]);
            string secret;
            if (generation.Generator == SecretGenerators.AgentEnrollmentToken)
            {
                var ca = services.GetRequiredService<AgentCertificateAuthority>();
                var (token, made) = EnrollmentTokens.Create(db, ca, generation.TokenName, generation.Labels, 2,
                    options.Value.AgentChannel.GeneratedEnrollmentTokenMinutes, clock.GetUtcNow());
                value.TokenId = token.Id;
                value.State = GeneratedValueStates.Created;
                value.UpdatedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
                secret = made;
            }
            else
            {
                secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(generation.Length)).Replace('+', '-').Replace('/', '_').TrimEnd('=')[..generation.Length];
            }

            // A new key's first version is 1, and the store saves it together with this record.
            value.StoreVersion = 1;
            value.State = GeneratedValueStates.Created;
            value.UpdatedAt = clock.GetUtcNow();
            try
            {
                await store.SetAsync(generation.Key, secret, Visibilities.Secret, value.StoreDescription, ct, value.Id);
            }
            catch (StoreException e)
            {
                // Not written: the record says so before the refusal is recorded.
                value.StoreVersion = null;
                value.State = value.TokenId is null ? GeneratedValueStates.Planned : GeneratedValueStates.Created;
                throw new ChangeRefusal(e is StoreConflictException ? ChangeRefusal.Reasons.Exists : ChangeRefusal.Reasons.Invalid,
                    $"A generated value could not be stored: {e.Message}");
            }
        }
    }

    /// <summary>The sections of a new definition a later revision must still have for the deploy to be the one applied.</summary>
    private static JsonObject Written(JsonObject document)
    {
        var written = new JsonObject();
        foreach (var (key, value) in document.Where(p => p.Key is not ("schemaVersion" or "type")))
            written[key] = value?.DeepClone();
        return written;
    }

    /// <summary>Refuses a VMID that is not free on the installation, offering the next free one.</summary>
    /// <param name="declaredAt">The commit whose guest files are read: the head, or the branch a push lands on.</param>
    /// <param name="defines">The repository that defines the installation, such as the commit a push would land; by default
    /// <paramref name="declaredAt"/>'s.</param>
    private async Task CheckVmidAsync(OctopushyDb db, DeployTarget target, int vmid, string declaredAt, LabRepository? defines, Guid? except,
        CancellationToken ct)
    {
        defines ??= (await revisions.ResolveAsync(declaredAt, WorkflowReleaseBook.Of(db, workflowMirrors), ct)).Repository;
        var installation = defines.Installation(target.Installation)
            ?? throw new ChangeRefusal(ChangeRefusal.Reasons.Unavailable, $"{target.Installation} is not defined on the branch any more.");
        var taken = await TakenVmidsAsync(db, InstallationAt.Of(installation), declaredAt, except, ct);
        if (!taken.TryGetValue(vmid, out var why))
            return;
        var next = NextFree(taken, Math.Max(VmidFloor, vmid));
        throw new ChangeRefusal(ChangeRefusal.Reasons.VmidTaken,
            $"VMID {vmid} is not free on {target.Installation}: {why}. The next free VMID is {next}; review the deploy again with it.",
            fieldIssues: [new FieldIssueDto("/vmid", $"Taken: {why}. {next} is free.")], nextVmid: next);
    }

    /// <summary>
    /// Every VMID taken on an installation, with why: observed anywhere on it (a cluster shares its VMIDs), written in any
    /// <c>guest.yaml</c> under it at <paramref name="declaredAt"/>, valid or not, bound to or reserved by a guest, or held by a
    /// deploy that is not settled yet. It fails closed: without a successful, complete and recent observation, or with a
    /// <c>guest.yaml</c> that cannot be read, which VMIDs are free is not known, and it refuses (<c>vmids-unknown</c>).
    /// </summary>
    private async Task<Dictionary<int, string>> TakenVmidsAsync(OctopushyDb db, InstallationAt at, string declaredAt, Guid? except, CancellationToken ct)
    {
        var installation = at.Address;
        var folder = at.Folder;
        if (await ObservationProblemAsync(db, at, ct) is { } problem)
            throw new ChangeRefusal(ChangeRefusal.Reasons.VmidsUnknown, problem, fieldIssues: [new FieldIssueDto("/vmid", problem)]);
        var taken = new Dictionary<int, string>();
        var observation = await ObservationAsync(db, installation, ct);
        foreach (var vm in (observation?.Data["guests"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (vm["vmid"] is JsonValue v && v.TryGetValue<int>(out var vmid))
                taken.TryAdd(vmid, $"observed on {vm["host"]?.ToString() ?? "a host"}{(vm["name"]?.ToString() is { Length: > 0 } n ? $" as {n}" : "")}");
        }

        foreach (var (vmid, file) in await WrittenVmidsAsync(installation, folder, declaredAt, ct))
            taken.TryAdd(vmid, $"written in {file}");
        var resources = await db.Resources.AsNoTracking()
            .Where(r => r.Kind == ResourceKinds.Guest && r.RetiredAt == null
                        && (r.BoundInstallation == installation && r.BoundVmid != null || r.ReservedInstallation == installation && r.ReservedVmid != null))
            .Select(r => new { r.Address, r.BoundVmid, r.BoundInstallation, r.ReservedVmid, r.ReservedInstallation }).ToListAsync(ct);
        foreach (var r in resources)
        {
            if (r.BoundInstallation == installation && r.BoundVmid is { } bound)
                taken.TryAdd(bound, $"the VM of {r.Address}");
            if (r.ReservedInstallation == installation && r.ReservedVmid is { } reserved)
                taken.TryAdd(reserved, $"reserved by {r.Address}");
        }

        var prefix = installation + "/";
        // A final change holds nothing: one applied is held by its definition from then on, and any other never will be.
        var settled = ChangeStatuses.Final;
        var deploys = await db.Changes.AsNoTracking()
            .Where(c => c.Kind == ChangeKinds.CreateGuest && c.Vmid != null && c.Address.StartsWith(prefix) && !settled.Contains(c.Status) && c.Id != except)
            .Select(c => new { c.Address, c.Vmid }).ToListAsync(ct);
        foreach (var deploy in deploys)
            taken.TryAdd(deploy.Vmid!.Value, $"held by the deploy of {deploy.Address}");
        return taken;
    }

    /// <summary>
    /// The VMID written in every <c>guest.yaml</c> under the installation at <paramref name="commit"/>, read as plain YAML so a
    /// guest that fails validation still holds its number. A file that is not YAML at all refuses: what it holds is unknown.
    /// </summary>
    private async Task<IReadOnlyList<(int Vmid, string File)>> WrittenVmidsAsync(string installation, string folder, string commit, CancellationToken ct)
    {
        var guests = $"{folder}/guests/";
        var files = (await mirror.TreeAsync(commit, ct))
            .Where(f => f.Path.StartsWith(guests, StringComparison.Ordinal) && f.Path.EndsWith("/guest.yaml", StringComparison.Ordinal)
                        && f.Path.Count(c => c == '/') == guests.Count(c => c == '/') + 1)
            .Select(f => f.Path).ToList();
        var written = new List<(int, string)>();
        foreach (var file in files)
        {
            JsonNode? root;
            try
            {
                var text = (await mirror.ReadFileAsync(commit, file, ct))?.Text
                    ?? throw new YamlReadException("it is not a text file", null);
                root = YamlDocumentReader.Read(text).Root;
            }
            catch (YamlReadException e)
            {
                throw new ChangeRefusal(ChangeRefusal.Reasons.VmidsUnknown,
                    $"{file} cannot be read ({e.Message}), so the VMID it holds on {installation} is unknown. Fix it before deploying there.");
            }

            var vmid = root?["vmid"];
            if (vmid is JsonValue number && number.TryGetValue<int>(out var n))
                written.Add((n, file));
            else if (vmid is JsonValue text && text.TryGetValue<string>(out var digits) && int.TryParse(digits, out var parsed))
                written.Add((parsed, file));
        }

        return written;
    }

    /// <summary>How old an installation's observation may be for its free VMIDs to be trusted.</summary>
    private TimeSpan ObservationMaxAge => TimeSpan.FromMinutes(Math.Max(1,
        options.Value.Deploy.ObservationMaxAgeMinutes ?? 3 * Math.Max(1, options.Value.Scheduling.ObserveMinutes)));

    /// <summary>
    /// Why the installation's latest observation cannot say which VMIDs are free, or null: there is none, a later one failed,
    /// it is older than <see cref="ObservationMaxAge"/>, or it did not see every host (one offline or missing, or a cluster
    /// without quorum).
    /// </summary>
    private async Task<string?> ObservationProblemAsync(OctopushyDb db, InstallationAt at, CancellationToken ct)
    {
        var installation = at.Address;
        var declaredHosts = at.Hosts;
        var resource = await db.Resources.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Kind == ResourceKinds.Installation && r.Address == installation && r.RetiredAt == null, ct);
        if (resource is null)
            return $"{installation} is not in the inventory yet: sync the repository, then observe it.";
        var observation = await ObservationAsync(db, installation, ct);
        if (observation is not { } latest)
            return resource.ObserveErrorAt is { } never
                ? $"{installation} has never been observed successfully; the last attempt, at {Time(never)}, failed: {resource.ObserveError}. "
                  + "Which VMIDs are free cannot be known: observe it first."
                : $"{installation} has never been observed successfully, so which VMIDs are free cannot be known. Observe it first.";
        if (resource.ObserveErrorAt is { } failed && failed >= latest.ObservedAt)
            return $"{installation} has not been observed successfully since {Time(latest.ObservedAt)}: the observation at {Time(failed)} failed "
                   + $"({resource.ObserveError}). Refresh its observation first.";
        var age = clock.GetUtcNow() - latest.ObservedAt;
        if (age > ObservationMaxAge)
            return $"{installation} has not been observed successfully since {Time(latest.ObservedAt)}, more than {(int)ObservationMaxAge.TotalMinutes} "
                   + "minutes ago. Refresh its observation first.";
        // A clock set back makes any observation look recent: one dated ahead of now is not trusted.
        if (age < -ClockTolerance)
            return $"{installation}'s last observation is dated {Time(latest.ObservedAt)}, after the server's time now, {Time(clock.GetUtcNow())}. "
                   + "Check the server's clock, then observe it again.";

        // Evidence about this installation only if it went through the connection the installation has now.
        if (!ObservedConnection.Matches(latest.Data["connection"], at.Connection))
            return latest.Data["connection"] is null
                ? $"{installation}'s last observation does not record the connection it went through. Observe it again."
                : $"{installation}'s last observation went through {ObservedConnection.Describe(latest.Data["connection"])}, and {installation} now "
                  + $"connects to {at.Connection.Endpoint}: it says nothing about the machines there. Observe it again.";

        // Proxmox lists only the guests the observing token may audit: the agent proves the list is every VM, or says why not.
        if (latest.Data["vmVisibility"] is not JsonObject visibility || visibility["complete"] is not JsonValue complete
            || !complete.TryGetValue<bool>(out var sees) || !sees)
            return $"{installation}'s last observation cannot show it saw every VM: "
                   + (latest.Data["vmVisibility"] is JsonObject seen && seen["reason"]?.ToString() is { Length: > 0 } reason
                       ? reason
                       : "it does not say whether its token sees every VM")
                   + ". Proxmox leaves out the VMs a token may not audit. Give the observing token VM.Audit on /vms, propagated and not "
                   + "narrowed below, and Sys.Audit on /access, then observe it again.";
        if (latest.Data["hosts"] is not JsonArray { Count: > 0 } listed)
            return $"{installation}'s last observation lists no host, so the VMIDs it shows may be incomplete. Observe it again.";
        var hosts = listed.OfType<JsonObject>().ToList();
        var unknown = hosts.Where(h => h["online"] is not JsonValue o || !o.TryGetValue<bool>(out _)).Select(h => h["name"]?.ToString() ?? "?").ToList();
        var offline = hosts.Where(h => h["online"] is JsonValue o && o.TryGetValue<bool>(out var up) && !up).Select(h => h["name"]?.ToString() ?? "?").ToList();
        var missing = declaredHosts.Where(d => hosts.All(h => h["name"]?.ToString() != d)).ToList();
        if (offline.Count > 0 || missing.Count > 0 || unknown.Count > 0)
            return $"{installation}'s last observation, at {Time(latest.ObservedAt)}, did not see every host"
                   + (offline.Count > 0 ? $" ({string.Join(", ", offline)} offline)" : "")
                   + (unknown.Count > 0 ? $" ({string.Join(", ", unknown)} without an online state)" : "")
                   + (missing.Count > 0 ? $" ({string.Join(", ", missing)} not listed)" : "")
                   + ", so the VMIDs it shows may be incomplete. Observe it again once every host is up.";
        // A cluster says it is one and whether it is quorate; a standalone host says it is none. Anything else is not known.
        if (!latest.Data.ContainsKey("cluster"))
            return $"{installation}'s last observation does not say whether it is a cluster. Observe it again.";
        if (latest.Data["cluster"] is JsonObject cluster)
        {
            if (cluster["quorate"] is not JsonValue q || !q.TryGetValue<bool>(out var quorate))
                return $"{installation}'s last observation does not say whether its cluster is quorate. Observe it again.";
            if (!quorate)
                return $"{installation}'s cluster was not quorate at its last observation, {Time(latest.ObservedAt)}, so the VMIDs it shows may be stale. "
                       + "Observe it again once it is.";
        }
        else if (latest.Data["cluster"] is not null || at.Type == "cluster")
        {
            return $"{installation} is declared a cluster, and its last observation saw a standalone host. Observe it again.";
        }

        return null;
    }

    /// <summary>How far ahead of the server's clock an observation may be dated, for clocks that drift a little.</summary>
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(2);

    private static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string FolderOf(InstallationDefinition installation) => Path.GetDirectoryName(installation.File)!.Replace('\\', '/');

    private static Task<Guid?> InstallationIdAsync(OctopushyDb db, string installation, CancellationToken ct) =>
        db.Resources.AsNoTracking().Where(r => r.Kind == ResourceKinds.Installation && r.Address == installation && r.RetiredAt == null)
            .Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);

    /// <summary>A workflow release as a review names it: a tag's commit, or for a workflow kept in the lab repository, its files.</summary>
    private static string ReleaseIdentity(string id, string version, string? tagCommit, string? localDigest) =>
        tagCommit is not null ? $"{id}@{version} at {tagCommit}" : $"{id}@{version} from the lab repository, files {localDigest}";

    /// <summary>
    /// A workflow kept in the lab repository as a release: what every run of it takes, by the same definition the runs' own
    /// hashes use (<see cref="ProvisionInputs.WorkflowHash"/>): its folder and every file of every stage's root, such as a module
    /// under <c>modules/</c>. A change to any of them is another release.
    /// </summary>
    private static string LocalRelease(WorkflowDefinition workflow) =>
        ReleaseIdentity(workflow.Id, workflow.Version.ToString(), null, Canonical.Hash(ProvisionInputs.WorkflowHash(workflow, workflow.Roots))[..16]);

    /// <summary>The lab repository's own workflow the catalog offered, as the head revision it read it from defines it.</summary>
    private async Task<WorkflowDefinition> LocalWorkflowAsync(OctopushyDb db, WorkflowVersionDto version, CancellationToken ct)
    {
        var head = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(ct);
        var workflow = head is null ? null
            : (await revisions.GetAsync(head, ct)).Workflows.FirstOrDefault(w => w.Id == version.Id && w.Version.ToString() == version.Version);
        return workflow ?? throw new ChangeRefusal(ChangeRefusal.Reasons.Stale, $"{version.Id}@{version.Version} is not in the head revision any more; reload the wizard.");
    }

    /// <summary>
    /// What binds a deploy to its review: the workflow release it resolved to, and every file it writes. A deploy commits only
    /// what was reviewed.
    /// </summary>
    private static string ReviewDigest(DeployTarget target, ComposedChange composed) =>
        ReviewDigest(target.Release, composed.Changes.Select(c => (c.Path, c.Content ?? "")));

    private static string ReviewDigest(string release, IEnumerable<(string Path, string Content)> files)
    {
        var text = new System.Text.StringBuilder(release).Append('\n');
        foreach (var (path, content) in files.OrderBy(f => f.Path, StringComparer.Ordinal))
            text.Append(path).Append('\0').Append(content).Append('\0');
        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// Refuses an attempt whose commit would not land what was reviewed: the new resource's reference resolving there to
    /// another workflow release, or its file there differing. Asked of every candidate, before it is pushed.
    /// </summary>
    private async Task CheckLandingAsync(DeployRequest request, DeployTarget target, LabRepository landing, string commit, CancellationToken ct)
    {
        var defined = target.ResourceKind == ResourceKinds.Guest ? landing.Guest(target.Address) is not null : landing.Service(target.Address) is not null;
        // A definition that does not even load there is refused by the validation that follows, as a new error.
        if (!defined)
            return;
        var workflow = target.ResourceKind == ResourceKinds.Guest ? landing.Guest(target.Address)?.Workflow : landing.Service(target.Address)?.Workflow;
        var release = workflow is null ? null
            : workflow.Repository is not null ? ReleaseIdentity(workflow.Id, workflow.Version.ToString(), workflow.Commit, null)
            : LocalRelease(workflow);
        if (release != request.ReviewedRelease)
            throw new ChangeRefusal(ChangeRefusal.Reasons.Stale,
                $"The workflow release changed since the review: it was {request.ReviewedRelease}, and at the commit this deploy would land it is "
                + $"{release ?? "nothing it resolves to"}. Review again.");
        var text = (await mirror.ReadFileAsync(commit, target.File, ct))?.Text ?? "";
        if (ReviewDigest(release!, [(target.File, text)]) != request.ReviewDigest)
            throw new ChangeRefusal(ChangeRefusal.Reasons.Stale, "What the deploy would land is not what was reviewed. Review again.");
    }

    /// <summary>Refuses a deploy whose workflow release, or whose files, differ from what was reviewed.</summary>
    private static void CheckReviewed(DeployRequest request, DeployTarget target, ComposedChange composed)
    {
        if (request.ReviewDigest is null)
            throw new ChangeRefusal(ChangeRefusal.Reasons.Stale, "Review the deploy first: a deploy commits only what was reviewed.");
        if (request.ReviewedRelease != target.Release)
            throw new ChangeRefusal(ChangeRefusal.Reasons.Stale,
                $"The workflow release changed since the review: it was {request.ReviewedRelease ?? "not named"}, it is {target.Release} now. Review again.");
        if (request.ReviewDigest != ReviewDigest(target, composed))
            throw new ChangeRefusal(ChangeRefusal.Reasons.Stale, "What the deploy would commit is not what was reviewed. Review again.");
    }

    private static int NextFree(IReadOnlyDictionary<int, string> taken, int from)
    {
        var vmid = from;
        while (taken.ContainsKey(vmid) && vmid < MaxVmid)
            vmid++;
        return vmid;
    }

    /// <summary>The installation's latest observation, or null.</summary>
    private static async Task<(JsonObject Data, DateTimeOffset ObservedAt)?> ObservationAsync(OctopushyDb db, string installation, CancellationToken ct)
    {
        var id = await db.Resources.AsNoTracking()
            .Where(r => r.Kind == ResourceKinds.Installation && r.Address == installation && r.RetiredAt == null)
            .Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
        if (id is null)
            return null;
        var latest = await db.Observations.AsNoTracking().Where(o => o.ResourceId == id).OrderByDescending(o => o.ObservedAt)
            .Select(o => new { o.Data, o.ObservedAt }).FirstOrDefaultAsync(ct);
        return latest is not null && JsonNode.Parse(latest.Data) is JsonObject data ? (data, latest.ObservedAt) : null;
    }

    /// <summary>
    /// Whether a <c>requiredWhen</c> hint, <c>{ pointer, value }</c>, holds for the form's value: the value at the JSON pointer
    /// into <c>{ hardware, network, inputs }</c> is <c>value</c>, or is an array that holds it. Returns how to say so, or null.
    /// </summary>
    public static string? RequiredWhen(JsonNode? hint, JsonObject instance)
    {
        if (hint is not JsonObject condition || condition["pointer"]?.GetValueKind() != JsonValueKind.String || !condition.ContainsKey("value"))
            return null;
        var pointer = condition["pointer"]!.GetValue<string>();
        JsonNode? node = instance;
        foreach (var part in pointer.Split('/').Skip(1).Select(p => p.Replace("~1", "/").Replace("~0", "~")))
        {
            node = node switch
            {
                JsonObject o => o[part],
                JsonArray a when int.TryParse(part, out var i) && i >= 0 && i < a.Count => a[i],
                _ => null,
            };
            if (node is null)
                return null;
        }

        var wanted = condition["value"];
        var holds = YamlEditor.Same(node, wanted) || node is JsonArray items && items.Any(i => YamlEditor.Same(i, wanted));
        return holds ? $"when {pointer} {(node is JsonArray ? "includes" : "is")} {wanted?.ToJsonString()}" : null;
    }

    private static Task<bool> RecordedObservedOnlyAsync(OctopushyDb db, string installation, CancellationToken ct) =>
        db.Resources.AsNoTracking().AnyAsync(r => r.Kind == ResourceKinds.Installation && r.Address == installation && r.RetiredAt == null
            && r.ObservedOnly, ct);

    /// <summary>What the latest observation says of a host: its status, or whether it was online.</summary>
    private static string? HostStatus(JsonObject? host) =>
        host?["status"]?.ToString() is { Length: > 0 } status ? status
        : host?["online"] is JsonValue online && online.TryGetValue<bool>(out var up) ? up ? "online" : "offline"
        : null;

    private static IReadOnlyList<string> LabLabels(LabRepository repository, string lab) =>
        repository.Lab(lab)?.AgentLabels ?? repository.Settings?.DefaultAgentLabels ?? [];

    private static string ObservedOnly(string installation) =>
        $"{installation} is observed only (manage: false): Octopushy changes nothing there, so nothing can be deployed to it.";

    private static void CheckKey(string key, string pointer)
    {
        if (!KeyPattern().IsMatch(key) || key.StartsWith(VariableStore.SystemScope, StringComparison.Ordinal))
            throw new ChangeRefusal(ChangeRefusal.Reasons.Invalid, $"'{key}' is not a store key a definition can name.",
                fieldIssues: [new FieldIssueDto(pointer, "Segments of letters, digits, '.', '_' or '-', separated by '/'; not system/.")]);
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\z")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^(?![Aa][Nn][Ss][Ii][Bb][Ll][Ee]_|[Oo][Cc][Tt][Oo][Pp][Uu][Ss][Hh][Yy])[A-Za-z_][A-Za-z0-9_]*\\z")]
    private static partial Regex ExtraVarPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*(?:/[A-Za-z0-9][A-Za-z0-9._-]*)*\\z")]
    private static partial Regex KeyPattern();
}
