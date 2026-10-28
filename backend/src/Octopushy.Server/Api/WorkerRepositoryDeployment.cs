using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;

namespace Octopushy.Server.Api;

public sealed record WorkerExistingDto(string Address, string Host, int Vmid, Guid Generation,
    Guid? ResourceId = null, Guid? RunId = null, Guid? AgentId = null, string Status = "Declared",
    bool CanVerifyRetirement = false, string? RetirementBlockedReason = null,
    string? VerifiedOsImageSha256 = null, Guid? VerifiedOsUpdateRunId = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkerDeploymentRequest(WorkerDraftRequest Selection, string StateBackend);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkerPublishRequest(WorkerDeploymentRequest Draft, string ReviewSha256, bool ConfirmRepositoryWrite);

public sealed record WorkerReviewDto(string Address, Guid Generation, string Branch, string BaseCommit,
    string ReviewSha256, IReadOnlyDictionary<string, string> Files, bool AlreadyExists);

public sealed record WorkerPublishedDto(string Address, string Commit, bool AlreadyPresent);

public static class WorkerRepositoryDeployment
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static string? BlockedReason(OctopushyOptions options)
    {
        if (!options.WorkerDeployment.RepositoryWritesEnabled)
            return "The operator must enable WorkerDeployment.RepositoryWritesEnabled before this wizard can add definitions to Git.";
        if (!Uri.TryCreate(options.WorkerDeployment.ImageUrl, UriKind.Absolute, out var image)
            || image.Scheme is not ("http" or "https") || image.UserInfo.Length != 0
            || image.Query.Length != 0 || image.Fragment.Length != 0
            || options.WorkerDeployment.ImageSha256 is not { Length: 64 } hash
            || hash.Any(c => !char.IsAsciiHexDigit(c)))
            return "Publish the tested worker-v1 image and configure its public ImageUrl and exact ImageSha256 on the controller.";
        try { ManagedWorkerSafety.PublicUrl(options.AgentChannel.PublicUrl); }
        catch (RunException e) { return e.Message; }
        return null;
    }

    public static void MapWorkerRepositoryDeployment(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/managed-worker-preparation").WithTags("Agents");
        group.MapPost("/review", async Task<Results<Ok<WorkerReviewDto>, ProblemHttpResult>> (
            WorkerDeploymentRequest request, RunEngine engine, IOptions<OctopushyOptions> options, CancellationToken ct) =>
        {
            try
            {
                return TypedResults.Ok(await engine.ExecuteAsync((db, services) =>
                    ReviewAsync(db, services.GetRequiredService<RevisionCache>(), options.Value, request, ct), ct));
            }
            catch (GitException e) { return TypedResults.Problem(statusCode: 409, title: e.Message); }
        }).WithName("reviewWorkerDeployment");

        group.MapPost("/publish", async Task<Results<Ok<WorkerPublishedDto>, ProblemHttpResult>> (
            WorkerPublishRequest request, RunEngine engine, RepositoryMirror mirror, RepositorySync sync,
            IOptions<OctopushyOptions> options, CancellationToken ct) =>
        {
            if (!request.ConfirmRepositoryWrite || request.Draft is null || string.IsNullOrEmpty(request.ReviewSha256))
                return TypedResults.Problem(statusCode: 400, title: "Explicit confirmation is required before adding files to the configured repository branch.");
            try
            {
                // ponytail: one bounded Git CAS under the existing gate; prevents settings changes
                // between revalidation and publication. Move to durable intents if write volume grows.
                var result = await engine.ExecuteAsync(async (db, services) =>
                {
                    var review = await ReviewAsync(db, services.GetRequiredService<RevisionCache>(), options.Value, request.Draft, ct);
                    if (review.ReviewSha256 != request.ReviewSha256)
                        throw new GitException("The reviewed files or source changed. Review again before publishing.");
                    var settings = await db.RepositorySettings.SingleAsync(s => s.Id == 1, ct);
                    if (review.AlreadyExists)
                        return new WorkerPublishedDto(review.Address, review.BaseCommit, true);
                    var credential = GitCredential.None;
                    if (settings.CredentialKind != "none")
                    {
                        var stored = await services.GetRequiredService<VariableStore>().GetAsync(RepositorySync.CredentialKey, ct)
                            ?? throw new GitException("The configured repository credential is missing.");
                        credential = new(settings.CredentialKind, settings.CredentialUsername, stored.Value);
                    }
                    var written = await mirror.AddFilesAsync(settings.Url!, settings.Branch, review.BaseCommit, review.Files, credential, ct);
                    return new WorkerPublishedDto(review.Address, written.Commit, written.AlreadyPresent);
                }, ct);
                sync.Trigger();
                return TypedResults.Ok(result);
            }
            catch (GitException e) { return TypedResults.Problem(statusCode: 409, title: e.Message); }
        }).WithName("publishWorkerDeployment");
    }

    internal static async Task<WorkerReviewDto> ReviewAsync(OctopushyDb db, RevisionCache revisions,
        OctopushyOptions options, WorkerDeploymentRequest request, CancellationToken ct)
    {
        if (BlockedReason(options) is { } blocked) throw new GitException(blocked);
        var selection = request.Selection ?? throw new GitException("Select a configured host and worker specifications.");
        var settings = await db.RepositorySettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct);
        var revision = await db.Revisions.AsNoTracking().SingleOrDefaultAsync(r => r.Id == selection.RevisionId, ct);
        if (revision is null || !RepositoryTrust.IsCurrentSource(revision, settings)
            || settings?.HeadCommit != revision.Commit || !revision.RepositoryValid)
            throw new GitException("The source revision changed. Sync and reload the wizard.");
        var repository = await revisions.GetAsync(revision.Commit, ct);
        var installation = repository.Installations.SingleOrDefault(i => i.Address == selection.Installation);
        if (!repository.RepositoryValid || installation is null || !repository.IsValid(installation.Address)
            || selection.Host is null || !installation.Hosts.TryGetValue(selection.Host, out var host))
            throw new GitException("Select a valid installation and one of its configured hosts.");
        if (request.StateBackend is null || !repository.Settings!.StateBackends.ContainsKey(request.StateBackend))
            throw new GitException("Select a state backend configured in this repository.");
        var address = $"{installation.Address}/{selection.Name}";
        var existing = repository.Guests.SingleOrDefault(g => g.Address == address);
        var durable = await db.Resources.AsNoTracking().Include(r => r.ManagedWorker)
            .SingleOrDefaultAsync(r => r.Address == address && r.ManagedWorker != null, ct);
        if (durable is not null && existing?.ManagedWorker?.Generation != durable.ManagedWorker!.Generation)
            throw new GitException(durable.RetiredAt is not null
                ? "This name belongs to a retired worker generation. Choose a new name and unused VMID; historical identities are not reused."
                : "A managed worker already owns this resource. Restore its existing definition; replacement is disabled.");
        var generation = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{revision.SourceId}/worker-v1/{address}"))[..16]);
        if (existing is not null)
        {
            if (existing.ManagedWorker is not { } worker || worker.Generation != generation
                || existing.Host != selection.Host || existing.Vmid != selection.Vmid)
                throw new GitException("This identity already belongs to a different guest. The wizard will not adopt or replace it.");
            // Existing identity is a status/resume target, never overwritten by a new draft.
            return MakeReview(address, generation, settings!.Branch, revision, new Dictionary<string, string>(), true);
        }
        if (ManagedWorkerPreparation.Validate(repository, installation, host, selection) is { } error)
            throw new GitException(error);

        var files = TemplateFiles();
        var network = new JsonObject { ["bridge"] = selection.Bridge, ["mode"] = "dhcp" };
        if (selection.Vlan is { } vlan) network["vlan"] = vlan;
        var guest = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["type"] = "vm",
            ["host"] = host.Id,
            ["vmid"] = selection.Vmid,
            ["template"] = "octopushy-worker-v1",
            ["hardware"] = new JsonObject
            {
                ["sockets"] = selection.Sockets,
                ["cores"] = selection.Cores,
                ["memory"] = selection.MemoryMiB,
                ["disks"] = new JsonArray(
                    new JsonObject { ["size"] = selection.BootDiskGiB, ["datastore"] = selection.Datastore },
                    new JsonObject { ["size"] = selection.StateDiskGiB, ["datastore"] = selection.Datastore }),
            },
            ["network"] = network,
            ["provision"] = new JsonObject
            {
                ["recipe"] = "octopushy-worker-v1",
                ["state"] = new JsonObject { ["backend"] = request.StateBackend },
            },
            ["policy"] = new JsonObject { ["approval"] = "manual", ["destructive"] = "manual" },
            // Enable only for the exact image the operator qualified for native-v1 reporting.
            ["setup"] = new JsonObject
            {
                ["diskReporter"] = new JsonObject
                {
                    ["enabled"] = options.DiskReporter.Enabled
                    && options.DiskReporter.SupportsManagedNixosImage(options.WorkerDeployment.ImageSha256)
                },
            },
            ["worker"] = new JsonObject
            {
                ["templateVersion"] = 1,
                ["generation"] = generation.ToString(),
                // Dedicated admission label: remote workload endpoints must be qualified before
                // operators route existing workloads (e.g. a loopback PG backend) to this worker.
                ["labels"] = new JsonArray($"worker-{generation:N}"),
                ["maxConcurrency"] = 1,
            },
        };
        files[$"labs/{installation.Lab}/proxmox/{installation.Id}/guests/{selection.Name}/guest.yaml"] = guest.ToJsonString(Pretty) + "\n";
        files["templates/octopushy-worker-v1/template.yaml"] = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["os"] = "nixos",
            ["description"] = "Octopushy managed worker v1; immutable image with guest-local identity.",
            ["source"] = new JsonObject
            {
                ["url"] = options.WorkerDeployment.ImageUrl,
                ["checksum"] = "sha256:" + options.WorkerDeployment.ImageSha256!.ToLowerInvariant(),
                ["fileName"] = "octopushy-worker-v1.qcow2",
            },
        }.ToJsonString(Pretty) + "\n";
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(repository.Root, path);
            if (File.Exists(full) && await File.ReadAllTextAsync(full, ct) != content)
                throw new GitException($"The versioned template conflicts with '{path}'. Review a template migration; the wizard will not overwrite it.");
        }
        return MakeReview(address, generation, settings!.Branch, revision, files, false);
    }

    private static WorkerReviewDto MakeReview(string address, Guid generation, string branch,
        DefinitionRevision revision, IReadOnlyDictionary<string, string> files, bool exists)
    {
        var digest = Canonical.Hash(JsonSerializer.SerializeToNode(new { revision.SourceId, revision.Commit, branch, address, generation, files, exists }));
        return new(address, generation, branch, revision.Commit, digest, files, exists);
    }

    internal static Dictionary<string, string> TemplateFiles()
    {
        var assembly = typeof(WorkerRepositoryDeployment).Assembly;
        return assembly.GetManifestResourceNames().Where(n => n.StartsWith("WorkerTemplate/", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToDictionary(n => n["WorkerTemplate/".Length..].Replace('\\', '/'),
                n => { using var stream = assembly.GetManifestResourceStream(n)!; using var reader = new StreamReader(stream); return reader.ReadToEnd(); },
                StringComparer.Ordinal);
    }
}
