using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Octopushy.Definitions;
using Octopushy.Server.Data;

namespace Octopushy.Server.Store;

public sealed class StoreException(string message) : Exception(message);

/// <summary>
/// The built-in variable store: a key/value dictionary whose values are encrypted at rest.
/// Plain values can be read back; secret values are write-only once saved.
/// </summary>
public sealed partial class VariableStore(OctopushyDb db, Envelope envelope, TimeProvider clock)
{
    public const string SystemScope = LabRepositoryLoader.SystemScope;

    public Task<List<StoreEntry>> ListAsync(CancellationToken cancellationToken) =>
        db.StoreEntries.AsNoTracking().OrderBy(e => e.Key).ToListAsync(cancellationToken);

    /// <summary>Sets a value from the API. Keys in the system scope are refused.</summary>
    public Task SetAsync(string key, string value, string visibility, string? description, CancellationToken cancellationToken)
    {
        if (key.StartsWith(SystemScope, StringComparison.Ordinal))
        {
            throw new StoreException("Keys under system/ hold Octopushy's own credentials and are managed by the server.");
        }

        return SetInternalAsync(key, value, visibility, description, cancellationToken);
    }

    /// <summary>Sets a value in any scope, for the server's own credentials.</summary>
    public async Task SetInternalAsync(string key, string value, string visibility, string? description, CancellationToken cancellationToken)
    {
        if (!KeyPattern().IsMatch(key))
        {
            throw new StoreException("A key is one or more segments of letters, digits, '.', '_', or '-', separated by '/'.");
        }

        if (visibility is not (Visibilities.Plain or Visibilities.Secret))
        {
            throw new StoreException("Visibility is plain or secret.");
        }

        var now = clock.GetUtcNow();
        var entry = await db.StoreEntries.FirstOrDefaultAsync(e => e.Key == key, cancellationToken);
        if (entry is null)
        {
            entry = new StoreEntry { Key = key, Visibility = visibility, CreatedAt = now };
            db.StoreEntries.Add(entry);
        }
        else if (entry.Visibility == Visibilities.Secret && visibility == Visibilities.Plain)
        {
            throw new StoreException("A secret cannot be made plain.");
        }

        entry.Visibility = visibility;
        entry.Description = description ?? entry.Description;
        entry.CurrentVersion++;
        entry.UpdatedAt = now;
        var (ciphertext, wrapped) = envelope.Seal(Encoding.UTF8.GetBytes(value), Context(key, entry.CurrentVersion));
        db.StoreVersions.Add(new StoreVersion
        {
            Key = key,
            Version = entry.CurrentVersion,
            Ciphertext = ciphertext,
            WrappedKey = wrapped,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Changes only the description of an entry.</summary>
    public async Task DescribeAsync(string key, string? description, CancellationToken cancellationToken)
    {
        var entry = await db.StoreEntries.FirstOrDefaultAsync(e => e.Key == key, cancellationToken)
            ?? throw new StoreException($"There is no entry '{key}'.");
        entry.Description = description;
        entry.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The current value and version, for the server's own use.</summary>
    public async Task<(string Value, int Version)?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var entry = await db.StoreEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Key == key, cancellationToken);
        if (entry is null)
        {
            return null;
        }

        var version = await db.StoreVersions.AsNoTracking()
            .FirstAsync(v => v.Key == key && v.Version == entry.CurrentVersion, cancellationToken);
        var plaintext = envelope.Open(version.Ciphertext, version.WrappedKey, Context(key, version.Version));
        return (Encoding.UTF8.GetString(plaintext), version.Version);
    }

    /// <summary>The value of a plain entry; secrets are never returned.</summary>
    public async Task<string?> GetPlainAsync(string key, CancellationToken cancellationToken)
    {
        var entry = await db.StoreEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Key == key, cancellationToken);
        if (entry is null || entry.Visibility != Visibilities.Plain || key.StartsWith(SystemScope, StringComparison.Ordinal))
        {
            return null;
        }

        return (await GetAsync(key, cancellationToken))?.Value;
    }

    /// <summary>Deletes an entry and its versions unless something still references it.</summary>
    public async Task DeleteAsync(string key, IReadOnlyCollection<string> referencedBy, CancellationToken cancellationToken)
    {
        if (key.StartsWith(SystemScope, StringComparison.Ordinal))
        {
            throw new StoreException("Keys under system/ are managed by the server.");
        }

        if (referencedBy.Count > 0)
        {
            throw new StoreException($"'{key}' is still used by {string.Join(", ", referencedBy)}.");
        }

        await db.StoreVersions.Where(v => v.Key == key).ExecuteDeleteAsync(cancellationToken);
        await db.StoreEntries.Where(e => e.Key == key).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Resolves the keys an attempt needs and records each release; null names a missing key.</summary>
    public async Task<(Dictionary<string, string> Values, List<string> Missing)> ResolveForAttemptAsync(
        IReadOnlyDictionary<string, string> secretNamesToKeys,
        Guid attemptId,
        Guid agentId,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var (name, key) in secretNamesToKeys)
        {
            var resolved = await GetAsync(key, cancellationToken);
            if (resolved is null)
            {
                missing.Add(key);
                continue;
            }

            values[name] = resolved.Value.Value;
            db.SecretResolutions.Add(new SecretResolution
            {
                Key = key,
                Version = resolved.Value.Version,
                AttemptId = attemptId,
                AgentId = agentId,
                ResolvedAt = clock.GetUtcNow(),
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return (values, missing);
    }

    private static string Context(string key, int version) => $"store\0{key}\0{version}";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*(?:/[A-Za-z0-9][A-Za-z0-9._-]*)*$")]
    private static partial Regex KeyPattern();
}
