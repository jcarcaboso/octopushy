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

    /// <summary>The fingerprints a verify step read, or null when it read none.</summary>
    public static string? Read(System.Text.Json.Nodes.JsonNode? verified)
    {
        var lines = (verified?["hostKeys"] as System.Text.Json.Nodes.JsonArray ?? [])
            .Select(k => k?.ToString().Trim()).Where(k => !string.IsNullOrEmpty(k)).Order(StringComparer.Ordinal).ToList();
        return lines.Count == 0 ? null : string.Join('\n', lines) + "\n";
    }

    /// <summary>Records the fingerprints as the pin in use: a new version when they changed, as for a replaced container.</summary>
    public static async Task RecordAsync(VariableStore store, Guid resourceId, string fingerprints, CancellationToken cancellationToken)
    {
        var key = Key(resourceId);
        if (await store.GetAsync(key, cancellationToken) is { } current && current.Value == fingerprints)
            return;
        await store.SetInternalAsync(key, fingerprints, Visibilities.Plain, "The SSH host keys Proxmox logged when it created this container", cancellationToken);
    }

    /// <summary>Retiring the guest removes its pin with its machine.</summary>
    public static async Task RemoveAsync(OctopushyDb db, Guid resourceId, CancellationToken cancellationToken)
    {
        var key = Key(resourceId);
        await db.StoreVersions.Where(v => v.Key == key).ExecuteDeleteAsync(cancellationToken);
        await db.StoreEntries.Where(e => e.Key == key).ExecuteDeleteAsync(cancellationToken);
    }
}
