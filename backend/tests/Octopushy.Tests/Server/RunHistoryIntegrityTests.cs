using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Octopushy.Server.Data;

namespace Octopushy.Tests.Server;

public class RunHistoryIntegrityTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private async Task<OctopushyDb> NewDbAsync() => new(new DbContextOptionsBuilder<OctopushyDb>()
        .UseNpgsql(await postgres.NewDatabaseAsync()).UseSnakeCaseNamingConvention().Options);

    [Fact]
    public async Task Upgrade_removes_all_three_kinds_of_orphan_and_keeps_observations()
    {
        await using var db = await NewDbAsync();
        await db.Database.GetService<IMigrator>().MigrateAsync("20261113082632_ServiceIcons");
        // The model no longer writes log_lines, which this schema still requires: it was always 0.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE attempts ALTER COLUMN log_lines SET DEFAULT 0");
        var orphan = NewAttempt();
        orphan.RunId = Guid.NewGuid();
        var observation = NewAttempt();
        db.Attempts.AddRange(orphan, observation);
        db.Artifacts.AddRange(NewArtifact(orphan.Id), NewArtifact(Guid.NewGuid()), NewArtifact(observation.Id));
        db.SecretResolutions.AddRange(NewResolution(orphan.Id), NewResolution(Guid.NewGuid()), NewResolution(observation.Id));
        await db.SaveChangesAsync();

        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(observation.Id, (await db.Attempts.SingleAsync()).Id);
        Assert.Equal(observation.Id, (await db.Artifacts.SingleAsync()).AttemptId);
        Assert.Equal(observation.Id, (await db.SecretResolutions.SingleAsync()).AttemptId);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Deleting_a_run_cascades_to_steps_attempts_artifacts_and_secret_releases()
    {
        await using var db = await NewDbAsync();
        await db.Database.MigrateAsync();
        var run = new Run { Id = Guid.NewGuid(), ResourceId = Guid.NewGuid(), Operation = "test", Reason = "manual" };
        var step = new Step { Id = Guid.NewGuid(), Run = run, Name = "test", Kind = StepKinds.Agent };
        var attempt = NewAttempt();
        attempt.RunId = run.Id;
        attempt.Step = step;
        // An attempt may refer to a run directly, without a step.
        var direct = NewAttempt();
        direct.RunId = run.Id;
        db.Runs.Add(run);
        db.Attempts.AddRange(attempt, direct);
        db.Artifacts.Add(NewArtifact(attempt.Id));
        db.SecretResolutions.Add(NewResolution(attempt.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await db.Runs.Where(r => r.Id == run.Id).ExecuteDeleteAsync();

        Assert.Empty(await db.Steps.ToListAsync());
        Assert.Empty(await db.Attempts.ToListAsync());
        Assert.Empty(await db.Artifacts.ToListAsync());
        Assert.Empty(await db.SecretResolutions.ToListAsync());
    }

    private static Attempt NewAttempt() => new() { Id = Guid.NewGuid(), Executor = "test", Action = "test" };
    private static ArtifactRecord NewArtifact(Guid attemptId) => new()
    {
        Id = Guid.NewGuid(),
        AttemptId = attemptId,
        Name = "plan",
        Sha256 = "digest",
    };
    private static SecretResolution NewResolution(Guid attemptId) => new()
    {
        AttemptId = attemptId,
        Key = "test/key",
        Version = 1,
        AgentId = Guid.NewGuid(),
    };
}
