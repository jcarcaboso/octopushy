using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Inventory;

/// <summary>
/// Applies a new revision to the resources Octopushy knows, and proposes the runs it implies:
/// a provision run for each guest whose inputs changed, a retire run for each guest newly
/// retired, and an observe run for each installation whose definition changed.
/// </summary>
public sealed class InventoryUpdater(RunFactory factory, RunEngine engine, TimeProvider clock, ILogger<InventoryUpdater> logger)
{
    public static readonly JsonSerializerOptions IssueJson = new(JsonSerializerDefaults.Web);

    /// <summary>Runs inside the engine's gate, with the revision already saved.</summary>
    public async Task ApplyAsync(OctopushyDb db, DefinitionRevision revision, LabRepository repository, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var active = await db.Resources.Where(r => r.RetiredAt == null).ToListAsync(cancellationToken);
        var byAddress = active.ToDictionary(r => r.Address, StringComparer.Ordinal);

        foreach (var guest in repository.Guests.Where(g => g.RenamedFrom is not null))
        {
            var previous = $"{guest.InstallationAddress}/{guest.RenamedFrom}";
            if (!byAddress.ContainsKey(guest.Address) && byAddress.Remove(previous, out var renamed) && repository.Resource(previous) is null)
            {
                logger.LogInformation("{Previous} was renamed to {Address}.", previous, guest.Address);
                renamed.Address = guest.Address;
                byAddress[guest.Address] = renamed;
            }
        }

        var seen = new HashSet<Guid>();
        foreach (var entry in repository.Resources)
        {
            if (!byAddress.TryGetValue(entry.Address, out var resource))
            {
                if (entry.Kind == ResourceKind.Guest && repository.Guest(entry.Address)?.Retired == true)
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
            var problem = IdentityProblem(resource, repository.Guest(resource.Address)!, guests);
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

        if (repository.RepositoryValid)
        {
            foreach (var resource in byAddress.Values.Where(r => !seen.Contains(r.Id) && r.RetiredAt is null))
            {
                resource.MissingSince ??= now;
                await SupersedeAsync(db, activeRuns, resource, $"The definition of {resource.Address} disappeared in {Short(revision.Commit)}; nothing runs until it is restored or retired.", cancellationToken);
            }
        }

        var runs = new List<Run>();
        foreach (var resource in guests)
        {
            if (!resource.Valid)
            {
                await SupersedeAsync(db, activeRuns, resource, $"The definition is invalid in {Short(revision.Commit)}.", cancellationToken);
                continue;
            }

            var guest = repository.Guest(resource.Address)!;
            var inputs = ProvisionInputs.Build(repository, guest, revision.Commit);
            var desired = guest.Retired ? RunFactory.RetireHash(inputs.Hash) : inputs.Hash;
            if (resource.DesiredInputsHash == desired)
            {
                continue;
            }

            resource.DesiredInputsHash = desired;
            resource.DesiredRevisionId = revision.Id;
            await SupersedeAsync(db, activeRuns, resource, $"Superseded by {Short(revision.Commit)}.", cancellationToken);
            runs.Add(factory.Guest(repository, guest, resource, revision, "revision"));
        }

        foreach (var resource in byAddress.Values.Where(r => r.Kind == ResourceKinds.Installation && seen.Contains(r.Id) && r.Valid))
        {
            var installation = repository.Installation(resource.Address)!;
            var hash = RunFactory.InstallationHash(installation);
            if (resource.DesiredInputsHash == hash)
            {
                continue;
            }

            resource.DesiredInputsHash = hash;
            resource.DesiredRevisionId = revision.Id;
            if (!activeRuns.Any(r => r.ResourceId == resource.Id))
            {
                runs.Add(factory.Observe(repository, installation, resource, revision, "revision"));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await engine.AddRunsAsync(db, runs, cancellationToken);
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

    private async Task SupersedeAsync(OctopushyDb db, List<Run> activeRuns, Resource resource, string reason, CancellationToken cancellationToken)
    {
        foreach (var run in activeRuns.Where(r => r.ResourceId == resource.Id && r.Operation != Operations.Observe).ToList())
        {
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
