using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;

namespace Octopushy.Server.Changes;

/// <summary>
/// The values a deploy generated, discarded when its commit never reached the branch: its store keys deleted, with their
/// versions, and its enrollment tokens revoked unless one was used. Both a refused deploy and one that
/// <see cref="ChangeReconciler"/> fails come through here, and never on a request's token, so a cancelled request leaves
/// nothing behind.
/// </summary>
public static class GeneratedValues
{
    public static async Task DiscardAsync(OctopushyDb db, ResourceChange change, ILogger logger, DateTimeOffset now)
    {
        if (change.GeneratedKeys.Count == 0 && change.GeneratedTokens.Count == 0)
            return;
        var keys = change.GeneratedKeys.ToList();
        var tokens = change.GeneratedTokens.ToList();
        try
        {
            await db.StoreVersions.Where(v => keys.Contains(v.Key)).ExecuteDeleteAsync(CancellationToken.None);
            await db.StoreEntries.Where(e => keys.Contains(e.Key)).ExecuteDeleteAsync(CancellationToken.None);
            await db.EnrollmentTokens.Where(t => tokens.Contains(t.Id) && t.UsedAt == null).ExecuteDeleteAsync(CancellationToken.None);
            change.GeneratedKeys = [];
            change.GeneratedTokens = [];
            change.UpdatedAt = now;
            logger.LogInformation("Deploy {Change} committed nothing: deleted {Keys} generated store value(s) and revoked {Tokens} enrollment token(s).",
                change.Id, keys.Count, tokens.Count);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Kept on the change, so the next reconciliation or a person can finish it.
            logger.LogError(e, "Deploy {Change} committed nothing, and its generated values could not all be removed: {Keys}. Delete them under Variables.",
                change.Id, string.Join(", ", keys));
        }
    }
}
