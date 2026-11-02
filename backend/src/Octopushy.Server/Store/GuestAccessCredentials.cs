using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Utilities;
using Org.BouncyCastle.Security;

namespace Octopushy.Server.Store;

/// <summary>
/// The key Octopushy logs in to a guest with. Each guest has its own, so a leaked key reaches one
/// machine. The private half is a versioned entry under system/, which no definition can name;
/// it leaves only through <see cref="AssignmentBuilder"/>, to a playbook step. The
/// public half is recorded per version in <see cref="GuestAccessKey"/>, which is also what says
/// where each version stands on the guest, apart from the provisioning hash.
/// </summary>
public static class GuestAccessCredentials
{
    public static string Key(Guid resourceId) => $"system/guest-access/{resourceId}";

    /// <summary>The version OpenTofu carries, generating the first the first time it is needed.</summary>
    public static async Task<GuestAccessKey> EnsureAsync(
        OctopushyDb db, VariableStore store, Guid resourceId, string guestName, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await db.GuestAccessKeys.FirstOrDefaultAsync(k => k.ResourceId == resourceId && k.Bootstrap, cancellationToken);
        if (existing is not null)
            return existing;

        var generated = Generate($"octopushy@{guestName}");
        var key = new GuestAccessKey
        {
            ResourceId = resourceId,
            Version = 1,
            PublicKey = generated.PublicKey,
            Fingerprint = generated.Fingerprint,
            Bootstrap = true,
            CreatedAt = now,
        };
        db.GuestAccessKeys.Add(key);
        // Same DbContext: the public half and the encrypted private half commit in one SaveChanges.
        await store.SetInternalAsync(Key(resourceId), generated.PrivateKey, Visibilities.Secret, "Octopushy's key for this guest", cancellationToken);
        return key;
    }

    /// <summary>
    /// The candidate a rotation works with. One left by an earlier rotation that stopped is used
    /// again: its key may already be on the guest, and two candidates would only add to what has to
    /// be cleaned up.
    /// </summary>
    public static async Task<GuestAccessKey> StageAsync(
        OctopushyDb db, VariableStore store, Guid resourceId, string guestName, int current, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var entry = Key(resourceId);
        var waiting = await db.GuestAccessKeys.Where(k => k.ResourceId == resourceId && k.Version > current && k.RemovedAt == null)
            .OrderByDescending(k => k.Version).FirstOrDefaultAsync(cancellationToken);
        if (waiting is not null
            && await db.StoreVersions.AnyAsync(v => v.Key == entry && v.Version == waiting.Version && v.RevokedAt == null, cancellationToken))
            return waiting;

        var generated = Generate($"octopushy@{guestName}");
        var version = await store.StageInternalAsync(entry, generated.PrivateKey, cancellationToken);
        var key = new GuestAccessKey
        {
            ResourceId = resourceId,
            Version = version,
            PublicKey = generated.PublicKey,
            Fingerprint = generated.Fingerprint,
            CreatedAt = now,
        };
        db.GuestAccessKeys.Add(key);
        await db.SaveChangesAsync(cancellationToken);
        return key;
    }

    /// <summary>Makes a verified candidate the version runs use. One guest's key, and no other's.</summary>
    public static async Task PromoteAsync(
        OctopushyDb db, Guid resourceId, AccessRotationContext rotation, string actor, string evidence, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var name = Key(resourceId);
        var entry = await db.StoreEntries.SingleAsync(e => e.Key == name, cancellationToken);
        var version = await db.StoreVersions.SingleAsync(v => v.Key == name && v.Version == rotation.Candidate, cancellationToken);
        var key = await db.GuestAccessKeys.SingleAsync(k => k.ResourceId == resourceId && k.Version == rotation.Candidate, cancellationToken);
        if (entry.CurrentVersion != rotation.Current || version.RevokedAt is not null || key.VerifiedAt is null)
            throw new RunException("The key in use changed, or the candidate has no verified login. Start the rotation again.");
        VariableStore.Activate(entry, version, now, actor, evidence);
        key.PromotedAt = now;
        key.PromotedBy = actor;
        key.PromotionEvidence = evidence;
    }

    /// <summary>The removed versions cannot be released again. A login has shown they are gone.</summary>
    public static async Task RevokeRemovedAsync(OctopushyDb db, Guid resourceId, AccessRotationContext rotation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var name = Key(resourceId);
        var entry = await db.StoreEntries.SingleAsync(e => e.Key == name, cancellationToken);
        var versions = await db.StoreVersions.Where(v => v.Key == name && rotation.Remove.Contains(v.Version) && v.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var version in versions)
        {
            VariableStore.Revoke(entry, version, now, "octopushy", "Removed from the guest by a rotation, and verified gone.");
        }
    }

    /// <summary>Whether what OpenTofu carries is older than the key in use.</summary>
    public static async Task<bool> BootstrapIsBehindAsync(OctopushyDb db, Guid resourceId, CancellationToken cancellationToken)
    {
        var name = Key(resourceId);
        var current = await db.StoreEntries.AsNoTracking().Where(e => e.Key == name).Select(e => (int?)e.CurrentVersion).FirstOrDefaultAsync(cancellationToken);
        return current is not null && await db.GuestAccessKeys.AnyAsync(k => k.ResourceId == resourceId && k.Bootstrap && k.Version != current, cancellationToken);
    }

    /// <summary>
    /// A new machine has to start with the key in use, not the one its predecessor started with.
    /// True if that changed what OpenTofu carries.
    /// </summary>
    public static async Task<bool> RebaseBootstrapAsync(OctopushyDb db, Guid resourceId, CancellationToken cancellationToken)
    {
        var name = Key(resourceId);
        var current = await db.StoreEntries.Where(e => e.Key == name).Select(e => (int?)e.CurrentVersion).FirstOrDefaultAsync(cancellationToken);
        var keys = await db.GuestAccessKeys.Where(k => k.ResourceId == resourceId).ToListAsync(cancellationToken);
        if (current is null || keys.Find(k => k.Version == current) is not { Bootstrap: false } target)
            return false;
        foreach (var key in keys)
        {
            key.Bootstrap = false;
        }

        target.Bootstrap = true;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>What Octopushy's own key can no longer be needed for: the guest is gone.</summary>
    public static async Task RemoveAsync(OctopushyDb db, Guid resourceId, CancellationToken cancellationToken)
    {
        // What each version was released to stays on record in SecretResolutions.
        var key = Key(resourceId);
        await db.StoreVersions.Where(v => v.Key == key).ExecuteDeleteAsync(cancellationToken);
        await db.StoreEntries.Where(e => e.Key == key).ExecuteDeleteAsync(cancellationToken);
        await db.GuestAccessKeys.Where(k => k.ResourceId == resourceId).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>A new Ed25519 key: the private half in OpenSSH format, unencrypted, as ssh-add takes it.</summary>
    public static (string PrivateKey, string PublicKey, string Fingerprint) Generate(string comment)
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var privateBlob = OpenSshPrivateKeyUtilities.EncodePrivateKey((Ed25519PrivateKeyParameters)pair.Private);
        var publicBlob = OpenSshPublicKeyUtilities.EncodePublicKey((Ed25519PublicKeyParameters)pair.Public);
        var pem = new StringBuilder("-----BEGIN OPENSSH PRIVATE KEY-----\n");
        foreach (var line in Convert.ToBase64String(privateBlob).Chunk(70))
        {
            pem.Append(line).Append('\n');
        }

        pem.Append("-----END OPENSSH PRIVATE KEY-----\n");
        return (
            pem.ToString(),
            $"ssh-ed25519 {Convert.ToBase64String(publicBlob)} {comment}",
            $"SHA256:{Convert.ToBase64String(SHA256.HashData(publicBlob)).TrimEnd('=')}");
    }
}
