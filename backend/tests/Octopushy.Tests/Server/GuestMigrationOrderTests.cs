using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Octopushy.Server.Data;

namespace Octopushy.Tests.Server;

/// <summary>
/// The last migration of 0.3.0 is the guest configuration. After it the keys, the schedules,
/// the rotation and the service pipelines run in that order, so a database reaches this version
/// straight from 0.3.0, or through a main that stopped at the schedules.
/// </summary>
[Collection(PostgresCollection.Name)]
public class GuestMigrationOrderTests(PostgresFixture postgres)
{
    private const string Release = "20261101205430_GuestConfiguration";
    private const string AccessKeys = "20261102121056_GuestAccessKeys";
    private const string Schedules = "20261102164720_GuestSchedules";
    private const string Rotation = "20261102173935_GuestAccessRotation";
    private const string Pipelines = "20261103102343_ServicePipelines";

    private static async Task<Guid> ReleasedGuestAsync(OctopushyDb db)
    {
        var id = Guid.NewGuid();
        var definition = "{}";
        await db.GetService<IMigrator>().MigrateAsync(Release);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO resources (id, kind, address, lab, definition, definition_file, issues, valid, first_seen_at)
            VALUES ({id}, 'guest', 'home/korriban/krb-app-01', 'home', {definition}::jsonb, 'guest.yaml', '[]'::jsonb, TRUE, {DateTimeOffset.UtcNow});
            """);
        return id;
    }

    private static async Task AssertCurrentAsync(OctopushyDb db)
    {
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        Assert.Contains(AccessKeys, applied);
        Assert.Contains(Rotation, applied);
        Assert.Contains(Schedules, applied);
        Assert.Contains(Pipelines, applied);
    }

    /// <summary>Both tables, with every column the running server reads and writes.</summary>
    private static async Task AssertUsableAsync(OctopushyDb db, Guid guest)
    {
        var now = DateTimeOffset.UtcNow;
        db.GuestAccessKeys.Add(new GuestAccessKey { ResourceId = guest, Version = 2, PublicKey = "ssh-ed25519 AAAA", Fingerprint = "SHA256:new", CreatedAt = now, AuthorizedAt = now, VerifiedAt = now, PromotedAt = now, PromotedBy = "ops", RemovedAt = now });
        db.GuestSchedules.Add(new GuestScheduleState { ResourceId = guest, Action = "update-system", Cron = "30 6 * * *", LastSlot = now });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_database_on_the_last_release_reaches_this_version_directly()
    {
        await using var db = await NewDbAsync();
        var guest = await ReleasedGuestAsync(db);

        await db.GetService<IMigrator>().MigrateAsync();

        await AssertCurrentAsync(db);
        await AssertUsableAsync(db, guest);
    }

    [Fact]
    public async Task A_database_that_took_the_schedules_first_still_takes_the_rotation()
    {
        await using var db = await NewDbAsync();
        var guest = await ReleasedGuestAsync(db);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(Schedules);
        Assert.Equal([Rotation, Pipelines], await db.Database.GetPendingMigrationsAsync());
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO guest_access_keys (resource_id, version, public_key, fingerprint, bootstrap, created_at)
            VALUES ({guest}, 1, 'ssh-ed25519 AAAA', 'SHA256:old', TRUE, {DateTimeOffset.UtcNow});
            INSERT INTO guest_schedules (resource_id, action, cron, last_slot)
            VALUES ({guest}, 'update-system', '0 6 * * *', {DateTimeOffset.UtcNow});
            """);

        await migrator.MigrateAsync();

        await AssertCurrentAsync(db);
        var key = await db.GuestAccessKeys.AsNoTracking().SingleAsync();
        Assert.Equal("SHA256:old", key.Fingerprint);
        Assert.Null(key.AuthorizedAt);
        Assert.Null(key.RemovedAt);
        Assert.Equal("0 6 * * *", (await db.GuestSchedules.AsNoTracking().SingleAsync()).Cron);
        await AssertUsableAsync(db, guest);
    }

    private async Task<OctopushyDb> NewDbAsync() =>
        new(new DbContextOptionsBuilder<OctopushyDb>().UseNpgsql(await postgres.NewDatabaseAsync()).UseSnakeCaseNamingConvention().Options);
}
