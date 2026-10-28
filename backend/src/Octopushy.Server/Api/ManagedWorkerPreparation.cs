using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Options;
using Octopushy.Server.Agents;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Api;

public sealed record WorkerHostOption(
    string Id, string Endpoint, IReadOnlyDictionary<string, string> Datastores, IReadOnlyList<string> Bridges);

public sealed record WorkerInstallationOption(
    string Address, string Name, bool VerifyTls, bool SharedToken, IReadOnlyList<WorkerHostOption> Hosts);

public sealed record WorkerPreparationDto(
    string? SourceUrl, string? Branch, string? HeadCommit, string? LastFetchError, RevisionDto? Revision,
    string? PreparationBlockedReason, IReadOnlyList<WorkerInstallationOption> Installations,
    IReadOnlyList<string> DeploymentGates,
    IReadOnlyList<string> StateBackends,
    string? DeploymentBlockedReason,
    IReadOnlyList<WorkerExistingDto> ExistingWorkers);

// Do not accept caller endpoints, commands, credentials or bootstrap destinations.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkerDraftRequest(
    Guid RevisionId, string Installation, string Host, string Name, int Vmid,
    int Sockets, int Cores, int MemoryMiB, int BootDiskGiB, int StateDiskGiB,
    string Datastore, string Bridge, int? Vlan);

public sealed record WorkerDraftDto(string FileName, string Content);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkerRetirementRequest(Guid Generation, bool ConfirmAlreadyRemoved);

public sealed record WorkerRetirementRunDto(Guid RunId);

/// <summary>Read-only preparation; reviewed Git publication is a separate explicit action.</summary>
public static partial class ManagedWorkerPreparation
{
    private static readonly string[] DeploymentGates =
    [
        "Publication adds a normal guest and the versioned worker recipe to Git. It does not approve an infrastructure plan.",
        "VM bootstrap creates its private key locally. Ready requires the correlated agent to connect with mTLS, not merely a running VM.",
        "An independent upgraded control worker must provide managed-worker, nixos-rootfs, proxmox and opentofu executors.",
        "Controller agent ingress and workload endpoints must be guest-reachable. A localhost PostgreSQL backend is not usable by this worker.",
        "Live VMID availability, storage, network and capacity are checked by the existing preview/apply flow, not reserved by this wizard.",
        "Deleting an agent revokes enrollment, not its VM. Retirement verification only checks an already removed VM after its Git definition and enrollment are removed; automatic VM destruction and replacement remain disabled.",
    ];

    public static void MapWorkerPreparation(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/managed-worker-preparation").WithTags("Agents");
        group.MapGet("", async (OctopushyDb db, RevisionCache revisions, IOptions<OctopushyOptions> options, AgentConnections connections, CancellationToken ct) =>
        {
            var settings = await db.RepositorySettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct);
            var revision = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(ct);
            if (revision is not null && (!RepositoryTrust.IsCurrentSource(revision, settings) || settings?.HeadCommit != revision.Commit))
                revision = null;
            var repository = revision is null ? null : await revisions.GetAsync(revision.Commit, ct);
            var blocked = settings?.Url is null ? "Configure a lab repository first."
                : revision is null ? "Sync the current repository source; its head revision has not been loaded."
                : !revision.RepositoryValid || repository?.RepositoryValid != true ? "Fix the repository definition and sync before preparing a draft."
                : null;
            var installations = blocked is not null || repository is null ? [] : repository.Installations
                .Where(i => repository.IsValid(i.Address))
                .Select(i => new WorkerInstallationOption(i.Address, i.DisplayName, i.Connection.VerifyTls, i.Connection.SharedToken,
                    i.Hosts.Values.Select(h => new WorkerHostOption(h.Id, ProvisionInputs.EndpointFor(i, h),
                        h.Datastores, h.Bridges)).ToList())).ToList();
            var boundResources = await db.Resources.AsNoTracking().Include(r => r.ManagedWorker)
                .Where(r => r.ManagedWorker != null && r.RetiredAt == null).ToListAsync(ct);
            var enrollments = await db.Agents.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Disabled, ct);
            var workers = (repository?.Guests.Where(g => g.ManagedWorker is not null)
                .Select(g => new WorkerExistingDto(g.Address, g.Host, g.Vmid, g.ManagedWorker!.Generation)) ?? [])
                .ToDictionary(w => w.Address, StringComparer.Ordinal);
            foreach (var resource in boundResources)
            {
                var binding = resource.ManagedWorker!;
                var connected = binding.AgentId is { } id && connections.IsConnected(id);
                var retirement = await db.Runs.AsNoTracking().Where(r => r.ResourceId == resource.Id && r.Operation == Operations.Retire
                    && r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled)
                    .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
                var blockedRetirement = await ManagedWorkerSafety.RetirementBlockedReasonAsync(db, resource, null, ct);
                var status = binding.AgentId is { } agentId && !enrollments.ContainsKey(agentId) ? "Enrollment removed"
                    : binding.AgentId is { } enrolledId && enrollments.GetValueOrDefault(enrolledId) ? "Disabled"
                    : resource.MissingSince is not null ? "Definition removed"
                    : binding.ReadyAt is not null ? connected ? "Ready" : "Enrolled · offline"
                    : binding.InstalledAt is not null ? "Waiting for authenticated connection"
                    : binding.PreparedAt is not null ? "Installing public identity" : "Provisioning";
                if (retirement is not null) status = "Verifying retirement";
                workers[resource.Address] = new(resource.Address, binding.Host, binding.Vmid, binding.Generation,
                    resource.Id, retirement?.Id ?? binding.RunId, binding.AgentId, status,
                    blockedRetirement is null, blockedRetirement, binding.VerifiedOsImageSha256, binding.VerifiedOsUpdateRunId);
            }
            return TypedResults.Ok(new WorkerPreparationDto(settings?.Url, settings?.Branch, settings?.HeadCommit,
                settings?.LastFetchError, revision is null ? null : Views.Revision(revision, true),
                blocked, installations, DeploymentGates,
                repository?.Settings?.StateBackends.Keys.ToArray() ?? [],
                blocked ?? WorkerRepositoryDeployment.BlockedReason(options.Value),
                workers.Values.ToArray()));
        }).WithName("getWorkerPreparation");

        group.MapPost("/{id:guid}/retire", async Task<Results<Ok<WorkerRetirementRunDto>, NotFound, ProblemHttpResult>> (
            Guid id, WorkerRetirementRequest request, HttpContext http, RunEngine engine, CancellationToken ct) =>
        {
            if (!request.ConfirmAlreadyRemoved)
                return TypedResults.Problem(statusCode: 400, title: "Confirm that this verifies an already removed VM; it never destroys infrastructure.");
            try
            {
                var result = await engine.ExecuteAsync<WorkerRetirementRunDto?>(async (db, services) =>
                {
                    var resource = await db.Resources.SingleOrDefaultAsync(r => r.Id == id && r.Kind == ResourceKinds.Guest, ct);
                    if (resource?.ManagedWorker is not { } binding) return null;
                    if (request.Generation != binding.Generation)
                        throw new RunException("The worker generation changed. Reload before verifying retirement.");
                    var existing = await db.Runs.Include(r => r.Steps).Where(r => r.ResourceId == id && r.Operation == Operations.Retire)
                        .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
                    if (existing is not null && ManagedWorkerSafety.IsRetirementCheck(existing)
                        && (resource.RetiredAt is not null || !RunStatuses.IsTerminal(existing.Status)))
                        return new(existing.Id);
                    if (await ManagedWorkerSafety.RetirementBlockedReasonAsync(db, resource, null, ct) is { } reason)
                        throw new RunException(reason);
                    var settings = await db.RepositorySettings.SingleAsync(s => s.Id == 1, ct);
                    var revision = await RepositoryTrust.EligibleRevisions(db).Where(r => r.Commit == settings.HeadCommit)
                        .OrderByDescending(r => r.Sequence).FirstAsync(ct);
                    var repository = await services.GetRequiredService<RevisionCache>().GetAsync(revision.Commit, ct);
                    var installation = repository.Installation(binding.Installation);
                    if (installation is null || !repository.IsValid(installation.Address))
                        throw new RunException("Restore the worker's valid original Proxmox installation before verifying retirement.");
                    var original = await db.Runs.SingleAsync(r => r.Id == binding.RunId, ct);
                    var run = services.GetRequiredService<RunFactory>().VerifyWorkerRetirement(repository, installation, resource,
                        revision, original, $"retirement verification requested by {http.User.Identity!.Name}");
                    await engine.AddRunsAsync(db, [run], ct);
                    return new(run.Id);
                }, ct);
                return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
            }
            catch (RunException e) { return TypedResults.Problem(statusCode: 409, title: e.Message); }
        }).WithName("verifyManagedWorkerRetirement");

        group.MapPost("", async Task<Results<Ok<WorkerDraftDto>, ProblemHttpResult>> (
            WorkerDraftRequest request, OctopushyDb db, RevisionCache revisions, CancellationToken ct) =>
        {
            var revision = await RepositoryTrust.HeadRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(ct);
            var settings = await db.RepositorySettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct);
            if (revision is null || revision.Id != request.RevisionId
                || !RepositoryTrust.IsCurrentSource(revision, settings) || settings?.HeadCommit != revision.Commit)
            {
                return TypedResults.Problem(statusCode: 409, title: "The current repository source or head changed. Reload preparation before generating a draft.");
            }
            var repository = await revisions.GetAsync(revision.Commit, ct);
            if (!revision.RepositoryValid || !repository.RepositoryValid)
            {
                return TypedResults.Problem(statusCode: 409, title: "The current repository definition is invalid. Fix it and sync first.");
            }
            var installation = repository.Installation(request.Installation);
            if (installation is null || !repository.IsValid(installation.Address)
                || request.Host is null || !installation.Hosts.TryGetValue(request.Host, out var host))
            {
                return TypedResults.Problem(statusCode: 400, title: "Select a valid configured Proxmox installation and one of its configured hosts.");
            }
            if (Validate(repository, installation, host, request) is { } error)
            {
                return TypedResults.Problem(statusCode: 400, title: error);
            }
            // Deliberately NOT a guest definition: even renaming this to guest.yaml fails schema validation.
            // No worker role is enabled until bootstrap and lifecycle guards exist end to end.
            var network = new JsonObject { ["bridge"] = request.Bridge, ["mode"] = "dhcp" };
            if (request.Vlan is { } vlan)
            {
                network["vlan"] = vlan;
            }
            var draft = new JsonObject
            {
                ["kind"] = "octopushy-managed-worker-preparation",
                ["schemaVersion"] = 1,
                ["preparationOnly"] = true,
                ["notice"] = "Review artifact only, not guest.yaml or an executable definition. Commit under docs/worker-drafts for review; do not copy into guests. No deployment, enrollment, reservation or approval has occurred.",
                ["baseRevision"] = JsonSerializer.SerializeToNode(Views.Revision(revision, true), JsonSerializerOptions.Web),
                ["repositorySource"] = new JsonObject
                {
                    ["id"] = revision.SourceId,
                    ["branch"] = settings!.Branch,
                },
                // References only: do not copy connection objects or possibly credential-bearing URLs into Git.
                ["installation"] = installation.Address,
                ["proxmoxVerifyTls"] = installation.Connection.VerifyTls,
                ["credentialMode"] = installation.Connection.SharedToken ? "shared" : "split",
                ["guest"] = new JsonObject
                {
                    ["id"] = request.Name,
                    ["type"] = "vm",
                    ["host"] = host.Id,
                    ["vmid"] = request.Vmid,
                    ["hardware"] = new JsonObject
                    {
                        ["sockets"] = request.Sockets,
                        ["cores"] = request.Cores,
                        ["memory"] = request.MemoryMiB,
                        ["disks"] = new JsonArray(
                            new JsonObject { ["size"] = request.BootDiskGiB, ["datastore"] = request.Datastore },
                            new JsonObject { ["size"] = request.StateDiskGiB, ["datastore"] = request.Datastore }),
                    },
                    ["network"] = network,
                    ["policy"] = new JsonObject { ["approval"] = "manual", ["destructive"] = "manual" },
                },
                ["deploymentGates"] = JsonSerializer.SerializeToNode(DeploymentGates),
            };
            return TypedResults.Ok(new WorkerDraftDto($"{request.Name}.managed-worker-preparation.json",
                draft.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n"));
        }).WithName("prepareWorkerDraft");
    }

    internal static string? Validate(LabRepository repository, InstallationDefinition installation, HostDefinition host, WorkerDraftRequest r)
    {
        if (r.Name is null || !GuestId().IsMatch(r.Name))
            return "Guest name must be 1–63 lowercase letters, digits or hyphens, starting and ending with a letter or digit.";
        if (r.Vmid is < 100 or > 999999999)
            return "VMID must be between 100 and 999999999; it is not reserved by preparation.";
        if (repository.Resource($"{installation.Address}/{r.Name}") is not null
            || repository.Guests.Any(g => g.InstallationAddress == installation.Address && g.Vmid == r.Vmid))
            return "That guest name or VMID is already declared in this installation. Choose a different identity.";
        if (r.Sockets is < 1 or > 16 || r.Cores is < 1 or > 128 || (long)r.Sockets * r.Cores > 128)
            return "Choose 1–16 CPU sockets and 1–128 cores per socket, at most 128 vCPUs in total.";
        if (r.MemoryMiB is < 512 or > 1048576)
            return "RAM must be between 512 and 1048576 MiB.";
        if (r.BootDiskGiB is < 8 or > 65536 || r.StateDiskGiB is < 16 or > 65536)
            return "Choose a boot disk of 8–65536 GiB and a separate agent state disk of 16–65536 GiB.";
        if (string.IsNullOrWhiteSpace(r.Datastore) || !host.Datastores.Values.Contains(r.Datastore, StringComparer.Ordinal))
            return "Select a datastore declared on the selected host; live storage support is not checked.";
        if (string.IsNullOrWhiteSpace(r.Bridge) || !host.Bridges.Contains(r.Bridge, StringComparer.Ordinal))
            return "Select an Ethernet bridge declared on the selected host.";
        return r.Vlan is < 1 or > 4094 ? "VLAN must be omitted or between 1 and 4094." : null;
    }

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z")]
    private static partial Regex GuestId();
}
