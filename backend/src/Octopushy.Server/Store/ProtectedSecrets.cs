using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;

namespace Octopushy.Server.Store;

/// <summary>
/// Who may receive a protected store entry. The flag and the grants live in the database, so no
/// commit can add, change, or remove them: not by editing a definition, and not by deleting the
/// definition that used the key. This answers for every way a definition can bind a key, because
/// it looks at the key, and the store asks it before any release.
/// </summary>
public static class ProtectedSecrets
{
    /// <summary>
    /// Why these keys may not go to this resource running this code, or null. Unprotected keys
    /// are not its concern.
    /// </summary>
    public static async Task<string?> WithheldAsync(
        OctopushyDb db, IEnumerable<string> keys, Guid? resourceId, string? codeDigest, string address, CancellationToken ct)
    {
        var wanted = keys.Distinct(StringComparer.Ordinal).ToList();
        var guarded = await db.StoreEntries.AsNoTracking().Where(e => e.Protected && wanted.Contains(e.Key)).Select(e => e.Key).ToListAsync(ct);
        foreach (var key in guarded.Order(StringComparer.Ordinal))
        {
            var grant = resourceId is null ? null
                : await db.SecretGrants.AsNoTracking().FirstOrDefaultAsync(g => g.Key == key && g.ResourceId == resourceId, ct);
            if (grant is null)
                return $"Waiting for trust: {address} has no grant for the protected secret {key}. "
                    + $"An operator has to review its code{Shown(codeDigest)}; a cluster's page does this for it and its members.";
            if (grant.CodeDigest != codeDigest)
                return $"Waiting for trust: the code of {address} changed since it was granted the protected secret {key}. "
                    + $"Review it{Shown(codeDigest)}, then trust it again.";
        }

        return null;
    }

    public static Task<bool> AnyAsync(OctopushyDb db, IEnumerable<string> keys, CancellationToken ct)
    {
        var wanted = keys.Distinct(StringComparer.Ordinal).ToList();
        return db.StoreEntries.AsNoTracking().AnyAsync(e => e.Protected && wanted.Contains(e.Key), ct);
    }

    /// <summary>Records that this resource, with this code, may receive this key. The caller saves.</summary>
    public static async Task GrantAsync(OctopushyDb db, string key, Guid resourceId, string? codeDigest, string actor, DateTimeOffset now, CancellationToken ct)
    {
        var grant = await db.SecretGrants.FirstOrDefaultAsync(g => g.Key == key && g.ResourceId == resourceId, ct);
        if (grant is null)
        {
            grant = new SecretGrant { Key = key, ResourceId = resourceId, GrantedBy = actor };
            db.SecretGrants.Add(grant);
        }

        grant.CodeDigest = codeDigest;
        grant.GrantedBy = actor;
        grant.GrantedAt = now;
    }

    private static string Shown(string? digest) => digest is null ? "" : $" (digest {digest[..Math.Min(12, digest.Length)]})";
}
