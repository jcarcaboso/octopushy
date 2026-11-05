using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;

namespace Octopushy.Server.Store;

/// <summary>
/// A guest's SSH host key, when it is Octopushy's. Octopushy generates it before the machine exists, a provision run
/// puts it in the guest's cloud-init snippet, and configure runs pin its public half, so nobody reads a key off a
/// console. The private half is a versioned secret under system/, which no definition can name. It leaves only
/// through <see cref="Orchestration.AssignmentBuilder"/>, to a provision run's plan.
/// <para>
/// Two versions matter. The store's current version is the one a plan carries. The pin is the newest version an apply
/// delivered: configure runs connect to it and to nothing else. A new machine gets a new version, and its pin moves
/// only when the apply that made the machine finishes. See docs/guest-configuration.md.
/// </para>
/// </summary>
public static class GuestHostCredentials
{
    public static string Key(Guid resourceId) => $"system/guest-host-key/{resourceId}";

    /// <summary>Whether Octopushy holds a host key for the guest. Once it does, the guest keeps it; see <see cref="Definitions.ManagedHostKey.Wanted"/>.</summary>
    public static Task<bool> IssuedAsync(OctopushyDb db, Guid resourceId, CancellationToken cancellationToken) =>
        db.GuestHostKeys.AnyAsync(k => k.ResourceId == resourceId, cancellationToken);

    /// <summary>The version a plan carries, generating the first the first time a run needs it.</summary>
    public static async Task<GuestHostKey> EnsureAsync(
        OctopushyDb db, VariableStore store, Guid resourceId, string guestName, DateTimeOffset now, CancellationToken cancellationToken) =>
        await CarriedAsync(db, resourceId, cancellationToken) ?? await AddVersionAsync(db, store, resourceId, guestName, now, cancellationToken);

    /// <summary>The version a plan carries now: the store's current one. Null before the first.</summary>
    public static async Task<GuestHostKey?> CarriedAsync(OctopushyDb db, Guid resourceId, CancellationToken cancellationToken)
    {
        var name = Key(resourceId);
        var current = await db.StoreEntries.Where(e => e.Key == name).Select(e => (int?)e.CurrentVersion).FirstOrDefaultAsync(cancellationToken);
        return current is null ? null : await db.GuestHostKeys.FirstOrDefaultAsync(k => k.ResourceId == resourceId && k.Version == current, cancellationToken);
    }

    /// <summary>The pin: the newest version an apply delivered. Null while no apply has delivered one.</summary>
    public static Task<GuestHostKey?> PinnedAsync(OctopushyDb db, Guid resourceId, CancellationToken cancellationToken) =>
        db.GuestHostKeys.AsNoTracking().Where(k => k.ResourceId == resourceId && k.DeliveredAt != null)
            .OrderByDescending(k => k.Version).FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// A new version, which plans carry from now on: a new machine must not start with a key an earlier machine held,
    /// on its disks, in its cloud-init drive, or wherever that machine's data went.
    /// </summary>
    public static Task<GuestHostKey> RenewAsync(
        OctopushyDb db, VariableStore store, Guid resourceId, string guestName, DateTimeOffset now, CancellationToken cancellationToken) =>
        AddVersionAsync(db, store, resourceId, guestName, now, cancellationToken);

    /// <summary>Plans carry <paramref name="version"/> again: a plan that keeps the machine must not change the key it serves.</summary>
    public static async Task RebaseAsync(OctopushyDb db, Guid resourceId, int version, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var name = Key(resourceId);
        var entry = await db.StoreEntries.SingleAsync(e => e.Key == name, cancellationToken);
        entry.CurrentVersion = version;
        entry.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>An apply carrying <paramref name="version"/> finished: it is the pin from now on, in the same save as the apply's record.</summary>
    public static async Task DeliveredAsync(OctopushyDb db, Guid resourceId, int version, Guid runId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var key = await db.GuestHostKeys.FirstOrDefaultAsync(k => k.ResourceId == resourceId && k.Version == version, cancellationToken);
        if (key is null)
            return;
        key.DeliveredAt = now;
        key.DeliveredRunId = runId;
    }

    /// <summary>The guest is gone, and so is its identity. What each version was released to stays on record.</summary>
    public static async Task RemoveAsync(OctopushyDb db, Guid resourceId, CancellationToken cancellationToken)
    {
        var key = Key(resourceId);
        await db.StoreVersions.Where(v => v.Key == key).ExecuteDeleteAsync(cancellationToken);
        await db.StoreEntries.Where(e => e.Key == key).ExecuteDeleteAsync(cancellationToken);
        await db.GuestHostKeys.Where(k => k.ResourceId == resourceId).ExecuteDeleteAsync(cancellationToken);
    }

    private static async Task<GuestHostKey> AddVersionAsync(
        OctopushyDb db, VariableStore store, Guid resourceId, string guestName, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var name = Key(resourceId);
        var latest = await db.StoreEntries.Where(e => e.Key == name).Select(e => (int?)e.LatestVersion).FirstOrDefaultAsync(cancellationToken);
        var generated = GuestAccessCredentials.Generate($"root@{guestName}");
        var key = new GuestHostKey
        {
            ResourceId = resourceId,
            Version = (latest ?? 0) + 1,
            PublicKey = generated.PublicKey,
            Fingerprint = generated.Fingerprint,
            CreatedAt = now,
        };
        db.GuestHostKeys.Add(key);
        // Same DbContext: the public half and the encrypted private half commit in one SaveChanges, as the version the store makes current.
        await store.SetInternalAsync(name, generated.PrivateKey, Visibilities.Secret, "The guest's SSH host key, Octopushy's", cancellationToken);
        return key;
    }
}

/// <summary>
/// The key Octopushy uploads cloud-init snippets with, one per installation, installed on each of its hosts by
/// <c>deploy/proxmox/snippets.sh</c>. Octopushy generates it so that no person copies a private key: what leaves
/// Octopushy is the public half, for that script. The account it opens can only write snippets, over SFTP, in a chroot,
/// so a leaked key writes snippets and nothing else.
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
