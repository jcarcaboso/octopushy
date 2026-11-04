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
/// retired, and a retire run for each guest whose folder was deleted. Each installation whose
/// definition changed is observed again.
/// </summary>
public sealed partial class InventoryUpdater(RunFactory factory, RunEngine engine, RevisionCache revisions, InventoryFaults faults, TimeProvider clock,
    ILogger<InventoryUpdater> logger)
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
                && repository.Resource(previous) is null)
            {
                logger.LogInformation("{Previous} was renamed to {Address}.", previous, guest.Address);
                byAddress.Remove(previous);
                renamed.Address = guest.Address;
                byAddress[guest.Address] = renamed;
                MoveServices(byAddress, previous, guest.Address);
            }
        }

        var seen = new HashSet<Guid>();
        foreach (var entry in repository.Resources)
        {
            if (!byAddress.TryGetValue(entry.Address, out var resource))
            {
                if (!entry.Valid && repository.Guest(entry.Address)?.RenamedFrom is { } renamedFrom
                    && repository.Resource($"{entry.ParentAddress}/{renamedFrom}") is null
                    && byAddress.TryGetValue($"{entry.ParentAddress}/{renamedFrom}", out var pendingRename))
                {
                    // Keep a guest's identity at its old address while
                    // displaying the invalid proposed definition. Do not create a
                    // competing row that would block the rename after correction.
                    resource = pendingRename;
                }
                // A service waits for its guest's row: one that a rename has not reached yet keeps its own.
                if (resource is null && entry.Kind == ResourceKind.Service && !byAddress.ContainsKey(entry.ParentAddress!))
                {
                    continue;
                }

                if (resource is null && repository.Guest(entry.Kind == ResourceKind.Service ? entry.ParentAddress! : entry.Address)?.Retired == true)
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
            resource.Definition = entry.Json?.ToJsonString();
            resource.DefinitionFile = entry.File;
            resource.Workflow = entry.Workflow;
            resource.Issues = JsonSerializer.Serialize(repository.IssuesFor(entry.Address).ToList(), IssueJson);
            if (entry.Kind == ResourceKind.Installation)
                await RecordObservedOnlyAsync(db, resource, entry, cancellationToken);
        }

        var guests = byAddress.Values.Where(r => r.Kind == ResourceKinds.Guest && seen.Contains(r.Id) && r.RetiredAt is null).ToList();
        foreach (var resource in guests.Where(r => r.Valid))
        {
            // Missing definitions still retain durable ownership. In particular,
            // an invalid rename must not let a second row claim a guest's VM.
            var definition = repository.Guest(resource.Address)!;
            var problem = IdentityProblem(resource, definition, byAddress.Values);
            // A reservation is for one machine: a guest that is not bound yet and now names another lets it go.
            if (resource.BoundVmid is null && resource.ReservedVmid is not null
                && (resource.ReservedVmid != definition.Vmid || resource.ReservedNode != definition.Host || resource.ReservedInstallation != definition.InstallationAddress))
                (resource.ReservedInstallation, resource.ReservedNode, resource.ReservedVmid, resource.ReservedAt, resource.ReservationAppliedAt) = (null, null, null, null, null);
            // A VM the installation's latest observation shows at this VMID, and that is not this guest's, for a
            // provision and a retire alike. Observations are runtime data, so the dispatch of the plan asks again.
            if (problem is null)
                problem = await SafetyBoundary.VmidCollisionAsync(db, resource, definition.InstallationAddress, definition.Vmid, cancellationToken);
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
        // The resources whose issues this revision set, kept per revision for what follows a change through it.
        var examined = byAddress.Values.Where(r => seen.Contains(r.Id)).ToList();
        if (repository.RepositoryValid)
        {
            foreach (var resource in byAddress.Values.Where(r => !seen.Contains(r.Id) && r.RetiredAt is null))
            {
                resource.MissingSince ??= now;
                await SupersedeAsync(db, activeRuns, revision, resource, $"The definition of {resource.Address} disappeared in {Short(revision.Commit)}.",
                    cancellationToken, keepRemoval: true);
                // Deleting a folder asks for removal; the destructive policy decides whether it waits for approval.
                if (resource.Kind != ResourceKinds.Guest) continue;
                try
                {
                    if (await GuestRemoval.ProposeAsync(db, revisions, factory, resource, revision, force: false, cancellationToken) is { } removal)
                        runs.Add(removal);
                }
                catch (RunException error)
                {
                    examined.Add(resource);
                    logger.LogWarning("The removal of {Address} cannot be planned: {Error}", resource.Address, error.Message);
                    resource.Issues = JsonSerializer.Serialize(new[] { new DefinitionIssue(IssueSeverity.Error, resource.DefinitionFile ?? resource.Address,
                        null, $"The folder was deleted, but its removal cannot be planned: {error.Message}", resource.Address) }, IssueJson);
                }
            }
        }

        var changedInstallations = new List<(Resource Resource, RunContext Context)>();
        // Whether Octopushy holds a key for a guest is part of whether it wants one; see ManagedAccess.Wanted.
        var guestIds = guests.Select(g => g.Id).ToList();
        var issued = (await db.GuestAccessKeys.AsNoTracking().Where(k => guestIds.Contains(k.ResourceId)).Select(k => k.ResourceId).Distinct()
            .ToListAsync(cancellationToken)).ToHashSet();
        foreach (var resource in guests)
        {
            if (!resource.Valid)
            {
                await SupersedeAsync(db, activeRuns, revision, resource, $"The definition is invalid in {Short(revision.Commit)}.", cancellationToken);
                continue;
            }

            var guest = repository.Guest(resource.Address)!;
            var inputs = ProvisionInputs.Build(repository, guest, revision.Commit, issued.Contains(resource.Id));
            var desired = guest.Retired ? RunFactory.RetireHash(inputs.Hash) : inputs.Hash;
            // A desired hash only counts as proposed while a run exists for its occurrence: one recorded without its run, as a
            // crash could leave before revisions were applied in one transaction, is proposed now, even by a later revision that
            // changes nothing for the guest. A run for the same inputs at an earlier time is not this work.
            var unfinished = !await GuestRemoval.ProposedAsync(db, resource, desired, cancellationToken);
            if (resource.DesiredInputsHash == desired && currentSourceIds.Contains(resource.DesiredRevisionId ?? Guid.Empty) && !unfinished)
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
                proposed = factory.Guest(repository, guest, resource, revision, "revision", issued.Contains(resource.Id));
            }
            catch (RunException error)
            {
                // Controller/image compatibility is a per-guest refusal, not a
                // reason to abandon other runs after recording this repository head.
                resource.Valid = false;
                var issues = repository.IssuesFor(resource.Address).ToList();
                issues.Add(new DefinitionIssue(IssueSeverity.Error, guest.File, null, error.Message, resource.Address));
                resource.Issues = JsonSerializer.Serialize(issues, IssueJson);
                await SupersedeAsync(db, activeRuns, revision, resource, $"The definition cannot be planned in {Short(revision.Commit)}.", cancellationToken);
                continue;
            }

            // The same work proposed again, when it was recorded without its run, keeps its occurrence; new work gets a new one.
            GuestRemoval.Record(resource, desired, revision.Id, proposed,
                sameWork: resource.DesiredInputsHash == desired && currentSourceIds.Contains(resource.DesiredRevisionId ?? Guid.Empty));
            await SupersedeAsync(db, activeRuns, revision, resource, $"Superseded by {Short(revision.Commit)}.", cancellationToken);
            runs.Add(proposed);
        }

        await UpdateConfigurationsAsync(db, activeRuns, revision, repository, guests, cancellationToken);
        await UpdatePipelinesAsync(db, activeRuns, revision, repository, byAddress.Values.Where(r => r.Kind == ResourceKinds.Service && seen.Contains(r.Id) && r.RetiredAt is null).ToList(), currentSourceIds, cancellationToken);

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

        await RecordIssuesAsync(db, revision, examined, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (faults.BeforeRuns is { } fault)
            await fault();
        await engine.AddRunsAsync(db, runs, cancellationToken);
        foreach (var (resource, context) in changedInstallations)
        {
            await engine.ObserveAsync(db, resource, revision, context, cancellationToken);
        }
    }

    /// <summary>The errors each examined resource has after this revision, kept for this revision alone.</summary>
    private static async Task RecordIssuesAsync(OctopushyDb db, DefinitionRevision revision, IEnumerable<Resource> examined, CancellationToken cancellationToken)
    {
        var ids = examined.Select(r => r.Id).ToList();
        var recorded = await db.RevisionIssues.Where(i => i.RevisionId == revision.Id && ids.Contains(i.ResourceId))
            .ToDictionaryAsync(i => i.ResourceId, cancellationToken);
        foreach (var resource in examined)
        {
            var errors = (JsonSerializer.Deserialize<List<DefinitionIssue>>(resource.Issues, IssueJson) ?? [])
                .Where(i => i.Severity == IssueSeverity.Error).ToList();
            recorded.TryGetValue(resource.Id, out var row);
            if (errors.Count == 0)
            {
                if (row is not null)
                    db.RevisionIssues.Remove(row);
                continue;
            }

            if (row is null)
            {
                row = new RevisionIssues { ResourceId = resource.Id, RevisionId = revision.Id };
                db.RevisionIssues.Add(row);
            }

            row.Issues = JsonSerializer.Serialize(errors, IssueJson);
        }
    }

    /// <summary>
    /// Records what each guest's setup stage wants in this revision. The orchestrator proposes
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
            if (guest.Setup is null || guest.Retired)
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
    /// Keeps the denylist on the installation itself, so it does not depend on its definition being readable:
    /// a definition that says <c>manage: false</c>, even one with other errors, puts the installation, its
    /// token keys and its endpoints on it. Only a valid definition that asks to be managed takes the
    /// installation off, and its keys and endpoints stay refused to other installations' work until an
    /// operator trusts it again as managed. An invalid or missing definition changes nothing.
    /// </summary>
    /// <remarks>
    /// Entering observed-only ends any trust given while the installation was managed: that grant becomes one for
    /// observations alone, so going back to managed takes trust again, however briefly it was observed only.
    /// </remarks>
    private static async Task RecordObservedOnlyAsync(OctopushyDb db, Resource resource, ResourceEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Json is { } json && SafetyBoundary.ObservedOnly(json))
        {
            if (!resource.ObservedOnly
                && await db.InstallationTrusts.FirstOrDefaultAsync(t => t.Address == resource.Address, cancellationToken) is { ObservedOnly: false } trust)
                trust.ObservedOnly = true;
            resource.ObservedOnly = true;
            resource.ObservedOnlyStoreKeys = resource.ObservedOnlyStoreKeys.Union(SafetyBoundary.TokenKeys(json), StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToList();
            resource.ObservedOnlyEndpoints = resource.ObservedOnlyEndpoints.Union(SafetyBoundary.Endpoints(json), StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal).ToList();
        }
        else if (entry.Valid && entry.Json is not null)
        {
            resource.ObservedOnly = false;
        }
    }

    /// <summary>
    /// A bound guest keeps its installation and VMID; changing them would silently point the
    /// guest at another VM. A VMID bound to one guest cannot be claimed by another.
    /// </summary>
    private static string? IdentityProblem(Resource resource, GuestDefinition guest, IEnumerable<Resource> guests)
    {
        if (resource.BoundVmid is { } bound && (bound != guest.Vmid || resource.BoundInstallation != guest.InstallationAddress))
        {
            return $"This guest is bound to VMID {bound} on {resource.BoundInstallation}. Changing it would make a different VM; retire this guest and add a new one instead.";
        }

        var other = guests.FirstOrDefault(r => r.Id != resource.Id && r.BoundVmid == guest.Vmid && r.BoundInstallation == guest.InstallationAddress);
        return other is null ? null : $"VMID {guest.Vmid} on {guest.InstallationAddress} is bound to {other.Address}.";
    }

    private async Task SupersedeAsync(OctopushyDb db, List<Run> activeRuns, DefinitionRevision revision, Resource resource, string reason,
        CancellationToken cancellationToken, bool keepRemoval = false)
    {
        foreach (var run in activeRuns.Where(r => r.ResourceId == resource.Id && r.RevisionId != revision.Id).ToList())
        {
            // A guest that stays deleted keeps its proposed removal.
            if (keepRemoval && run.Operation == Operations.Retire && RunContext.Parse(run.Context).DefinitionRemovedIn is not null)
                continue;
            if (RunEngine.CanSupersede(run))
            {
                await engine.CancelInternalAsync(db, run.Id, reason, cancellationToken);
                activeRuns.Remove(run);
                // It never ran, so the inputs it was made for are still to be configured.
                if (run.Operation is Operations.Configure or Operations.Pipeline
                    && await db.GuestConfigurations.FirstOrDefaultAsync(c => c.ResourceId == resource.Id, cancellationToken) is { } configuration)
                    configuration.ProposedHash = null;
            }
        }
    }

    private static string Kind(ResourceKind kind) => kind switch
    {
        ResourceKind.Lab => ResourceKinds.Lab,
        ResourceKind.Installation => ResourceKinds.Installation,
        ResourceKind.Service => ResourceKinds.Service,
        _ => ResourceKinds.Guest,
    };

    private static string Short(string commit) => commit[..Math.Min(10, commit.Length)];
}
