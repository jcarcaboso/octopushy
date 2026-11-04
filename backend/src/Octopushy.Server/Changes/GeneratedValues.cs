using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Definitions.Yaml;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Changes;

/// <summary>
/// Removes what deploys generated once nothing can use it. Every trigger comes through <see cref="CleanUpAsync"/>, with the same
/// guards: each sync, and a deploy refused before its push. A value is removed only when all of these hold:
/// <list type="bullet">
/// <item>its change is final, so its status will not change again;</item>
/// <item>no definition at the branch head references its key: every <c>store</c> in any definition file, valid or not, so a
/// dismissed change whose commit turns up later, or an applied guest, keeps what it uses;</item>
/// <item>no change that is not final references it in a commit it recorded, pushed or about to be;</item>
/// <item>no run that has not finished uses its key.</item>
/// </list>
/// What is removed is only what the value owns: the store version it wrote, identified by the generation id that version was
/// created with, while it is still the one present, and its enrollment token, unless the token was used. Each removal is its own
/// transaction. One that fails stays pending, with its error, and each sync tries it again. Drafts of the repository editor and
/// branches other than the one Octopushy reads are not looked at: a value referenced only there can be removed.
/// </summary>
public static class GeneratedValues
{
    /// <summary>Definition files: what a store reference in the lab repository can be written in.</summary>
    private static readonly string[] DefinitionFolders = ["labs", "templates"];

    /// <summary>
    /// Every store key a definition file in the snapshot at <paramref name="root"/> references: each <c>store</c> anywhere in
    /// <c>octopushy.yaml</c> and the YAML under <c>labs/</c> and <c>templates/</c>, read as plain YAML whether the file validates
    /// or not. Null, so that nothing is removed, when the snapshot or any of those files cannot be read.
    /// </summary>
    public static IReadOnlySet<string>? ReferencedKeys(string root)
    {
        if (!Directory.Exists(root))
            return null;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var files = DefinitionFolders.Select(f => Path.Combine(root, f)).Where(Directory.Exists)
                .SelectMany(f => Directory.EnumerateFiles(f, "*", SearchOption.AllDirectories))
                .Where(f => f.EndsWith(".yaml", StringComparison.Ordinal) || f.EndsWith(".yml", StringComparison.Ordinal))
                .Append(Path.Combine(root, "octopushy.yaml")).Where(File.Exists);
            foreach (var file in files)
            {
                if (!Collect(File.ReadAllText(file), keys))
                    return null;
            }
        }
        catch (IOException)
        {
            return null;
        }

        return keys;
    }

    /// <summary>Adds every <c>store</c> string in a YAML text to <paramref name="keys"/>; false when the text is not YAML.</summary>
    public static bool Collect(string text, ISet<string> keys)
    {
        JsonNode? root;
        try
        {
            root = YamlDocumentReader.Read(text).Root;
        }
        catch (YamlReadException)
        {
            return false;
        }

        Walk(root, keys);
        return true;

        static void Walk(JsonNode? node, ISet<string> keys)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (key, value) in o)
                    {
                        if (key == "store" && value is JsonValue v && v.TryGetValue<string>(out var stored))
                            keys.Add(stored);
                        Walk(value, keys);
                    }

                    break;
                case JsonArray a:
                    foreach (var item in a)
                        Walk(item, keys);
                    break;
            }
        }
    }

    /// <summary>How long a removal waits for a row someone else holds, before it is left for the next sync.</summary>
    private const string LockTimeout = "5s";

    /// <param name="referenced">Every store key a definition at the branch head references (<see cref="ReferencedKeys"/>).</param>
    /// <param name="change">Only this change's values; every change's when null.</param>
    public static async Task CleanUpAsync(OctopushyDb db, RepositoryMirror mirror, IReadOnlySet<string> referenced, ILogger logger, DateTimeOffset now,
        Guid? change = null)
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
        // What changes that are not final may still bring to the branch: a key one of their commits references is not removed.
        if (await KeysOfOpenChangesAsync(db, mirror) is not { } open)
        {
            logger.LogWarning("A commit of an open change could not be read; no generated value is removed until it can.");
            return;
        }

        inUse.UnionWith(open);
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
            var generation = value.Id;
            var entry = await db.StoreEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Key == key, CancellationToken.None);
            var ours = entry is not null && entry.LatestVersion == version && entry.CurrentVersion == version
                && await db.StoreVersions.AnyAsync(v => v.Key == key && v.Version == version && v.GenerationId == generation, CancellationToken.None);
            if (entry is null)
                notes.Add($"{key} was already gone.");
            else if (!ours)
            {
                // Someone wrote the key since: a person, or a later deploy of the same name, whatever its description. It is theirs.
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
                // Only while it is still exactly the version this value wrote; anything else is left alone.
                var owned = db.StoreEntries.Where(e => e.Key == key && e.LatestVersion == version && e.CurrentVersion == version
                    && db.StoreVersions.Any(v => v.Key == key && v.Version == version && v.GenerationId == generation));
                await db.StoreVersions.Where(v => v.Key == key && owned.Any()).ExecuteDeleteAsync(CancellationToken.None);
                var deleted = await db.StoreEntries.Where(e => e.Key == key && e.LatestVersion == version && e.CurrentVersion == version)
                    .ExecuteDeleteAsync(CancellationToken.None);
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

    /// <summary>
    /// Every store key a change that is not final may still bring to the branch: in the values it recorded writing
    /// (<see cref="ResourceChange.Edited"/>, a deploy's whole new definition included), and in its files at each commit it
    /// recorded, pushed or about to be, and the one that landed. A dismissed change is among them: a dismissal proves nothing.
    /// A commit the mirror does not hold is read from what the change recorded alone. Null when a file is not YAML, so nothing
    /// is removed.
    /// </summary>
    private static async Task<HashSet<string>?> KeysOfOpenChangesAsync(OctopushyDb db, RepositoryMirror mirror)
    {
        var finals = ChangeStatuses.Final;
        var open = await db.Changes.AsNoTracking().Where(c => !finals.Contains(c.Status))
            .Select(c => new { c.Commit, c.CandidateCommits, c.Files, c.Edited }).ToListAsync(CancellationToken.None);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in open)
        {
            if (change.Edited is { } edited && !Collect(edited, keys))
                return null;
            foreach (var commit in change.CandidateCommits.Append(change.Commit).OfType<string>().Distinct())
            {
                foreach (var file in change.Files.Where(f => f.EndsWith(".yaml", StringComparison.Ordinal) || f.EndsWith(".yml", StringComparison.Ordinal)))
                {
                    string? text;
                    try
                    {
                        // A file the commit deletes holds nothing.
                        text = (await mirror.ReadFileAsync(commit, file, CancellationToken.None))?.Text;
                    }
                    catch (GitException)
                    {
                        // Not in this mirror, so not on the branch it fetched: what the change recorded stands for it.
                        continue;
                    }

                    if (text is not null && !Collect(text, keys))
                        return null;
                }
            }
        }

        return keys;
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
