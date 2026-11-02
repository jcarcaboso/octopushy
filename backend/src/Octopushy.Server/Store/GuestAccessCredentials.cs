using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Utilities;
using Org.BouncyCastle.Security;

namespace Octopushy.Server.Store;

/// <summary>
/// The key Octopushy logs in to a guest with. Each guest has its own, so a leaked key reaches one
/// machine. The private half is a versioned entry under system/, which no definition can name;
/// it leaves only through <see cref="Orchestration.AssignmentBuilder"/>, to a playbook step. The
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
