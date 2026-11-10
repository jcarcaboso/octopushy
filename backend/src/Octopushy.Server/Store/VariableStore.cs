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
/// Plain values are listed; secret values are concealed and read back only when an operator reveals them.
/// </summary>
public sealed partial class VariableStore(OctopushyDb db, Envelope envelope, TimeProvider clock)
{
    public const string SystemScope = LabRepositoryLoader.SystemScope;

    public Task<List<StoreEntry>> ListAsync(CancellationToken cancellationToken) =>
        db.StoreEntries.AsNoTracking().OrderBy(e => e.Key).ToListAsync(cancellationToken);

    /// <summary>Sets a value from the API. Keys in the system scope are refused.</summary>
    /// <param name="generation">For a value a deploy generates, the <see cref="GeneratedValue"/> the new version belongs to.</param>
    public Task SetAsync(string key, string value, string visibility, string? description, CancellationToken cancellationToken, Guid? generation = null)
    {
        CheckUserKey(key);
        return SetValueAsync(key, value, visibility, description, false, cancellationToken, generation);
    }

    /// <summary>Sets a value in any scope, for the server's own credentials.</summary>
    public Task SetInternalAsync(string key, string value, string visibility, string? description, CancellationToken cancellationToken) =>
        SetValueAsync(key, value, visibility, description, true, cancellationToken);

    private async Task SetValueAsync(string key, string value, string visibility, string? description, bool internalWrite, CancellationToken cancellationToken,
        Guid? generation = null)
    {
        await PutValueAsync(key, value, visibility, description, internalWrite, cancellationToken, generation);
        await SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Adds a version of a server-owned value and makes it current, without saving: the caller's own save commits it
    /// together with its other changes, or not at all. Returns the new version.
    /// </summary>
    public Task<int> AddInternalAsync(string key, string value, string visibility, string? description, CancellationToken cancellationToken) =>
        PutValueAsync(key, value, visibility, description, true, cancellationToken);

    private async Task<int> PutValueAsync(string key, string value, string visibility, string? description, bool internalWrite, CancellationToken cancellationToken,
        Guid? generation = null)
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
        // An entry added earlier in this unit of work and not saved yet is the same entry.
        var entry = db.StoreEntries.Local.FirstOrDefault(e => e.Key == key)
            ?? await db.StoreEntries.FirstOrDefaultAsync(e => e.Key == key, cancellationToken);
        if (entry is null)
        {
            // A key deleted before continues its numbering: an old version number never names a new value.
            entry = new StoreEntry { Key = key, Visibility = visibility, CreatedAt = now, LatestVersion = await DeletedLatestVersionAsync(key, cancellationToken) };
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
            GenerationId = generation,
        });
        return entry.CurrentVersion;
    }

    /// <summary>Stores a replacement without changing the active version.</summary>
    public async Task<int> StageAsync(string key, string value, string? description, CancellationToken cancellationToken)
    {
        CheckUserKey(key);
        return await StageValueAsync(key, value, description, cancellationToken);
    }

    /// <summary>A candidate version of a server-owned secret. The operator endpoints stay closed to system/.</summary>
    public Task<int> StageInternalAsync(string key, string value, CancellationToken cancellationToken) =>
        StageValueAsync(key, value, null, cancellationToken);

    private async Task<int> StageValueAsync(string key, string value, string? description, CancellationToken cancellationToken)
    {
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

        Activate(entry, candidate, clock.GetUtcNow(), actor, verificationNote);
        await SaveAsync(cancellationToken);
    }

    /// <summary>The one way a version becomes the active one, whoever asks.</summary>
    internal static void Activate(StoreEntry entry, StoreVersion version, DateTimeOffset now, string actor, string note)
    {
        entry.CurrentVersion = version.Version;
        entry.UpdatedAt = now;
        version.ActivatedAt = now;
        version.ActivatedBy = actor;
        version.ActivationNote = note.Trim();
    }

    /// <summary>The one way a version stops being released, whoever asks.</summary>
    internal static void Revoke(StoreEntry entry, StoreVersion version, DateTimeOffset now, string actor, string reason)
    {
        version.RevokedAt = now;
        version.RevokedBy = actor;
        version.RevocationNote = reason.Trim();
        entry.UpdatedAt = now;
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

        Revoke(entry, value, clock.GetUtcNow(), actor, reason);
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

    /// <summary>
    /// One exact version of a server-owned credential, for the single caller that releases it. A
    /// revoked version is refused, and an unpromoted one unless a rotation is testing it.
    /// </summary>
    public async Task<string?> GetVersionInternalAsync(string key, int number, CancellationToken cancellationToken, bool candidate = false)
    {
        var version = await db.StoreVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Key == key && v.Version == number && v.RevokedAt == null && (candidate || v.ActivatedAt != null), cancellationToken);
        return version is null ? null : Encoding.UTF8.GetString(envelope.Open(version.Ciphertext, version.WrappedKey, Context(key, number)));
    }

    /// <summary>
    /// A version's value for masking only, whatever its state: one revoked since it was released still has to be masked
    /// in what the attempt it went to reports. Null when the version is gone.
    /// </summary>
    public async Task<string?> OpenForMaskingAsync(string key, int number, CancellationToken cancellationToken)
    {
        var version = await db.StoreVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Key == key && v.Version == number, cancellationToken);
        return version is null ? null : Encoding.UTF8.GetString(envelope.Open(version.Ciphertext, version.WrappedKey, Context(key, number)));
    }

    /// <summary>The value of a plain entry; secrets are never returned.</summary>
    public Task<string?> GetPlainAsync(string key, CancellationToken cancellationToken) =>
        key.StartsWith(SystemScope, StringComparison.Ordinal) ? Task.FromResult<string?>(null) : ActiveValueAsync(key, plainOnly: true, cancellationToken);

    /// <summary>
    /// The active value of a key outside the system scope, plain or secret, for an operator who asked to see it. Null when the
    /// key is missing or its active version is revoked or was never promoted.
    /// </summary>
    public Task<string?> RevealAsync(string key, CancellationToken cancellationToken)
    {
        CheckUserKey(key);
        return ActiveValueAsync(key, plainOnly: false, cancellationToken);
    }

    private async Task<string?> ActiveValueAsync(string key, bool plainOnly, CancellationToken cancellationToken)
    {
        // Visibility and the immutable version come from one database snapshot. A
        // concurrent plain -> secret update must never return the new secret value.
        var value = await (from entry in db.StoreEntries.AsNoTracking()
                           join version in db.StoreVersions.AsNoTracking()
                               on new { entry.Key, Version = entry.CurrentVersion } equals new { version.Key, version.Version }
                           where entry.Key == key && (!plainOnly || entry.Visibility == Visibilities.Plain) &&
                                 version.RevokedAt == null && version.ActivatedAt != null
                           select version).FirstOrDefaultAsync(cancellationToken);
        return value is null ? null : Encoding.UTF8.GetString(envelope.Open(value.Ciphertext, value.WrappedKey, Context(key, value.Version)));
    }

    /// <summary>
    /// The version the next write of <paramref name="key"/> creates: one after its latest, or, for a key not in the store, one
    /// after the last it had before it was deleted.
    /// </summary>
    public async Task<int> NextVersionAsync(string key, CancellationToken cancellationToken)
    {
        var latest = await db.StoreEntries.AsNoTracking().Where(e => e.Key == key).Select(e => (int?)e.LatestVersion).FirstOrDefaultAsync(cancellationToken);
        return (latest ?? await DeletedLatestVersionAsync(key, cancellationToken)) + 1;
    }

    private Task<int> DeletedLatestVersionAsync(string key, CancellationToken cancellationToken) =>
        db.DeletedStoreKeys.AsNoTracking().Where(d => d.Key == key).Select(d => d.LatestVersion).FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Deletes an entry once nothing needs it: every version's encrypted value goes, and the key leaves the list. Who received
    /// which version stays on record in <see cref="SecretResolution"/>, and the key's last version number is kept, so a key
    /// written again continues after it. Refused while a definition, an open change, a run that has not finished, or an attempt
    /// that may still receive it or report uses it.
    /// </summary>
    /// <param name="referencedBy">What else holds the key, as the caller knows it: see <see cref="Api.VariableUses.HoldingAsync"/>.</param>
    public async Task DeleteAsync(string key, IReadOnlyCollection<string> referencedBy, string actor, CancellationToken cancellationToken)
    {
        CheckUserKey(key);
        var users = referencedBy.Concat(await UnfinishedUsesAsync(key, cancellationToken)).Distinct(StringComparer.Ordinal).ToList();
        if (users.Count > 0)
        {
            throw new StoreException($"'{key}' is still used by {string.Join(", ", users)}. Delete it once nothing uses it.");
        }

        var entry = await db.StoreEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Key == key, cancellationToken);
        if (entry is null)
        {
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Only the entry as it was checked: a version staged or written since makes this a conflict, and nothing is deleted.
        var deleted = await db.StoreEntries.Where(e => e.Key == key && e.LatestVersion == entry.LatestVersion).ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
        {
            throw new StoreConflictException("The entry changed concurrently. Refresh and try again.");
        }

        await db.StoreVersions.Where(v => v.Key == key).ExecuteDeleteAsync(cancellationToken);
        await RecordDeletionAsync(db, key, entry.LatestVersion, actor, clock.GetUtcNow(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        // What this unit of work tracked of the key is gone too: a write after this one starts a new entry.
        var stale = db.ChangeTracker.Entries<StoreEntry>().Where(e => e.Entity.Key == key).Cast<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry>()
            .Concat(db.ChangeTracker.Entries<StoreVersion>().Where(e => e.Entity.Key == key)).ToList();
        foreach (var tracked in stale)
        {
            tracked.State = EntityState.Detached;
        }
    }

    /// <summary>
    /// Records the last version of a key whose entry is being deleted, in the caller's transaction, so that a key written again
    /// continues after it. Never lowers what an earlier deletion recorded.
    /// </summary>
    internal static Task RecordDeletionAsync(OctopushyDb db, string key, int latestVersion, string actor, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO deleted_store_keys (key, latest_version, deleted_at, deleted_by) VALUES ({key}, {latestVersion}, {now}, {actor})
            ON CONFLICT (key) DO UPDATE SET latest_version = GREATEST(deleted_store_keys.latest_version, EXCLUDED.latest_version),
                deleted_at = EXCLUDED.deleted_at, deleted_by = EXCLUDED.deleted_by
            """, cancellationToken);

    /// <summary>
    /// What may still need the key's value: a run that has not finished and binds it, pinned or not, since one waiting for
    /// approval or not yet dispatched takes it later; and an attempt that has not finished, which may still be given it, or
    /// was given it and still reports, so its logs and outputs are masked with it. A finished attempt needs nothing: what it
    /// reported was masked when it was kept, and a late result is withheld when its values cannot be read back.
    /// </summary>
    private async Task<List<string>> UnfinishedUsesAsync(string key, CancellationToken cancellationToken)
    {
        var uses = new List<string>();
        var runs = await db.Runs.AsNoTracking()
            .Where(r => r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled)
            .Select(r => new { r.Number, r.Context })
            .ToListAsync(cancellationToken);
        uses.AddRange(runs.Where(r => Orchestration.RunContext.Parse(r.Context).Secrets.ContainsValue(key)).OrderBy(r => r.Number).Select(r => $"run #{r.Number}"));

        var attempts = await db.Attempts.AsNoTracking()
            .Where(a => a.Status == AttemptStatuses.Queued || a.Status == AttemptStatuses.Assigned || a.Status == AttemptStatuses.Running)
            .Select(a => new
            {
                a.Context,
                Released = db.SecretResolutions.Any(r => r.AttemptId == a.Id && r.Key == key),
                Run = db.Runs.Where(r => r.Id == a.RunId).Select(r => (long?)r.Number).FirstOrDefault(),
                Installation = db.Resources.Where(r => r.Id == a.ResourceId).Select(r => r.Address).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        foreach (var attempt in attempts.Where(a => a.Released || (a.Context is { } context && Orchestration.RunContext.Parse(context).Secrets.ContainsValue(key))))
        {
            uses.Add(attempt.Run is { } number ? $"run #{number}"
                : $"an observation of {attempt.Installation ?? "a removed installation"}");
        }

        return uses;
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
