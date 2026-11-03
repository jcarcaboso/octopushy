using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;

namespace Octopushy.Server.Store;

/// <summary>
/// A guest's SSH host key, when it is Octopushy's. Octopushy generates it before the machine
/// exists, a provision run puts it in the guest's cloud-init snippet, and configure runs pin its
/// public half, so nobody reads a key off a console and a rebuild keeps the same identity. The
/// private half is a secret under system/, which no definition can name. It leaves only through
/// <see cref="Orchestration.AssignmentBuilder"/>, to a provision run's plan.
/// </summary>
public static class GuestHostCredentials
{
    public static string Key(Guid resourceId) => $"system/guest-host-key/{resourceId}";

    /// <summary>Whether Octopushy holds a host key for the guest. Once it does, the guest keeps it; see <see cref="Definitions.ManagedHostKey.Wanted"/>.</summary>
    public static Task<bool> IssuedAsync(OctopushyDb db, Guid resourceId, CancellationToken cancellationToken) =>
        db.GuestHostKeys.AnyAsync(k => k.ResourceId == resourceId, cancellationToken);

    /// <summary>The guest's key, generating it the first time a plan needs it.</summary>
    public static async Task<GuestHostKey> EnsureAsync(
        OctopushyDb db, VariableStore store, Guid resourceId, string guestName, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await db.GuestHostKeys.FirstOrDefaultAsync(k => k.ResourceId == resourceId, cancellationToken);
        if (existing is not null)
            return existing;

        var generated = GuestAccessCredentials.Generate($"root@{guestName}");
        var key = new GuestHostKey { ResourceId = resourceId, PublicKey = generated.PublicKey, Fingerprint = generated.Fingerprint, CreatedAt = now };
        db.GuestHostKeys.Add(key);
        // Same DbContext: the public half and the encrypted private half commit in one SaveChanges.
        await store.SetInternalAsync(Key(resourceId), generated.PrivateKey, Visibilities.Secret, "The guest's SSH host key, Octopushy's", cancellationToken);
        return key;
    }

    /// <summary>The guest is gone, and so is its identity. What each version was released to stays on record.</summary>
    public static async Task RemoveAsync(OctopushyDb db, Guid resourceId, CancellationToken cancellationToken)
    {
        var key = Key(resourceId);
        await db.StoreVersions.Where(v => v.Key == key).ExecuteDeleteAsync(cancellationToken);
        await db.StoreEntries.Where(e => e.Key == key).ExecuteDeleteAsync(cancellationToken);
        await db.GuestHostKeys.Where(k => k.ResourceId == resourceId).ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>
/// The key Octopushy uploads cloud-init snippets with, one per installation, installed on each of
/// its hosts by <c>deploy/proxmox/snippets.sh</c>. Octopushy generates it so that no person copies
/// a private key: what leaves Octopushy is the public half, for that script. The account it opens
/// can only write snippets, over SFTP, in a chroot, so a leaked key writes snippets and nothing else.
/// </summary>
public static class InstallationSnippetCredentials
{
    public static string Key(string installation) => $"system/installation-snippets/{installation}";

    public static Task<InstallationSnippetKey?> FindAsync(OctopushyDb db, string installation, CancellationToken cancellationToken) =>
        db.InstallationSnippetKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Installation == installation, cancellationToken);

    /// <summary>The installation's key, generating it when a person first asks for it.</summary>
    public static async Task<InstallationSnippetKey> EnsureAsync(
        OctopushyDb db, VariableStore store, string installation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await db.InstallationSnippetKeys.FirstOrDefaultAsync(k => k.Installation == installation, cancellationToken);
        if (existing is not null)
            return existing;

        var generated = GuestAccessCredentials.Generate($"octopushy-snippets@{installation}");
        var key = new InstallationSnippetKey { Installation = installation, PublicKey = generated.PublicKey, Fingerprint = generated.Fingerprint, CreatedAt = now };
        db.InstallationSnippetKeys.Add(key);
        await store.SetInternalAsync(Key(installation), generated.PrivateKey, Visibilities.Secret, "Octopushy's key for snippet uploads to this installation", cancellationToken);
        return key;
    }
}
