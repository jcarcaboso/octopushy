using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;

namespace Octopushy.Server.Store;

/// <summary>Reporter credentials remain stable across retries, encrypted in the existing store.</summary>
public static class GuestReporterCredentials
{
    public static string Key(Guid resourceId) => $"system/disk-reporter/{resourceId}";

    public static async Task<string> EnsureAsync(
        OctopushyDb db, VariableStore store, Guid resourceId, int intervalSeconds, bool rotate, CancellationToken ct)
    {
        var usage = await db.GuestDiskUsages.FirstOrDefaultAsync(d => d.ResourceId == resourceId, ct);
        if (usage is null)
        {
            usage = new GuestDiskUsage { ResourceId = resourceId };
            db.GuestDiskUsages.Add(usage);
        }
        var key = Key(resourceId);
        var stored = rotate ? null : await store.GetAsync(key, ct);
        var token = stored is { } credential && usage.TokenHash == EnrollmentService.TokenHash(credential.Value)
            ? credential.Value
            : Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        usage.IntervalSeconds = intervalSeconds;
        usage.TokenHash = EnrollmentService.TokenHash(token);
        if (stored?.Value != token)
        {
            // Same DbContext: the hash and encrypted token are committed in one SaveChanges.
            await store.SetInternalAsync(key, token, Visibilities.Secret, "Guest disk reporting credential", ct);
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }
        return token;
    }
}
