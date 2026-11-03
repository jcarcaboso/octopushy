using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// What keeps Octopushy off machines it does not manage, apart from the refusal of plans in
/// <see cref="GuestVmPlan.ForeignRemoval"/>: installations it only observes (<c>manage: false</c>).
/// See docs/safety-boundary.md.
/// </summary>
public static class SafetyBoundary
{
    /// <summary>An installation that says <c>manage: false</c>, with what would let a run reach it.</summary>
    public sealed record ObservedOnlyInstallation(string Address, IReadOnlySet<string> StoreKeys, IReadOnlySet<string> Endpoints);

    /// <summary>Whether an installation's definition, as JSON, says <c>manage: false</c>.</summary>
    public static bool ObservedOnly(JsonNode? definition) =>
        definition?["manage"] is JsonValue manage && manage.TryGetValue<bool>(out var managed) && !managed;

    public static string ObservedOnlyReason(string installation) =>
        $"{installation} is observed only (manage: false): Octopushy runs nothing against it and releases its credentials only to observe it.";

    /// <summary>
    /// The installations that say <c>manage: false</c> in the latest revision synced, with the store keys
    /// of their Proxmox tokens and their endpoints, so that a run naming any of them is refused too. A CA
    /// certificate is no credential, and installations may share one, so it does not count.
    /// </summary>
    public static async Task<List<ObservedOnlyInstallation>> ObservedOnlyInstallationsAsync(OctopushyDb db, CancellationToken cancellationToken)
    {
        var installations = await db.Resources.AsNoTracking()
            .Where(r => r.Kind == ResourceKinds.Installation && r.RetiredAt == null && r.Definition != null)
            .Select(r => new { r.Address, r.Definition })
            .ToListAsync(cancellationToken);
        var result = new List<ObservedOnlyInstallation>();
        foreach (var installation in installations)
        {
            var definition = JsonNode.Parse(installation.Definition!);
            if (!ObservedOnly(definition))
                continue;
            var connection = definition!["connection"];
            var keys = new[] { "token", "readOnlyToken", "privilegedToken" }
                .Select(role => connection?[role]?["store"]?.ToString())
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
            var endpoints = (definition["hosts"] as JsonObject ?? []).Select(h => h.Value?["endpoint"]?.ToString())
                .Append(connection?["endpoint"]?.ToString())
                .OfType<string>()
                .Select(NormalEndpoint)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            result.Add(new ObservedOnlyInstallation(installation.Address, keys, endpoints));
        }

        return result;
    }

    /// <summary>
    /// Why this run may not exist or go on because it would work in an installation Octopushy only observes,
    /// or null. Its resource, its context's address and host, the store keys it names and the endpoint it
    /// reaches each count, so a run with a context that was altered after it was made is refused too.
    /// </summary>
    public static async Task<string?> ObservedOnlyRefusalAsync(OctopushyDb db, Run run, RunContext context, CancellationToken cancellationToken,
        IReadOnlyList<ObservedOnlyInstallation>? observedOnly = null)
    {
        observedOnly ??= await ObservedOnlyInstallationsAsync(db, cancellationToken);
        if (observedOnly.Count == 0)
            return null;
        var resource = await db.Resources.AsNoTracking().Where(r => r.Id == run.ResourceId).Select(r => r.Address).FirstOrDefaultAsync(cancellationToken);
        var endpoint = context.Proxmox.Endpoint is { } e ? NormalEndpoint(e) : null;
        foreach (var installation in observedOnly)
        {
            bool Under(string? address) => address is not null
                && (address == installation.Address || address.StartsWith(installation.Address + "/", StringComparison.Ordinal));
            if (Under(resource) || Under(context.Address) || Under(context.HostKey)
                || context.Secrets.Values.Any(installation.StoreKeys.Contains)
                || endpoint is not null && installation.Endpoints.Contains(endpoint))
                return ObservedOnlyReason(installation.Address);
        }

        return null;
    }

    private static string NormalEndpoint(string endpoint) => endpoint.Trim().TrimEnd('/');
}
