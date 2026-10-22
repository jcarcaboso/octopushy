using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Octopushy.Definitions;
using Octopushy.Server.Data;

namespace Octopushy.Server.Store;

public class StoreException(string message) : Exception(message);

public sealed class StoreConflictException(string message) : StoreException(message);

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
        CheckUserKey(key);
        return SetValueAsync(key, value, visibility, description, false, cancellationToken);
    }

    /// <summary>Sets a value in any scope, for the server's own credentials.</summary>
    public Task SetInternalAsync(string key, string value, string visibility, string? description, CancellationToken cancellationToken) =>
        SetValueAsync(key, value, visibility, description, true, cancellationToken);

    private async Task SetValueAsync(string key, string value, string visibility, string? description, bool internalWrite, CancellationToken cancellationToken)
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
        else if (entry.Visibility == Visibilities.Secret && !internalWrite)
        {
            throw new StoreException("Stage a candidate version to replace an existing secret, then explicitly promote it after verification.");
        }

        entry.Visibility = visibility;
        entry.Description = description ?? entry.Description;
        entry.LatestVersion++;
        entry.CurrentVersion = entry.LatestVersion;
        entry.UpdatedAt = now;
        var (ciphertext, wrapped) = envelope.Seal(Encoding.UTF8.GetBytes(value), Context(key, entry.CurrentVersion));
        db.StoreVersions.Add(new StoreVersion
        {
            Key = key,
            Version = entry.CurrentVersion,
            Ciphertext = ciphertext,
            WrappedKey = wrapped,
            CreatedAt = now,
            ActivatedAt = now,
            ActivatedBy = "initial-or-internal",
            ActivationNote = "Initial value or immediate plain/system value update; not an automated credential check.",
        });
        await SaveAsync(cancellationToken);
    }

    /// <summary>Stores a replacement without changing the active version.</summary>
    public async Task<int> StageAsync(string key, string value, string? description, CancellationToken cancellationToken)
    {
        CheckUserKey(key);
        if (string.IsNullOrEmpty(value))
        {
            throw new StoreException("A candidate value must not be empty.");
        }

        var entry = await db.StoreEntries.FirstOrDefaultAsync(e => e.Key == key, cancellationToken)
            ?? throw new StoreException("Create the initial value before staging a replacement.");
        if (entry.Visibility != Visibilities.Secret)
        {
            throw new StoreException("Only secret entries have staged credential replacements.");
        }

        var now = clock.GetUtcNow();
        var version = ++entry.LatestVersion;
        entry.UpdatedAt = now;
        entry.Description = description ?? entry.Description;
        var (ciphertext, wrapped) = envelope.Seal(Encoding.UTF8.GetBytes(value), Context(key, version));
        db.StoreVersions.Add(new StoreVersion
        {
            Key = key, Version = version, Ciphertext = ciphertext, WrappedKey = wrapped, CreatedAt = now,
        });
        await SaveAsync(cancellationToken);
        return version;
    }

    /// <summary>Operator-attested promotion, not proof of upstream authentication.</summary>
    public async Task PromoteAsync(string key, int version, int expectedCurrentVersion, string verificationNote, string actor, CancellationToken cancellationToken)
    {
        CheckUserKey(key);
        CheckNote(verificationNote, "A note describing the external verification is required.");
        var entry = await db.StoreEntries.FirstOrDefaultAsync(e => e.Key == key, cancellationToken)
            ?? throw new StoreException($"There is no entry '{key}'.");
        if (entry.Visibility != Visibilities.Secret)
        {
            throw new StoreException("Only secret entries support credential promotion.");
        }

        if (entry.CurrentVersion != expectedCurrentVersion)
        {
            throw new StoreConflictException("The active version changed. Refresh and review before promoting.");
        }

        var candidate = await db.StoreVersions.FirstOrDefaultAsync(v => v.Key == key && v.Version == version, cancellationToken)
            ?? throw new StoreException("The selected version does not exist.");
        if (candidate.RevokedAt is not null)
        {
            throw new StoreException("A revoked version cannot be promoted.");
        }

        if (entry.CurrentVersion == version)
        {
            throw new StoreConflictException("The selected version is already active.");
        }

        entry.CurrentVersion = version;
        entry.UpdatedAt = clock.GetUtcNow();
        candidate.ActivatedAt = entry.UpdatedAt;
        candidate.ActivatedBy = actor;
        candidate.ActivationNote = verificationNote.Trim();
        await SaveAsync(cancellationToken);
    }

    /// <summary>Blocks subsequent release; does not retract assignments or revoke a provider token.</summary>
    public async Task RevokeAsync(string key, int version, string reason, string actor, CancellationToken cancellationToken)
    {
        CheckUserKey(key);
        CheckNote(reason, "A revocation reason is required.");
        var entry = await db.StoreEntries.FirstOrDefaultAsync(e => e.Key == key, cancellationToken)
            ?? throw new StoreException($"There is no entry '{key}'.");
        if (entry.Visibility != Visibilities.Secret)
        {
            throw new StoreException("Only secret entries support credential revocation.");
        }

        var value = await db.StoreVersions.FirstOrDefaultAsync(v => v.Key == key && v.Version == version, cancellationToken)
            ?? throw new StoreException("The selected version does not exist.");
        if (value.RevokedAt is not null)
        {
            throw new StoreConflictException("The selected version is already revoked.");
        }

        value.RevokedAt = clock.GetUtcNow();
        value.RevokedBy = actor;
        value.RevocationNote = reason.Trim();
        entry.UpdatedAt = value.RevokedAt.Value;
        await SaveAsync(cancellationToken);
    }

    /// <summary>Changes only the description of an entry.</summary>
    public async Task DescribeAsync(string key, string? description, CancellationToken cancellationToken)
    {
        CheckUserKey(key);
        var entry = await db.StoreEntries.FirstOrDefaultAsync(e => e.Key == key, cancellationToken)
            ?? throw new StoreException($"There is no entry '{key}'.");
        entry.Description = description;
        entry.UpdatedAt = clock.GetUtcNow();
        await SaveAsync(cancellationToken);
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
        if (version.RevokedAt is not null || version.ActivatedAt is null)
        {
            return null;
        }
        var plaintext = envelope.Open(version.Ciphertext, version.WrappedKey, Context(key, version.Version));
        return (Encoding.UTF8.GetString(plaintext), version.Version);
    }

    /// <summary>The value of a plain entry; secrets are never returned.</summary>
    public async Task<string?> GetPlainAsync(string key, CancellationToken cancellationToken)
    {
        if (key.StartsWith(SystemScope, StringComparison.Ordinal))
        {
            return null;
        }

        // Visibility and the immutable version come from one database snapshot. A
        // concurrent plain -> secret update must never return the new secret value.
        var value = await (from entry in db.StoreEntries.AsNoTracking()
                           join version in db.StoreVersions.AsNoTracking()
                               on new { entry.Key, Version = entry.CurrentVersion } equals new { version.Key, version.Version }
                           where entry.Key == key && entry.Visibility == Visibilities.Plain &&
                                 version.RevokedAt == null && version.ActivatedAt != null
                           select version).FirstOrDefaultAsync(cancellationToken);
        return value is null ? null : Encoding.UTF8.GetString(envelope.Open(value.Ciphertext, value.WrappedKey, Context(key, value.Version)));
    }

    /// <summary>Deletes an entry and its versions unless something still references it.</summary>
    public async Task DeleteAsync(string key, IReadOnlyCollection<string> referencedBy, CancellationToken cancellationToken)
    {
        CheckUserKey(key);

        if (referencedBy.Count > 0)
        {
            throw new StoreException($"'{key}' is still used by {string.Join(", ", referencedBy)}.");
        }

        if (await db.SecretResolutions.AnyAsync(r => r.Key == key, cancellationToken))
        {
            throw new StoreException("This key has execution history. Revoke versions instead of deleting their evidence.");
        }

        var contexts = await db.Runs.AsNoTracking().Select(r => r.Context).ToListAsync(cancellationToken);
        if (contexts.Select(Orchestration.RunContext.Parse).Any(c => c.SecretBindingsPinned && c.Secrets.ContainsValue(key)))
        {
            throw new StoreException("A run pins this key. Revoke versions instead of deleting pinned history.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.StoreVersions.Where(v => v.Key == key).ExecuteDeleteAsync(cancellationToken);
        await db.StoreEntries.Where(e => e.Key == key).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Resolves the keys an attempt needs and records each release; null names a missing key.</summary>
    public async Task<(Dictionary<string, string> Values, List<string> Missing)> ResolveForAttemptAsync(
        IReadOnlyDictionary<string, StoreReference> secretNamesToKeys,
        IReadOnlySet<string> requireSecret,
        Guid attemptId,
        Guid agentId,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var (name, reference) in secretNamesToKeys)
        {
            var key = reference.Key;
            var entry = await db.StoreEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Key == key, cancellationToken);
            var version = await db.StoreVersions.AsNoTracking()
                .FirstOrDefaultAsync(v => v.Key == key && v.Version == reference.Version, cancellationToken);
            if (key.StartsWith(SystemScope, StringComparison.Ordinal) || reference.Version is null ||
                entry is null || version is null || version.RevokedAt is not null || version.ActivatedAt is null ||
                (requireSecret.Contains(name) && entry.Visibility != Visibilities.Secret))
            {
                missing.Add($"{key} v{reference.Version}: unavailable, revoked, unpromoted, or not a secret");
                continue;
            }

            values[name] = Encoding.UTF8.GetString(envelope.Open(version.Ciphertext, version.WrappedKey, Context(key, version.Version)));
            db.SecretResolutions.Add(new SecretResolution
            {
                Key = key,
                Version = version.Version,
                AttemptId = attemptId,
                AgentId = agentId,
                ResolvedAt = clock.GetUtcNow(),
            });
        }

        if (missing.Count > 0)
        {
            // No partial release: neither audit rows nor plaintext leave this call.
            foreach (var audit in db.ChangeTracker.Entries<SecretResolution>().Where(e => e.State == EntityState.Added).ToList())
            {
                audit.State = EntityState.Detached;
            }

            values.Clear();
        }
        else
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        return (values, missing);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new StoreConflictException("The entry changed concurrently. Refresh and try again.");
        }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            throw new StoreConflictException("The entry changed concurrently. Refresh and try again.");
        }
    }

    internal static void CheckUserKey(string key)
    {
        if (string.IsNullOrEmpty(key) || !KeyPattern().IsMatch(key) || key.StartsWith(SystemScope, StringComparison.Ordinal))
        {
            throw new StoreException("Use a valid store key outside the reserved system/ scope.");
        }
    }

    private static void CheckNote(string? note, string message)
    {
        if (string.IsNullOrWhiteSpace(note) || note.Length > 2000)
        {
            throw new StoreException(message + " Maximum length: 2000 characters. Do not include credential values.");
        }
    }

    private static string Context(string key, int version) => $"store\0{key}\0{version}";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*(?:/[A-Za-z0-9][A-Za-z0-9._-]*)*$")]
    private static partial Regex KeyPattern();
}
