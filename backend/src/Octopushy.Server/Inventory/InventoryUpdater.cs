using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Inventory;

/// <summary>
/// Applies a new revision to the resources Octopushy knows, and proposes the runs it implies:
/// a provision run for each guest whose inputs changed, a retire run for each guest newly
/// retired, a retire run for each guest whose folder was deleted, and a cluster run for each
/// application cluster whose inputs changed. Each installation whose definition changed is
/// observed again. A deleted cluster.yaml only marks the cluster missing: nothing is destroyed.
/// </summary>
public sealed class InventoryUpdater(RunFactory factory, RunEngine engine, RevisionCache revisions, TimeProvider clock, ILogger<InventoryUpdater> logger)
{
    public static readonly JsonSerializerOptions IssueJson = new(JsonSerializerDefaults.Web);

    /// <summary>Runs inside the engine's gate, with the revision already saved.</summary>
    public async Task ApplyAsync(OctopushyDb db, DefinitionRevision revision, LabRepository repository, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var active = await db.Resources.Where(r => r.RetiredAt == null).ToListAsync(cancellationToken);
        var byAddress = active.ToDictionary(r => r.Address, StringComparer.Ordinal);

        foreach (var guest in repository.Guests.Where(g => g.RenamedFrom is not null && repository.IsValid(g.Address)))
        {
            var previous = $"{guest.InstallationAddress}/{guest.RenamedFrom}";
            if (!byAddress.ContainsKey(guest.Address) && byAddress.TryGetValue(previous, out var renamed)
                && renamed.ManagedWorker is null && repository.Resource(previous) is null)
            {
                logger.LogInformation("{Previous} was renamed to {Address}.", previous, guest.Address);
                byAddress.Remove(previous);
                renamed.Address = guest.Address;
                byAddress[guest.Address] = renamed;
                // A service's address starts with its guest's, and its identity goes with it.
                foreach (var service in byAddress.Values.Where(r => r.Kind == ResourceKinds.Service && r.ParentAddress == previous).ToList())
                {
                    byAddress.Remove(service.Address);
                    service.Address = guest.Address + service.Address[previous.Length..];
                    service.ParentAddress = guest.Address;
                    byAddress[service.Address] = service;
                }
            }
        }

        var seen = new HashSet<Guid>();
        foreach (var entry in repository.Resources)
        {
            if (!byAddress.TryGetValue(entry.Address, out var resource))
            {
                if (!entry.Valid && repository.Guest(entry.Address)?.RenamedFrom is { } renamedFrom
                    && repository.Resource($"{entry.ParentAddress}/{renamedFrom}") is null
                    && byAddress.TryGetValue($"{entry.ParentAddress}/{renamedFrom}", out var pendingRename)
                    && pendingRename.ManagedWorker is null)
                {
                    // Keep an ordinary guest's identity at its old address while
                    // displaying the invalid proposed definition. Do not create a
                    // competing row that would block the rename after correction.
                    resource = pendingRename;
                }
                if (resource is null && entry.Kind == ResourceKind.Guest && repository.Guest(entry.Address)?.Retired == true)
                {
                    // A retired guest whose removal is done keeps pointing at its old resource.
                    resource = await db.Resources
                        .Where(r => r.Address == entry.Address && r.RetiredAt != null)
                        .OrderByDescending(r => r.RetiredAt)
                        .FirstOrDefaultAsync(cancellationToken);
                }

                if (resource is null)
                {
                    resource = new Resource
                    {
                        Id = Guid.CreateVersion7(),
                        Address = entry.Address,
                        Kind = Kind(entry.Kind),
                        ParentAddress = entry.ParentAddress,
                        Lab = entry.Address.Split('/')[0],
                        FirstSeenAt = now,
                    };
                    db.Resources.Add(resource);
                    byAddress[entry.Address] = resource;
                }
            }

            seen.Add(resource.Id);
            resource.LastRevisionId = revision.Id;
            resource.MissingSince = null;
            resource.Valid = entry.Valid;
            // A file that cannot be read says nothing, so what it last said stands: the stateful guard reads this,
            // and a commit that breaks the file must not be how a guest loses its guard.
            resource.Definition = entry.Json?.ToJsonString() ?? resource.Definition;
            resource.DefinitionFile = entry.File;
            resource.Issues = JsonSerializer.Serialize(repository.IssuesFor(entry.Address).ToList(), IssueJson);
        }

        var guests = byAddress.Values.Where(r => r.Kind == ResourceKinds.Guest && seen.Contains(r.Id) && r.RetiredAt is null).ToList();
        foreach (var resource in guests.Where(r => r.Valid))
        {
            // Missing definitions still retain durable ownership. In particular,
            // an invalid rename must not let a second row claim a worker's VM.
            var problem = IdentityProblem(resource, repository.Guest(resource.Address)!, byAddress.Values);
            if (problem is not null)
            {
                resource.Valid = false;
                var issues = repository.IssuesFor(resource.Address).ToList();
                issues.Add(new DefinitionIssue(IssueSeverity.Error, repository.Guest(resource.Address)!.File, null, problem, resource.Address));
                resource.Issues = JsonSerializer.Serialize(issues, IssueJson);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        var activeRuns = await db.Runs.Include(r => r.Steps).ThenInclude(s => s.Attempts).AsSplitQuery()
            .Where(r => r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled)
            .ToListAsync(cancellationToken);
        var currentSourceIds = await RepositoryTrust.CurrentRevisions(db).Select(r => r.Id).ToListAsync(cancellationToken);

        var runs = new List<Run>();
        if (repository.RepositoryValid)
        {
            foreach (var resource in byAddress.Values.Where(r => !seen.Contains(r.Id) && r.RetiredAt is null))
            {
                resource.MissingSince ??= now;
                await WithdrawAsync(db, activeRuns, revision, resource, $"The definition of {resource.Address} disappeared in {Short(revision.Commit)}.",
                    cancellationToken, keepRemoval: true);
                // Deleting a folder asks for removal; the destructive policy decides whether it waits for approval.
                if (resource.Kind is not (ResourceKinds.Guest or ResourceKinds.Service)) continue;
                try
                {
                    var removal = resource.Kind == ResourceKinds.Guest
                        ? await GuestRemoval.ProposeAsync(db, revisions, factory, repository, resource, revision, force: false, cancellationToken)
                        : await ServiceRemoval.ProposeAsync(db, revisions, factory, repository, resource, revision, now, force: false, cancellationToken);
                    if (removal is not null)
                        runs.Add(removal);
                }
                catch (RunException error)
                {
                    logger.LogWarning("The removal of {Address} cannot be planned: {Error}", resource.Address, error.Message);
                    resource.Issues = JsonSerializer.Serialize(new[] { new DefinitionIssue(IssueSeverity.Error, resource.DefinitionFile ?? resource.Address,
                        null, $"The folder was deleted, but its removal cannot be planned: {error.Message}", resource.Address) }, IssueJson);
                }
            }
        }

        foreach (var resource in byAddress.Values.Where(r => r.Kind == ResourceKinds.Cluster && seen.Contains(r.Id) && r.RetiredAt is null).ToList())
        {
            var cluster = repository.Cluster(resource.Address);
            if (resource.Valid && cluster is not null && ClusterProblem(resource, cluster) is { } problem)
            {
                resource.Valid = false;
                var issues = repository.IssuesFor(resource.Address).ToList();
                issues.Add(new DefinitionIssue(IssueSeverity.Error, cluster.File, null, problem, resource.Address));
                resource.Issues = JsonSerializer.Serialize(issues, IssueJson);
            }

            if (!resource.Valid || cluster is null)
            {
                await WithdrawAsync(db, activeRuns, revision, resource, $"The definition is invalid in {Short(revision.Commit)}.", cancellationToken);
                continue;
            }

            var inputs = ClusterInputs.Build(repository, cluster, revision.Commit);
            if (resource.DesiredInputsHash == inputs.Hash && currentSourceIds.Contains(resource.DesiredRevisionId ?? Guid.Empty))
            {
                if (activeRuns.Any(r => r.ResourceId == resource.Id && r.RevisionId == revision.Id))
                    await SupersedeAsync(db, activeRuns, revision, resource, $"Superseded by {Short(revision.Commit)}.", cancellationToken);
                continue;
            }

            resource.DesiredInputsHash = inputs.Hash;
            resource.DesiredRevisionId = revision.Id;
            await SupersedeAsync(db, activeRuns, revision, resource, $"Superseded by {Short(revision.Commit)}.", cancellationToken);
            runs.Add(factory.Cluster(repository, cluster, resource, revision, "revision"));
        }

        var changedInstallations = new List<(Resource Resource, RunContext Context)>();
        foreach (var resource in guests)
        {
            if (!resource.Valid)
            {
                await WithdrawAsync(db, activeRuns, revision, resource, $"The definition is invalid in {Short(revision.Commit)}.", cancellationToken);
                continue;
            }

            var guest = repository.Guest(resource.Address)!;
            var inputs = ProvisionInputs.Build(repository, guest, revision.Commit);
            var desired = guest.Retired ? RunFactory.RetireHash(inputs.Hash) : inputs.Hash;
            if (resource.DesiredInputsHash == desired && currentSourceIds.Contains(resource.DesiredRevisionId ?? Guid.Empty))
            {
                // Only promote a real replacement already proposed by discovery. Metadata
                // or policy-only revisions leave the older pinned plan/approval intact.
                if (activeRuns.Any(r => r.ResourceId == resource.Id && r.RevisionId == revision.Id))
                    await SupersedeAsync(db, activeRuns, revision, resource, $"Superseded by {Short(revision.Commit)}.", cancellationToken);
                continue;
            }

            Run proposed;
            try
            {
                proposed = factory.Guest(repository, guest, resource, revision, "revision");
            }
            catch (RunException error)
            {
                // Controller/image compatibility is a per-guest refusal, not a
                // reason to abandon other runs after recording this repository head.
                resource.Valid = false;
                var issues = repository.IssuesFor(resource.Address).ToList();
                issues.Add(new DefinitionIssue(IssueSeverity.Error, guest.File, null, error.Message, resource.Address));
                resource.Issues = JsonSerializer.Serialize(issues, IssueJson);
                await WithdrawAsync(db, activeRuns, revision, resource, $"The definition cannot be planned in {Short(revision.Commit)}.", cancellationToken);
                continue;
            }

            resource.DesiredInputsHash = desired;
            resource.DesiredRevisionId = revision.Id;
            await SupersedeAsync(db, activeRuns, revision, resource, $"Superseded by {Short(revision.Commit)}.", cancellationToken);
            runs.Add(proposed);
        }

        await UpdateConfigurationsAsync(db, activeRuns, revision, repository, guests, cancellationToken);
        await UpdateServicesAsync(db, activeRuns, revision, repository, byAddress, seen, cancellationToken);

        foreach (var resource in byAddress.Values.Where(r => r.Kind == ResourceKinds.Installation && seen.Contains(r.Id) && r.Valid))
        {
            var installation = repository.Installation(resource.Address)!;
            var hash = RunFactory.InstallationHash(installation);
            if (resource.DesiredInputsHash == hash && currentSourceIds.Contains(resource.DesiredRevisionId ?? Guid.Empty))
                continue;

            resource.DesiredInputsHash = hash;
            resource.DesiredRevisionId = revision.Id;
            changedInstallations.Add((resource, RunFactory.Observation(repository, installation, revision)));
        }

        await db.SaveChangesAsync(cancellationToken);
        await engine.AddRunsAsync(db, runs, cancellationToken);
        foreach (var (resource, context) in changedInstallations)
        {
            await engine.ObserveAsync(db, resource, revision, context, cancellationToken);
        }
    }

    /// <summary>What a formed cluster cannot change: moving the primary is a promotion, and the domain is permanent.</summary>
    private static string? ClusterProblem(Resource resource, ClusterDefinition cluster)
    {
        if (resource.BoundClusterDomain is { } domain && domain != cluster.ClusterDomain)
            return $"This cluster was formed with the domain {domain}, which cannot change. Put it back; a new domain is a new cluster.";
        if (resource.BoundPrimary is { } primary && primary != cluster.Primary?.Guest)
            return $"The primary of this cluster is {primary}. Changing it is a promotion, which this version does not do: put it back, and see docs/technitium-lxc.md for promoting by hand.";
        return null;
    }

    /// <summary>
    /// Records what each guest's configure recipe wants in this revision. The orchestrator proposes
    /// the run, because that also has to wait for the guest's provisioning. A configure run that
    /// has not started changing anything gives way to the new inputs.
    /// </summary>
    private async Task UpdateConfigurationsAsync(OctopushyDb db, List<Run> activeRuns, DefinitionRevision revision, LabRepository repository,
        List<Resource> guests, CancellationToken cancellationToken)
    {
        var configurations = await db.GuestConfigurations.ToDictionaryAsync(c => c.ResourceId, cancellationToken);
        foreach (var resource in guests.Where(r => r.Valid))
        {
            var guest = repository.Guest(resource.Address)!;
            configurations.TryGetValue(resource.Id, out var configuration);
            if (guest.SetupRecipe is null || guest.Retired)
            {
                configuration?.DesiredHash = null;
                continue;
            }

            if (configuration is null)
            {
                configuration = new GuestConfiguration { ResourceId = resource.Id };
                db.GuestConfigurations.Add(configuration);
            }

            var hash = ConfigureInputs.Build(repository, guest).Hash;
            if (configuration.DesiredHash == hash)
                continue;
            configuration.DesiredHash = hash;
            configuration.DesiredRevisionId = revision.Id;
            foreach (var run in activeRuns.Where(r => r.ResourceId == resource.Id && r.Operation == Operations.Configure
                && r.RevisionId != revision.Id && RunEngine.CanSupersede(r)).ToList())
            {
                await engine.CancelInternalAsync(db, run.Id, $"Superseded by {Short(revision.Commit)}.", cancellationToken);
                activeRuns.Remove(run);
            }
        }
    }

    /// <summary>
    /// The same for each service. A service's definition may be fine while its guest is not ready: that
    /// is the scheduler's to wait for, and the wanted hash is recorded regardless.
    /// </summary>
    private async Task UpdateServicesAsync(OctopushyDb db, List<Run> activeRuns, DefinitionRevision revision, LabRepository repository,
        Dictionary<string, Resource> byAddress, HashSet<Guid> seen, CancellationToken cancellationToken)
    {
        var configurations = await db.GuestConfigurations.ToDictionaryAsync(c => c.ResourceId, cancellationToken);
        foreach (var resource in byAddress.Values.Where(r => r.Kind == ResourceKinds.Service && seen.Contains(r.Id) && r.RetiredAt is null).ToList())
        {
            var service = repository.Service(resource.Address);
            configurations.TryGetValue(resource.Id, out var configuration);
            var usable = resource.Valid && service is not null && repository.IsValid(service.Guest) && repository.Guest(service.Guest)!.Retired is false;
            if (configuration is null)
            {
                if (!usable)
                    continue;
                configuration = new GuestConfiguration { ResourceId = resource.Id };
                db.GuestConfigurations.Add(configuration);
                configurations[resource.Id] = configuration;
            }

            var hash = usable ? ConfigureInputs.Build(repository, service!).Hash : null;
            if (configuration.DesiredHash == hash)
                continue;
            configuration.DesiredHash = hash;
            // A folder that came back wants what it had: its removal is what was proposed last, not a deployment.
            if (configuration.ProposedHash?.StartsWith("removal:", StringComparison.Ordinal) == true)
                configuration.ProposedHash = configuration.AppliedHash;
            configuration.DesiredRevisionId = usable ? revision.Id : configuration.DesiredRevisionId;
            // A restored folder comes back here too, and that cancels a removal that has not started.
            foreach (var run in activeRuns.Where(r => r.ResourceId == resource.Id && r.Operation == Operations.Configure
                && r.RevisionId != revision.Id && RunEngine.CanSupersede(r)).ToList())
            {
                await engine.CancelInternalAsync(db, run.Id, $"Superseded by {Short(revision.Commit)}.", cancellationToken);
                activeRuns.Remove(run);
            }
        }
    }

    /// <summary>
    /// A bound guest keeps its installation and VMID; changing them would silently point the
    /// guest at another VM. A VMID bound to one guest cannot be claimed by another.
    /// </summary>
    private static string? IdentityProblem(Resource resource, GuestDefinition guest, IEnumerable<Resource> guests)
    {
        if (resource.ManagedWorker is { } worker && (guest.Retired || guest.ManagedWorker?.Generation != worker.Generation
            || guest.Host != worker.Host || guest.Vmid != worker.Vmid || guest.InstallationAddress != worker.Installation))
            return "Managed workers cannot be retired, replaced, moved or have their worker role removed; retain the existing binding.";

        var managedOwner = guests.FirstOrDefault(r => r.Id != resource.Id && r.ManagedWorker is { } bound
            && (bound.Installation == guest.InstallationAddress && bound.Vmid == guest.Vmid
                || bound.Generation == guest.ManagedWorker?.Generation
                || guest.RenamedFrom is not null && r.Address == $"{guest.InstallationAddress}/{guest.RenamedFrom}"));
        if (managedOwner is not null)
            return $"Managed worker identity is bound to {managedOwner.Address}; renaming or adopting its generation/VM is not supported.";

        if (resource.BoundVmid is { } bound && (bound != guest.Vmid || resource.BoundInstallation != guest.InstallationAddress))
        {
            return $"This guest is bound to VMID {bound} on {resource.BoundInstallation}. Changing it would make a different VM; retire this guest and add a new one instead.";
        }

        var other = guests.FirstOrDefault(r => r.Id != resource.Id && r.BoundVmid == guest.Vmid && r.BoundInstallation == guest.InstallationAddress);
        return other is null ? null : $"VMID {guest.Vmid} on {guest.InstallationAddress} is bound to {other.Address}.";
    }

    /// <summary>Cancels the resource's runs from other revisions that can be, and says whether any was.</summary>
    private async Task<bool> SupersedeAsync(OctopushyDb db, List<Run> activeRuns, DefinitionRevision revision, Resource resource, string reason,
        CancellationToken cancellationToken, bool keepRemoval = false)
    {
        var cancelled = false;
        foreach (var run in activeRuns.Where(r => r.ResourceId == resource.Id && r.RevisionId != revision.Id).ToList())
        {
            // A guest or service that stays deleted keeps its proposed removal.
            if (keepRemoval && RunContext.Parse(run.Context).DefinitionRemovedIn is not null)
                continue;
            if (RunEngine.CanSupersede(run))
            {
                await engine.CancelInternalAsync(db, run.Id, reason, cancellationToken);
                activeRuns.Remove(run);
                // It never ran, so the inputs it was made for are still to be configured.
                if (run.Operation == Operations.Configure
                    && await db.GuestConfigurations.FirstOrDefaultAsync(c => c.ResourceId == resource.Id, cancellationToken) is { } configuration)
                    configuration.ProposedHash = null;
                cancelled = true;
            }
        }

        return cancelled;
    }

    /// <summary>
    /// Cancels the runs a revision that cannot be planned leaves behind, and forgets what they were
    /// proposed for. The resource still remembers its last proposal as made, so the same definition
    /// coming back would look as if a run already existed for it, and none would be made.
    /// </summary>
    private async Task WithdrawAsync(OctopushyDb db, List<Run> activeRuns, DefinitionRevision revision, Resource resource, string reason,
        CancellationToken cancellationToken, bool keepRemoval = false)
    {
        if (await SupersedeAsync(db, activeRuns, revision, resource, reason, cancellationToken, keepRemoval))
        {
            resource.DesiredInputsHash = null;
            resource.DesiredRevisionId = null;
        }
    }

    private static string Kind(ResourceKind kind) => kind switch
    {
        ResourceKind.Lab => ResourceKinds.Lab,
        ResourceKind.Installation => ResourceKinds.Installation,
        ResourceKind.Cluster => ResourceKinds.Cluster,
        ResourceKind.Service => ResourceKinds.Service,
        _ => ResourceKinds.Guest,
    };

    private static string Short(string commit) => commit[..Math.Min(10, commit.Length)];
}
