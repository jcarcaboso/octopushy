using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Octopushy.Server.Data;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class SchemaHistoryTests(PostgresFixture postgres)
{
    private async Task<OctopushyDb> NewDbAsync() => new(new DbContextOptionsBuilder<OctopushyDb>()
        .UseNpgsql(await postgres.NewDatabaseAsync()).UseSnakeCaseNamingConvention().Options);

    [Fact]
    public async Task An_empty_database_is_migrated_from_the_initial_migration_of_0_5_0()
    {
        await using var db = await NewDbAsync();
        await SchemaHistory.RefuseUnknownMigrationsAsync(db.Database, CancellationToken.None);
        await db.Database.MigrateAsync();
        // 0.5.0 squashed everything into Initial; what follows it is additive.
        Assert.Equal("20261103191532_Initial", db.Database.GetMigrations().First());
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        await SchemaHistory.RefuseUnknownMigrationsAsync(db.Database, CancellationToken.None);
    }

    [Fact]
    public async Task A_database_from_0_5_0_is_upgraded_by_the_migrations_after_initial()
    {
        await using var db = await NewDbAsync();
        await db.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync("20261103191532_Initial");
        Assert.Equal(["20261103191532_Initial"], await db.Database.GetAppliedMigrationsAsync());
        await SchemaHistory.RefuseUnknownMigrationsAsync(db.Database, CancellationToken.None);
        await db.Database.MigrateAsync();
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(0, await db.Changes.CountAsync());
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
