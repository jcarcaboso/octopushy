using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;

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

    public static bool Matches(ManagedWorkerBinding binding, Run run, RunContext context) =>
        run.Operation == Operations.Provision && context.ManagedWorker?.Generation == binding.Generation
        && context.Proxmox.Node == binding.Host && context.Proxmox.Vmid == binding.Vmid
        && context.HostKey == $"{binding.Installation}/{binding.Host}";

    /// <summary>Checks work on <paramref name="resourceId"/>: a step of <paramref name="run"/>, or an observation when it is null.</summary>
    public static async Task ValidateDispatchAsync(OctopushyDb db, Guid resourceId, Run? run, RunContext context, Guid agentId, CancellationToken ct)
    {
        var own = await db.ManagedWorkers.AsNoTracking().SingleOrDefaultAsync(w => w.AgentId == agentId, ct);
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
    }
}
