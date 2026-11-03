using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Octopushy.Server.Data;

/// <summary>Why this release cannot use the database it was given.</summary>
public sealed class FreshDatabaseRequiredException(string message) : Exception(message);

/// <summary>
/// This release starts its schema again from one <c>Initial</c> migration. A database created by an
/// earlier release records migrations this one does not have, and migrating it would try to create
/// tables that already exist, or worse, run against a schema it does not know. So it is refused.
/// </summary>
public static class SchemaHistory
{
    /// <summary>Throws <see cref="FreshDatabaseRequiredException"/> when the database records a migration this release does not have.</summary>
    public static async Task RefuseUnknownMigrationsAsync(DatabaseFacade database, CancellationToken cancellationToken)
    {
        var known = database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var unknown = (await database.GetAppliedMigrationsAsync(cancellationToken)).Where(id => !known.Contains(id)).ToList();
        if (unknown.Count > 0)
        {
            throw new FreshDatabaseRequiredException(
                $"This database was created by an earlier release of Octopushy: it records {unknown.Count} migration(s) this release does not have, "
                + $"such as {unknown[0]}. This release requires a fresh database and cannot upgrade it. Nothing was changed. "
                + "Stop Octopushy, drop and recreate its PostgreSQL database (or volume), start it again and run setup; see deploy/README.md.");
        }
    }
}
