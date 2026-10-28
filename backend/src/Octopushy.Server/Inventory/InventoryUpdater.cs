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
/// retired, and, once the revision is trusted, a retire run for each guest whose folder was
/// deleted. Each installation whose definition changed is observed again.
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
            resource.Definition = entry.Json?.ToJsonString();
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
                await SupersedeAsync(db, activeRuns, revision, resource, $"The definition of {resource.Address} disappeared in {Short(revision.Commit)}.",
                    cancellationToken, keepRemoval: true);
                // Deleting a folder asks for removal, but only once the deleting revision is trusted:
                // an unreviewed commit never proposes destroying anything.
                if (revision.TrustedAt is null || resource.Kind != ResourceKinds.Guest) continue;
                try
                {
                    if (await GuestRemoval.ProposeAsync(db, revisions, factory, resource, revision, force: false, cancellationToken) is { } removal)
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

        var changedInstallations = new List<(Resource Resource, RunContext Context)>();
        foreach (var resource in guests)
        {
            if (!resource.Valid)
            {
                await SupersedeAsync(db, activeRuns, revision, resource, $"The definition is invalid in {Short(revision.Commit)}.", cancellationToken);
                continue;
            }

            var guest = repository.Guest(resource.Address)!;
            var inputs = ProvisionInputs.Build(repository, guest, revision.Commit);
            var desired = guest.Retired ? RunFactory.RetireHash(inputs.Hash) : inputs.Hash;
            if (resource.DesiredInputsHash == desired && currentSourceIds.Contains(resource.DesiredRevisionId ?? Guid.Empty))
            {
                // Only promote a real replacement already proposed by discovery. Metadata
                // or policy-only revisions leave the older pinned plan/approval intact.
                if (revision.TrustedAt is not null && activeRuns.Any(r => r.ResourceId == resource.Id && r.RevisionId == revision.Id))
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
                await SupersedeAsync(db, activeRuns, revision, resource, $"The definition cannot be planned in {Short(revision.Commit)}.", cancellationToken);
                continue;
            }

            resource.DesiredInputsHash = desired;
            resource.DesiredRevisionId = revision.Id;
            await SupersedeAsync(db, activeRuns, revision, resource, $"Superseded by {Short(revision.Commit)}.", cancellationToken);
            runs.Add(proposed);
        }

        foreach (var resource in byAddress.Values.Where(r => r.Kind == ResourceKinds.Installation && seen.Contains(r.Id) && r.Valid))
        {
            var installation = repository.Installation(resource.Address)!;
            var hash = RunFactory.InstallationHash(installation);
            if (resource.DesiredInputsHash == hash && currentSourceIds.Contains(resource.DesiredRevisionId ?? Guid.Empty))
            {
                // Trusting the head lets its observation replace a trusted one that discovery kept waiting.
                if (revision.TrustedAt is not null && resource.DesiredRevisionId == revision.Id)
                    changedInstallations.Add((resource, RunFactory.Observation(repository, installation, revision)));
                continue;
            }

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

    private async Task SupersedeAsync(OctopushyDb db, List<Run> activeRuns, DefinitionRevision revision, Resource resource, string reason,
        CancellationToken cancellationToken, bool keepRemoval = false)
    {
        foreach (var run in activeRuns.Where(r => r.ResourceId == resource.Id && r.RevisionId != revision.Id).ToList())
        {
            // A guest that stays deleted keeps its proposed removal.
            if (keepRemoval && run.Operation == Operations.Retire && RunContext.Parse(run.Context).DefinitionRemovedIn is not null)
                continue;
            // Discovery must not discard a prepared, trusted plan for an untrusted head.
            if (revision.TrustedAt is null && await RepositoryTrust.BlockedReasonAsync(db, run, cancellationToken) is null)
                continue;
            if (RunEngine.CanSupersede(run))
            {
                await engine.CancelInternalAsync(db, run.Id, reason, cancellationToken);
                activeRuns.Remove(run);
            }
        }
    }

    private static string Kind(ResourceKind kind) => kind switch
    {
        ResourceKind.Lab => ResourceKinds.Lab,
        ResourceKind.Installation => ResourceKinds.Installation,
        _ => ResourceKinds.Guest,
    };

    private static string Short(string commit) => commit[..Math.Min(10, commit.Length)];
}
