using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Inventory;

/// <summary>
/// The store values a guest's setup takes at whatever version is active, and the versions its
/// onChange action was last started with. A different active version is a change to deliver, so
/// it starts that action once, as a commit would. Nothing here reads a value.
/// </summary>
public static class FollowedSecrets
{
    /// <summary>
    /// The version a run would take now, or null when it would take none: the key is not set, was
    /// deleted, or its active version is revoked. None of those is something to deliver, so a
    /// key in that state never starts a run.
    /// </summary>
    public static async Task<Dictionary<string, int?>> UsableAsync(OctopushyDb db, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        var found = keys.Count == 0 ? [] : await db.StoreEntries.AsNoTracking()
            .Where(e => keys.Contains(e.Key) && e.CurrentVersion > 0)
            .Select(e => new
            {
                e.Key,
                e.CurrentVersion,
                Revoked = db.StoreVersions.Any(v => v.Key == e.Key && v.Version == e.CurrentVersion && v.RevokedAt != null),
            })
            .ToListAsync(cancellationToken);
        var usable = keys.ToDictionary(k => k, _ => (int?)null, StringComparer.Ordinal);
        foreach (var entry in found.Where(e => !e.Revoked))
            usable[entry.Key] = entry.CurrentVersion;
        return usable;
    }

    public static Dictionary<string, int?> Parse(string? json) =>
        json is null ? new(StringComparer.Ordinal)
            : new(JsonSerializer.Deserialize<Dictionary<string, int?>>(json) ?? [], StringComparer.Ordinal);

    public static string Serialize(IReadOnlyDictionary<string, int?> versions) =>
        JsonSerializer.Serialize(versions.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value));

    /// <summary>
    /// The keys whose usable version differs from the one recorded. A key that has no usable
    /// version now is not a change: the run would only block, and a revoked or deleted value
    /// waits for a person to set the next one.
    /// </summary>
    public static IReadOnlyList<Change> Changes(IReadOnlyDictionary<string, int?> recorded, IReadOnlyDictionary<string, int?> usable) =>
        usable.Where(p => p.Value is not null && recorded.GetValueOrDefault(p.Key) != p.Value)
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new Change(p.Key, recorded.GetValueOrDefault(p.Key), p.Value!.Value))
            .ToList();

    /// <summary>
    /// What to record when the onChange action starts: every followed key, at its usable version
    /// or null. A key that has none keeps the version recorded before, so that a revoked value
    /// that is then promoted again is not mistaken for a new one.
    /// </summary>
    public static Dictionary<string, int?> Started(IReadOnlyDictionary<string, int?> recorded, IReadOnlyDictionary<string, int?> usable) =>
        usable.ToDictionary(p => p.Key, p => p.Value ?? recorded.GetValueOrDefault(p.Key), StringComparer.Ordinal);

    /// <summary>Records that the guest's onChange action starts now, with the versions active at this moment.</summary>
    public static async Task RecordAsync(OctopushyDb db, GuestConfiguration configuration, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        var usable = await UsableAsync(db, keys, cancellationToken);
        configuration.SecretVersions = Serialize(Started(Parse(configuration.SecretVersions), usable));
    }

    /// <summary>
    /// The versions the newest run of an action pinned, by store key, or null if it has not
    /// pinned yet. A run pins at its first dispatch, which can be after a promotion that came
    /// moments after it was created, and that run then delivered the new version already.
    /// Only bindings that follow the active version count: one pinned to a version in Git says
    /// nothing about what a following binding of the same key received.
    /// </summary>
    public static async Task<Dictionary<string, int>?> PinnedByNewestAsync(OctopushyDb db, Guid resourceId, string action, CancellationToken cancellationToken)
    {
        var contexts = await db.Runs.AsNoTracking()
            .Where(r => r.ResourceId == resourceId && r.Operation == Operations.Configure)
            .OrderByDescending(r => r.CreatedAt).Take(50).Select(r => r.Context)
            .ToListAsync(cancellationToken);
        foreach (var json in contexts)
        {
            var context = RunContext.Parse(json);
            if (context.Ansible?.Action != action)
                continue;
            if (!context.SecretBindingsPinned)
                return null;
            return context.SecretVersions
                .Where(p => context.Secrets.ContainsKey(p.Key) && !context.RequestedSecretVersions.ContainsKey(p.Key))
                .GroupBy(p => context.Secrets[p.Key], StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Max(p => p.Value), StringComparer.Ordinal);
        }

        return null;
    }

    /// <summary>What a run started by these changes says about them.</summary>
    public static string Describe(IReadOnlyList<Change> changes) =>
        "A store value this guest's setup uses changed: "
        + string.Join(", ", changes.Select(c => c.From is { } from ? $"{c.Key} v{from} to v{c.To}" : $"{c.Key} set, v{c.To}"))
        + ".";

    public sealed record Change(string Key, int? From, int To);
}
