using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
using Octopushy.Server.Git;

namespace Octopushy.Server.Orchestration;

public static class ManagedWorkerSafety
{
    public static string PublicUrl(string? value)
    {
        const string reason = "Managed workers require an explicit guest-reachable HTTPS AgentChannel.PublicUrl (not loopback).";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new RunException(reason);
        var host = uri.Host.Trim('[', ']').TrimEnd('.');
        if (IPAddress.TryParse(host, out var address))
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                throw new RunException(reason);
        }
        if (uri.IsLoopback || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            throw new RunException(reason);
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public static bool DestructivePlan(JsonNode? outputs) =>
        (outputs?["counts"]?["delete"]?.GetValue<int>() ?? 0) > 0
        || (outputs?["counts"]?["replace"]?.GetValue<int>() ?? 0) > 0
        || outputs?["changes"] is JsonArray changes && changes.Any(change =>
            change?["actions"] is JsonArray actions && actions.Any(action => action?.ToString() == "delete"));

    public static bool UnchangedPlan(JsonNode? outputs) =>
        outputs is JsonObject plan
        && plan["hasChanges"] is JsonValue changed && changed.TryGetValue<bool>(out var hasChanges) && !hasChanges
        && plan["changes"] is JsonArray { Count: 0 }
        && plan["counts"] is JsonObject counts && new[] { "create", "update", "delete", "replace" }
            .All(key => counts[key] is JsonValue value && value.TryGetValue<int>(out var count) && count == 0);

    /// <summary>The read-only check that a worker removed by hand is gone; never a removal that destroys it.</summary>
    public static bool IsRetirementCheck(Run? run) =>
        run is not null && run.Operation == Operations.Retire && run.Steps.Count == 1
        && run.Steps[0].Name == StepNames.VerifyAbsent
        && run.Steps[0].Attempts.All(a => !a.Mutating)
        && RunContext.Parse(run.Context).DefinitionRemovedIn is null;

    /// <summary>A retire run that destroys a managed worker whose folder was deleted.</summary>
    public static bool IsWorkerRemoval(Run? run, RunContext context) =>
        run is not null && run.Operation == Operations.Retire && context.DefinitionRemovedIn is not null && context.ManagedWorker is not null;

    public static bool Matches(ManagedWorkerBinding binding, Run run, RunContext context) =>
        (run.Operation == Operations.Provision || IsRetirementCheck(run) || IsWorkerRemoval(run, context))
        && context.ManagedWorker?.Generation == binding.Generation
        && context.Proxmox.Node == binding.Host && context.Proxmox.Vmid == binding.Vmid
        && context.HostKey == $"{binding.Installation}/{binding.Host}";

    public static async Task<bool> HasUnfinishedUpdateAsync(OctopushyDb db, Guid agentId, CancellationToken ct)
    {
        var resources = db.ManagedWorkers.Where(w => w.AgentId == agentId).Select(w => w.ResourceId);
        var runs = await db.Runs.AsNoTracking().Where(r => resources.Contains(r.ResourceId)
            && r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled).ToListAsync(ct);
        return runs.Any(r => RunContext.Parse(r.Context).WorkerUpdate?.AgentId == agentId);
    }

    public static async Task<string?> RetirementBlockedReasonAsync(OctopushyDb db, Resource resource, Run? run, CancellationToken ct)
    {
        var binding = resource.ManagedWorker;
        if (binding is null || resource.RetiredAt is not null)
            return "There is no active managed worker to retire.";
        if (resource.MissingSince is null)
            return "Remove the worker's Git definition first. Revoking an enrollment does not delete or retire its VM.";
        if (binding.AgentId is { } agentId && await db.Agents.AnyAsync(a => a.Id == agentId, ct))
            return "Remove the worker's agent enrollment first; disabling it alone does not revoke its identity.";
        if (await db.Runs.AnyAsync(r => r.ResourceId == resource.Id && (run == null || r.Id != run.Id)
            && r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled, ct))
            return "The worker has an unfinished run. Resolve it before verifying retirement.";
        var resourceRuns = db.Runs.Where(r => r.ResourceId == resource.Id && (run == null || r.Id != run.Id)).Select(r => r.Id);
        if (await db.Attempts.AnyAsync(a => (a.RunId != null && resourceRuns.Contains(a.RunId.Value) || binding.AgentId != null && a.AgentId == binding.AgentId)
            && (a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running
                || a.Mutating && a.Status == AttemptStatuses.Uncertain && a.Step!.Status == StepStatuses.Uncertain), ct))
            return "The worker has active work or unresolved mutating outcomes. Drain and inspect it first.";
        var settings = await db.RepositorySettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct);
        var head = await RepositoryTrust.EligibleRevisions(db).AsNoTracking()
            .Where(r => settings != null && r.Commit == settings.HeadCommit).OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(ct);
        if (head is null)
            return "Trust the exact current-source head before verifying retirement.";
        var original = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == binding.RunId, ct);
        var source = await db.Revisions.AsNoTracking().SingleOrDefaultAsync(r => r.Id == binding.RevisionId, ct);
        if (original is null || source?.SourceId != head.SourceId)
            return "Restore the worker's original repository source before verifying retirement.";
        if (run is not null && (run.RevisionId != head.Id || run.Commit != head.Commit
            || RunContext.Parse(run.Context).Proxmox.Endpoint != RunContext.Parse(original.Context).Proxmox.Endpoint))
            return "The source head or Proxmox endpoint changed. Verify retirement against the original installation again.";
        return null;
    }

    /// <summary>Checks work on <paramref name="resourceId"/>: a step of <paramref name="run"/>, or an observation when it is null.</summary>
    public static async Task ValidateDispatchAsync(OctopushyDb db, Guid resourceId, Run? run, RunContext context, Guid agentId, CancellationToken ct)
    {
        if (await db.Agents.AnyAsync(a => a.Id == agentId && (a.Disabled || a.Draining), ct))
            throw new AssignmentException("This agent is disabled or draining and cannot receive new assignments.");
        var own = await db.ManagedWorkers.AsNoTracking().SingleOrDefaultAsync(w => w.AgentId == agentId, ct);
        if (IsRetirementCheck(run) && own is not null)
            throw new AssignmentException("Only an independent control worker may verify managed-worker retirement.");
        if (own is not null && context.AgentLabels.Count == 0)
            throw new AssignmentException("Managed workers require explicit matching workload labels; unlabeled work stays on independent control workers.");
        if (own is not null && (own.ReadyAt is null || resourceId == own.ResourceId
            || context.Proxmox.Vmid == own.Vmid && context.Proxmox.Node == own.Host))
            throw new AssignmentException("A managed worker must be bootstrap-ready and must never operate on its own guest, including preview.");
        var bound = await db.ManagedWorkers.AsNoTracking().SingleOrDefaultAsync(w => w.ResourceId == resourceId, ct);
        if (bound is not null && (run is null || !Matches(bound, run, context)))
            throw new AssignmentException("Managed worker retirement, replacement, movement or role removal is not supported.");
        if (context.ManagedWorker is not null && bound is null)
            throw new AssignmentException("Managed worker run has no durable generation binding.");
        if (bound is not null && IsRetirementCheck(run))
        {
            var resource = await db.Resources.SingleAsync(r => r.Id == resourceId, ct);
            if (await RetirementBlockedReasonAsync(db, resource, run, ct) is { } reason)
                throw new AssignmentException(reason);
        }
        // A removal still destroys a worker whose enrollment was removed by hand.
        else if (bound?.AgentId is { } enrolledId && !IsWorkerRemoval(run, context) && !await db.Agents.AnyAsync(a => a.Id == enrolledId, ct))
            throw new AssignmentException("This worker's enrollment was removed. Its generation cannot be silently re-enrolled; verify retirement instead.");
        if (context.WorkerUpdate is { } update)
        {
            if (bound is null || bound.AgentId != update.AgentId || bound.ReadyAt is null
                || !await db.Agents.AnyAsync(a => a.Id == update.AgentId && a.Draining && !a.Disabled, ct))
                throw new AssignmentException("An OS update requires the original Ready worker to remain enrolled and draining.");
            if (await db.Attempts.AnyAsync(a => a.AgentId == update.AgentId
                && (a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running
                    || a.Mutating && a.Status == AttemptStatuses.Uncertain && a.Step!.Status == StepStatuses.Uncertain), ct))
                throw new AssignmentException("The target worker has active or uncertain work; no update credentials may be released.");
        }
    }
}
