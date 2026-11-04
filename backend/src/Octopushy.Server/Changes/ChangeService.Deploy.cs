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
        IReadOnlyList<string> AgentLabels);

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
                    observedHosts.FirstOrDefault(o => o["name"]?.ToString() == h)?["status"]?.ToString()))
                .Concat(observedHosts.Select(o => o["name"]?.ToString()).OfType<string>().Where(h => !installation.Hosts.ContainsKey(h)).Distinct()
                    .Select(h => new DeployHostDto(h, false, true, observedHosts.First(o => o["name"]?.ToString() == h)["status"]?.ToString())))
                .ToList();
            var guests = repository.Guests.Where(g => g.InstallationAddress == installation.Address).OrderBy(g => g.Id, StringComparer.Ordinal)
                .Select(g => new DeployGuestDto(g.Address, g.Id, g.WorkflowReference.ToString(), repository.IsValid(g.Address))).ToList();
            // Observed only by its definition, or by the server's record of it, which outlives a definition that stops saying so.
            var managed = installation.Managed && !await RecordedObservedOnlyAsync(db, installation.Address, ct);
            installations.Add(new DeployInstallationDto(installation.Address, installation.Lab, installation.DisplayName, installation.Type,
                managed, managed ? null : ObservedOnly(installation.Address), hosts, guests, LabLabels(repository, installation.Lab),
                observation?.ObservedAt));
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
        if (repository.Installation(installation) is null)
            return null;
        var floor = Math.Clamp(from ?? VmidFloor, VmidFloor, MaxVmid);
        var taken = await TakenVmidsAsync(db, installation, repository, null, ct);
        return new NextVmidDto(installation, NextFree(taken, floor), floor);
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
                    await CheckVmidAsync(db, target, request.Vmid!.Value, target.Repository, null, ct);
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
                generations.Select(g => new GeneratedValueDto(g.Secret, g.Key, g.Generator, g.Detail)).ToList(), next);
        }
        finally
        {
            await mirror.DeleteDraftAsync(id, ct);
        }
    }

    /// <summary>
    /// Commits a deploy as <paramref name="actor"/>: checks the VMID again, records the change as <c>committing</c> with the
    /// store keys it will fill, generates the values it asks for, pushes the new folder, and syncs at once. A deploy refused
    /// here discards what it generated at once; one whose request ends without saying how the push went is settled by
    /// <see cref="ChangeReconciler"/>, which discards them too if its commit never reaches the branch.
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
                if (deployed.ResourceKind == ResourceKinds.Guest)
                    await CheckVmidAsync(db, deployed, request.Vmid!.Value, deployed.Repository, null, ct);
                // On record before anything is generated or pushed, with every key it may fill: whatever happens to this
                // request, the branch never moves, and no value is left in the store, without a change that says why.
                change = RecordDeploy(id, deployed, composed, request, actor);
                change.Status = ChangeStatuses.Committing;
                change.Detail = "Pushing its commit.";
                change.GeneratedKeys = generations.Select(g => g.Key).ToList();
                db.Changes.Add(change);
                await db.SaveChangesAsync(ct);
                await GenerateAsync(db, services, deployed, generations, change, ct);
                var releases = WorkflowReleaseBook.Of(db, workflowMirrors);
                Func<PublishCandidate, Task>? check = deployed.ResourceKind != ResourceKinds.Guest ? null : async candidate =>
                {
                    // At the commit the push lands on: the number reviewed must still be free, or nothing is committed.
                    var at = (await revisions.ResolveAsync(candidate.Remote, releases, ct)).Repository;
                    await CheckVmidAsync(db, deployed, request.Vmid!.Value, at, id, ct);
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
                    await GeneratedValues.DiscardAsync(db, change, logger, clock.GetUtcNow());
                }

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

        var (reference, workflow) = await ResolveWorkflowAsync(db, request.Workflow, resourceKind == ResourceKinds.Guest ? WorkflowKinds.Vm : WorkflowKinds.Service, ct);
        var lab = address.Split('/')[0];
        return new DeployTarget(request.Kind, resourceKind, address, name, folder, file, installationAddress, repository, settings, reference, workflow,
            LabLabels(repository, lab));
    }

    /// <summary>The release a deploy's reference resolves to now, as the wizard offered it.</summary>
    private async Task<(WorkflowReference, WorkflowVersionDto)> ResolveWorkflowAsync(OctopushyDb db, string? text, string manages, CancellationToken ct)
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
        return (reference, version);
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
    /// Generates each value into its store key, and the enrollment tokens they hold, each token recorded on
    /// <paramref name="change"/> in the same save that creates it. Nothing here is logged or returned.
    /// </summary>
    private async Task GenerateAsync(OctopushyDb db, IServiceProvider services, DeployTarget target, IReadOnlyList<Generation> generations,
        ResourceChange change, CancellationToken ct)
    {
        if (generations.Count == 0)
            return;
        var store = services.GetRequiredService<VariableStore>();
        try
        {
            foreach (var generation in generations)
            {
                string value;
                if (generation.Generator == SecretGenerators.AgentEnrollmentToken)
                {
                    var ca = services.GetRequiredService<AgentCertificateAuthority>();
                    var (token, made) = EnrollmentTokens.Create(db, ca, generation.TokenName, generation.Labels, 2,
                        options.Value.AgentChannel.GeneratedEnrollmentTokenMinutes, clock.GetUtcNow());
                    change.GeneratedTokens = [.. change.GeneratedTokens, token.Id];
                    await db.SaveChangesAsync(ct);
                    value = made;
                }
                else
                {
                    value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(generation.Length)).Replace('+', '-').Replace('/', '_').TrimEnd('=')[..generation.Length];
                }

                await store.SetAsync(generation.Key, value, Visibilities.Secret,
                    $"Generated at deploy for {target.Address} ({generation.Generator}, change {change.Id}).", ct);
            }
        }
        catch (StoreException e)
        {
            throw new ChangeRefusal(ChangeRefusal.Reasons.Invalid, $"A generated value could not be stored: {e.Message}");
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
    private async Task CheckVmidAsync(OctopushyDb db, DeployTarget target, int vmid, LabRepository declaredAt, Guid? except, CancellationToken ct)
    {
        var taken = await TakenVmidsAsync(db, target.Installation, declaredAt, except, ct);
        if (!taken.TryGetValue(vmid, out var why))
            return;
        var next = NextFree(taken, Math.Max(VmidFloor, vmid));
        throw new ChangeRefusal(ChangeRefusal.Reasons.VmidTaken,
            $"VMID {vmid} is not free on {target.Installation}: {why}. The next free VMID is {next}; review the deploy again with it.",
            fieldIssues: [new FieldIssueDto("/vmid", $"Taken: {why}. {next} is free.")], nextVmid: next);
    }

    /// <summary>
    /// Every VMID taken on an installation, with why: observed anywhere on it (a cluster shares its VMIDs), declared by a guest
    /// at <paramref name="declaredAt"/>, bound to or reserved by a guest, or held by a deploy that is not settled yet.
    /// </summary>
    private static async Task<Dictionary<int, string>> TakenVmidsAsync(OctopushyDb db, string installation, LabRepository declaredAt, Guid? except,
        CancellationToken ct)
    {
        var taken = new Dictionary<int, string>();
        var observation = await ObservationAsync(db, installation, ct);
        foreach (var vm in (observation?.Data["guests"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (vm["vmid"] is JsonValue v && v.TryGetValue<int>(out var vmid))
                taken.TryAdd(vmid, $"observed on {vm["host"]?.ToString() ?? "a host"}{(vm["name"]?.ToString() is { Length: > 0 } n ? $" as {n}" : "")}");
        }

        foreach (var guest in declaredAt.Guests.Where(g => g.InstallationAddress == installation))
            taken.TryAdd(guest.Vmid, $"declared by {guest.Address}");
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
        var settled = new[] { ChangeStatuses.Applied, ChangeStatuses.Failed, ChangeStatuses.Refused, ChangeStatuses.NoChange };
        var deploys = await db.Changes.AsNoTracking()
            .Where(c => c.Kind == ChangeKinds.CreateGuest && c.Vmid != null && c.Address.StartsWith(prefix) && !settled.Contains(c.Status) && c.Id != except)
            .Select(c => new { c.Address, c.Vmid }).ToListAsync(ct);
        foreach (var deploy in deploys)
            taken.TryAdd(deploy.Vmid!.Value, $"held by the deploy of {deploy.Address}");
        return taken;
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

    private static Task<bool> RecordedObservedOnlyAsync(OctopushyDb db, string installation, CancellationToken ct) =>
        db.Resources.AsNoTracking().AnyAsync(r => r.Kind == ResourceKinds.Installation && r.Address == installation && r.RetiredAt == null
            && r.ObservedOnly, ct);

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
