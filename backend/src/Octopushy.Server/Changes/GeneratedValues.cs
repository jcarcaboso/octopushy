using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Changes;

/// <summary>
/// Removes what deploys generated once nothing can use it. Every trigger comes through <see cref="CleanUpAsync"/>, with the same
/// guards: each sync, a deploy refused before its push, and a dismissal. A value is removed only when all of these hold:
/// <list type="bullet">
/// <item>its change is final, so its status will not change again;</item>
/// <item>no definition at the branch head references its key, so a dismissed change whose commit turns up later, or an applied
/// guest, keeps what it uses;</item>
/// <item>no run that has not finished uses its key.</item>
/// </list>
/// What is removed is only what the value owns: the store version it wrote, while that version is still the one present and the
/// entry still carries the description naming its change, and its enrollment token, unless the token was used. Each removal
/// is its own transaction. One that fails stays pending, with its error, and each sync tries it again.
/// </summary>
public static partial class GeneratedValues
{
    /// <summary>
    /// Every store key a definition at <paramref name="head"/> may reference: those it read, and, so that a definition that does
    /// not validate still counts, every <c>store:</c> written in any YAML file under <c>labs/</c>. Null when the snapshot cannot be
    /// read, and then nothing is removed.
    /// </summary>
    public static IReadOnlySet<string>? ReferencedKeys(LabRepository head)
    {
        var keys = head.StoreUses.Select(u => u.Key).ToHashSet(StringComparer.Ordinal);
        var labs = Path.Combine(head.Root, "labs");
        if (!Directory.Exists(head.Root))
            return null;
        if (!Directory.Exists(labs))
            return keys;
        try
        {
            foreach (var file in Directory.EnumerateFiles(labs, "*.yaml", SearchOption.AllDirectories))
            {
                foreach (Match match in StoreReference().Matches(File.ReadAllText(file)))
                    keys.Add(match.Groups["key"].Value);
            }
        }
        catch (IOException)
        {
            return null;
        }

        return keys;
    }

    [GeneratedRegex(@"\bstore\s*:\s*[""']?(?<key>[A-Za-z0-9][A-Za-z0-9._/-]*)")]
    private static partial Regex StoreReference();

    /// <summary>How long a removal waits for a row someone else holds, before it is left for the next sync.</summary>
    private const string LockTimeout = "5s";

    /// <param name="referenced">Every store key a definition at the branch head references.</param>
    /// <param name="change">Only this change's values; every change's when null.</param>
    public static async Task CleanUpAsync(OctopushyDb db, IReadOnlySet<string> referenced, ILogger logger, DateTimeOffset now, Guid? change = null)
    {
        var finals = ChangeStatuses.Final;
        var values = await db.GeneratedValues
            .Where(v => (v.State == GeneratedValueStates.Planned || v.State == GeneratedValueStates.Created)
                        && (change == null || v.ChangeId == change)
                        && db.Changes.Any(c => c.Id == v.ChangeId && finals.Contains(c.Status)))
            .OrderBy(v => v.CreatedAt)
            .ToListAsync(CancellationToken.None);
        if (values.Count == 0)
            return;
        var inUse = await KeysOfUnfinishedRunsAsync(db);
        foreach (var value in values)
        {
            if (value.State == GeneratedValueStates.Planned)
            {
                // Recorded before it was made, and each part is recorded in the save that makes it: nothing of it exists.
                Settle(value, GeneratedValueStates.Removed, "Never made: nothing to remove.", now);
                await db.SaveChangesAsync(CancellationToken.None);
                continue;
            }

            if (referenced.Contains(value.Key) || inUse.Contains(value.Key))
            {
                // In use: looked at again at the next sync, when what uses it may be gone.
                if (value.CleanupPending)
                {
                    value.CleanupPending = false;
                    value.Detail = referenced.Contains(value.Key) ? "A definition at the branch head uses it." : "A run that has not finished uses it.";
                    value.UpdatedAt = now;
                    await db.SaveChangesAsync(CancellationToken.None);
                }

                continue;
            }

            try
            {
                await RemoveAsync(db, value, now);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // What the failed transaction set on it never happened.
                await db.Entry(value).ReloadAsync(CancellationToken.None);
                var pending = value;
                pending.CleanupPending = true;
                pending.CleanupError = e.InnerException?.Message ?? e.Message;
                pending.CleanupAttempts++;
                pending.UpdatedAt = now;
                await db.SaveChangesAsync(CancellationToken.None);
                logger.LogWarning("Removing {Key}, generated by change {Change}, failed; the next sync tries again: {Error}",
                    value.Key, value.ChangeId, pending.CleanupError);
            }
        }
    }

    /// <summary>Removes what one value owns, in one transaction.</summary>
    private static async Task RemoveAsync(OctopushyDb db, GeneratedValue value, DateTimeOffset now)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        await db.Database.ExecuteSqlRawAsync($"SET LOCAL lock_timeout = '{LockTimeout}'", CancellationToken.None);
        var notes = new List<string>();
        var kept = false;
        if (value.StoreVersion is { } version)
        {
            var key = value.Key;
            var marker = value.StoreDescription;
            var entry = await db.StoreEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Key == key, CancellationToken.None);
            if (entry is null)
                notes.Add($"{key} was already gone.");
            else if (entry.Description != marker || entry.LatestVersion != version || entry.CurrentVersion != version)
            {
                // Someone wrote the key since: a person, or a later deploy of the same name. It is theirs now.
                kept = true;
                notes.Add($"{key} was written again since, so it is left as it is.");
            }
            else if (await db.SecretResolutions.AnyAsync(r => r.Key == key, CancellationToken.None))
            {
                kept = true;
                notes.Add($"{key} was given to a run, so its history is kept: revoke it under Variables if it must go.");
            }
            else
            {
                // Only while it is still exactly what this value wrote; anything else is left alone.
                var owned = db.StoreEntries.Where(e => e.Key == key && e.LatestVersion == version && e.CurrentVersion == version && e.Description == marker);
                await db.StoreVersions.Where(v => v.Key == key && owned.Any()).ExecuteDeleteAsync(CancellationToken.None);
                var deleted = await owned.ExecuteDeleteAsync(CancellationToken.None);
                if (deleted == 0)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    throw new InvalidOperationException($"{key} changed while it was being removed.");
                }

                notes.Add($"{key} version {version} deleted.");
            }
        }

        if (value.TokenId is { } token)
        {
            var revoked = await db.EnrollmentTokens.Where(t => t.Id == token && t.UsedAt == null).ExecuteDeleteAsync(CancellationToken.None);
            if (revoked > 0)
                notes.Add("Its enrollment token was revoked.");
            else if (await db.EnrollmentTokens.AnyAsync(t => t.Id == token, CancellationToken.None))
                notes.Add("Its enrollment token was used, so it stays: removing it would not affect the agent it enrolled.");
        }

        Settle(value, kept ? GeneratedValueStates.Kept : GeneratedValueStates.Removed, string.Join(" ", notes), now);
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }

    private static void Settle(GeneratedValue value, string state, string detail, DateTimeOffset now)
    {
        value.State = state;
        value.Detail = detail;
        value.CleanupPending = false;
        value.CleanupError = null;
        value.UpdatedAt = now;
    }

    /// <summary>Every store key a run that has not finished was built with.</summary>
    private static async Task<HashSet<string>> KeysOfUnfinishedRunsAsync(OctopushyDb db)
    {
        var contexts = await db.Runs.AsNoTracking()
            .Where(r => r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled)
            .Select(r => r.Context).ToListAsync(CancellationToken.None);
        return contexts.SelectMany(c => RunContext.Parse(c).Secrets.Values).ToHashSet(StringComparer.Ordinal);
    }
}
