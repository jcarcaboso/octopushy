using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkerUpdateRequest(Guid Generation, string ArchiveSha256);
public sealed record WorkerUpdateView(Guid Generation, string ArchiveUrl, string ArchiveSha256, string SystemPath, string ImageSha256);

public static class ManagedWorkerUpdates
{
    public static WorkerUpdateContext Target(WorkerUpdateOptions options, Guid agentId)
    {
        if (!options.Enabled || !Uri.TryCreate(options.ArchiveUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || options.ArchiveUrl.Length > 2048
            || !Hex(options.ArchiveSha256) || !Hex(options.ImageSha256)
            || options.SystemPath is null || !Regex.IsMatch(options.SystemPath, @"^/nix/store/[0-9abcdfghijklmnpqrsvwxyz]{32}-nixos-system-[A-Za-z0-9._+-]{1,200}\z")
            || options.CaCertificatePem?.Length > 16384 || options.CaCertificatePem?.Contains("PRIVATE KEY", StringComparison.Ordinal) == true)
            throw new RunException("Publish and qualify a worker OS closure, then configure WorkerUpdates with its HTTPS archive URL, exact archive/image SHA-256 and NixOS system path.");
        ManagedWorkerSafety.PublicUrl(uri.GetLeftPart(UriPartial.Authority));
        return new(agentId, options.ArchiveUrl, options.ArchiveSha256!.ToLowerInvariant(), options.SystemPath,
            options.ImageSha256!.ToLowerInvariant(), options.CaCertificatePem);
    }

    private static bool Hex(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    public static WorkerUpdateView View(Guid generation, WorkerUpdateContext target) =>
        new(generation, target.ArchiveUrl, target.ArchiveSha256, target.SystemPath, target.ImageSha256);

    public static void MapWorkerUpdates(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/managed-worker-preparation").WithTags("Managed workers");
        group.MapGet("/{id:guid}/update", async Task<Results<Ok<WorkerUpdateView>, NotFound, ProblemHttpResult>> (
            Guid id, OctopushyDb db, IOptions<OctopushyOptions> options, CancellationToken ct) =>
        {
            var resource = await db.Resources.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
            if (resource?.ManagedWorker is not { AgentId: { } agentId } binding) return TypedResults.NotFound();
            try { return TypedResults.Ok(View(binding.Generation, Target(options.Value.WorkerUpdates, agentId))); }
            catch (RunException e) { return TypedResults.Problem(statusCode: 409, title: e.Message); }
        }).WithName("reviewManagedWorkerUpdate");

        group.MapPost("/{id:guid}/update", async Task<Results<Ok<StartedRun>, NotFound, ProblemHttpResult>> (
            Guid id, WorkerUpdateRequest request, HttpContext http, RunEngine engine, IOptions<OctopushyOptions> options, CancellationToken ct) =>
        {
            try
            {
                var result = await engine.ExecuteAsync<StartedRun?>(async (db, services) =>
                {
                    var resource = await db.Resources.SingleOrDefaultAsync(r => r.Id == id && r.Kind == ResourceKinds.Guest, ct);
                    if (resource?.ManagedWorker is not { AgentId: { } agentId, ReadyAt: not null } binding) return null;
                    if (resource.RetiredAt is not null || resource.MissingSince is not null || !resource.Valid || binding.Generation != request.Generation)
                        throw new RunException("Reload the active, valid worker generation before requesting an OS update.");
                    var target = Target(options.Value.WorkerUpdates, agentId);
                    if (!string.Equals(target.ArchiveSha256, request.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                        throw new RunException("The offered release changed; review its current archive fingerprint.");
                    var existing = await db.Runs.Where(r => r.ResourceId == id && r.Status != RunStatuses.Succeeded
                        && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled).OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
                    if (existing is not null)
                    {
                        if (RunContext.Parse(existing.Context).WorkerUpdate == target) return new(existing.Id);
                        throw new RunException("Resolve the worker's unfinished run before updating its OS.");
                    }
                    var agent = await db.Agents.SingleOrDefaultAsync(a => a.Id == agentId, ct);
                    if (agent is null || agent.Disabled) throw new RunException("The enrolled worker must not be disabled or removed. Drain it instead.");
                    if (await db.Attempts.AnyAsync(a => a.AgentId == agentId && (a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running
                        || a.Mutating && a.Status == AttemptStatuses.Uncertain && a.Step!.Status == StepStatuses.Uncertain), ct))
                        throw new RunException("The worker has active work or an unresolved mutation. Drain and inspect it before updating.");
                    var settings = await db.RepositorySettings.SingleAsync(s => s.Id == 1, ct);
                    var revision = await RepositoryTrust.EligibleRevisions(db).Where(r => r.Commit == settings.HeadCommit).OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(ct)
                        ?? throw new RunException("Sync the current repository source first; its head must be loaded and valid.");
                    var original = await db.Runs.SingleAsync(r => r.Id == binding.RunId, ct);
                    var originalRevision = await db.Revisions.SingleAsync(r => r.Id == original.RevisionId, ct);
                    if (revision.SourceId != originalRevision.SourceId) throw new RunException("This worker is bound to a different repository-source generation.");
                    var repository = await services.GetRequiredService<RevisionCache>().GetAsync(revision.Commit, ct);
                    var guest = repository.Guest(resource.Address) ?? throw new RunException("The current worker definition is missing.");
                    var run = services.GetRequiredService<RunFactory>().UpdateWorker(repository, guest, resource, revision,
                        $"OS update requested by {http.User.Identity!.Name}; closure SHA-256 {target.ArchiveSha256}", target);
                    if (RunContext.Parse(run.Context).Proxmox.Endpoint != RunContext.Parse(original.Context).Proxmox.Endpoint)
                        throw new RunException("The original worker's Proxmox endpoint changed; inspect rather than updating another endpoint.");
                    agent.Draining = true;
                    await engine.AddRunsAsync(db, [run], ct);
                    return new(run.Id);
                }, ct);
                return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
            }
            catch (RunException e) { return TypedResults.Problem(statusCode: 409, title: e.Message); }
        }).WithName("updateManagedWorkerOs");
    }
}
