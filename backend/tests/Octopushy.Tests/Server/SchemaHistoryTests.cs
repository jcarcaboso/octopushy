using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class SchemaHistoryTests(PostgresFixture postgres)
{
    private async Task<OctopushyDb> NewDbAsync() => new(new DbContextOptionsBuilder<OctopushyDb>()
        .UseNpgsql(await postgres.NewDatabaseAsync()).UseSnakeCaseNamingConvention().Options);

    [Fact]
    public async Task An_empty_database_is_migrated_to_the_one_initial_migration()
    {
        await using var db = await NewDbAsync();
        await SchemaHistory.RefuseUnknownMigrationsAsync(db.Database, CancellationToken.None);
        await db.Database.MigrateAsync();
        Assert.Single(db.Database.GetMigrations());
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        await SchemaHistory.RefuseUnknownMigrationsAsync(db.Database, CancellationToken.None);
    }

    [Fact]
    public async Task A_database_from_an_earlier_release_is_refused_untouched()
    {
        await using var db = await NewDbAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "__EFMigrationsHistory" (migration_id varchar(150) PRIMARY KEY, product_version varchar(32) NOT NULL);
            INSERT INTO "__EFMigrationsHistory" VALUES ('20261020083859_Initial', '10.0.0'), ('20261103183239_RemoveDiskReporter', '10.0.0');
            CREATE TABLE agents (id uuid PRIMARY KEY);
            """);

        var error = await Assert.ThrowsAsync<FreshDatabaseRequiredException>(() => SchemaHistory.RefuseUnknownMigrationsAsync(db.Database, CancellationToken.None));
        Assert.Contains("requires a fresh database", error.Message);
        Assert.Contains("20261020083859_Initial", error.Message);
        Assert.Equal(["20261020083859_Initial", "20261103183239_RemoveDiskReporter"], await db.Database.GetAppliedMigrationsAsync());
    }
}
