using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;

namespace Octopushy.Server.Store;

/// <summary>
/// The SSH host keys of a container Octopushy made, pinned by their SHA-256 fingerprints as Proxmox logged them when it
/// generated them: one line per key, <c>type SHA256:…</c>. A plain entry under system/, which no definition can name; the
/// verify step of the guest's own provision run writes it, and its Ansible work binds it as its pin. See
/// <see cref="Definitions.ContainerHostKey"/>.
/// </summary>
public static class ContainerHostKeys
{
    public const string Prefix = "system/container-host-key/";

    public static string Key(Guid resourceId) => $"{Prefix}{resourceId}";

    /// <summary>What a person can do about a container with no pin in use, as the runs waiting for one say it.</summary>
    public const string Recovery = "Octopushy reads them only from the create task of the provision run that made this container. "
        + "Plan the guest: its verify reads them again from that creation, if nothing has changed the VMID since. "
        + "If that verify says they could not be read, or no run of Octopushy's made this container, planning again will not fix it: "
        + "set access.hostKey, or rebuild the container.";

    /// <summary>The fingerprints a verify step read, or null when it read none.</summary>
    public static string? Read(System.Text.Json.Nodes.JsonNode? verified)
    {
        var lines = (verified?["hostKeys"] as System.Text.Json.Nodes.JsonArray ?? [])
            .Select(k => k?.ToString().Trim()).Where(k => !string.IsNullOrEmpty(k)).Order(StringComparer.Ordinal).ToList();
        return lines.Count == 0 ? null : string.Join('\n', lines) + "\n";
    }

    /// <summary>Whether the guest has a pin a run can use now: its current version is active and not revoked.</summary>
    public static async Task<bool> InUseAsync(OctopushyDb db, Guid resourceId, CancellationToken cancellationToken)
    {
        var key = Key(resourceId);
        return await (from entry in db.StoreEntries.AsNoTracking()
                      join version in db.StoreVersions.AsNoTracking() on new { entry.Key, Version = entry.CurrentVersion } equals new { version.Key, version.Version }
                      where entry.Key == key && version.RevokedAt == null && version.ActivatedAt != null
                      select version).AnyAsync(cancellationToken);
    }

    /// <summary>Records the fingerprints as the pin in use: a new version when they changed, as for a replaced container.</summary>
    public static async Task RecordAsync(VariableStore store, Guid resourceId, string fingerprints, CancellationToken cancellationToken)
    {
        var key = Key(resourceId);
        if (await store.GetAsync(key, cancellationToken) is { } current && current.Value == fingerprints)
            return;
        await store.SetInternalAsync(key, fingerprints, Visibilities.Plain, "The SSH host keys Proxmox logged when it created this container", cancellationToken);
    }

    /// <summary>
    /// The pin stops being usable for any connection, from now on: every version is revoked, and kept, for the record of
    /// what was released. Called as an apply that may destroy the container it was read from is sent, whether that apply
    /// then succeeds, fails or is lost: from then on the machine at the VMID may be another. A run bound to a revoked
    /// version is blocked, and the next verify that reads the new container's keys records them as a new version.
    /// </summary>
    public static async Task WithdrawAsync(OctopushyDb db, Guid resourceId, DateTimeOffset now, string reason, CancellationToken cancellationToken)
    {
        var key = Key(resourceId);
        var entry = await db.StoreEntries.FirstOrDefaultAsync(e => e.Key == key, cancellationToken);
        if (entry is null)
            return;
        foreach (var version in await db.StoreVersions.Where(v => v.Key == key && v.RevokedAt == null).ToListAsync(cancellationToken))
        {
            VariableStore.Revoke(entry, version, now, "octopushy", reason);
        }
    }

    /// <summary>Retiring the guest removes its pin with its machine.</summary>
    public static async Task RemoveAsync(OctopushyDb db, Guid resourceId, CancellationToken cancellationToken)
    {
        var key = Key(resourceId);
        await db.StoreVersions.Where(v => v.Key == key).ExecuteDeleteAsync(cancellationToken);
        await db.StoreEntries.Where(e => e.Key == key).ExecuteDeleteAsync(cancellationToken);
    }
}
