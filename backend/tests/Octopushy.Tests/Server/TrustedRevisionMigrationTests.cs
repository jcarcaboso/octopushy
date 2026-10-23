using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Octopushy.Server.Data;
using Octopushy.Server.Git;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class TrustedRevisionMigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Forward_migration_preserves_evidence_but_grants_no_revision_or_legacy_run_authority()
    {
        await using var db = new OctopushyDb(new DbContextOptionsBuilder<OctopushyDb>()
            .UseNpgsql(await postgres.NewDatabaseAsync()).UseSnakeCaseNamingConvention().Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261021180000_CredentialLifecycle");
        var revisionId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var commit = new string('a', 40);
        var context = "{}";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO repository_settings (id, url, branch, poll_seconds, credential_kind, head_commit, updated_at)
            VALUES (1, '/synthetic/old-repository', 'main', 60, 'none', {commit}, {now});
            INSERT INTO revisions (id, commit, loaded_at, repository_valid, error_count, warning_count, issues)
            VALUES ({revisionId}, {commit}, {now}, TRUE, 0, 0, '[]');
            INSERT INTO runs (id, resource_id, operation, revision_id, commit, reason, status, context, created_at)
            VALUES ({runId}, {resourceId}, 'provision', {revisionId}, {commit}, 'manual', 'queued', {context}::jsonb, {now});
            INSERT INTO secret_resolutions (key, version, attempt_id, agent_id, resolved_at)
            VALUES ('fixture/token', 1, {Guid.NewGuid()}, {Guid.NewGuid()}, {now});
            """);
        await migrator.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        var revision = await db.Revisions.SingleAsync();
        Assert.Null(revision.SourceId);
        Assert.Null(revision.TrustedAt);
        Assert.Null(revision.TrustedBy);
        var settings = await db.RepositorySettings.SingleAsync();
        Assert.NotEqual(Guid.Empty, settings.SourceId);
        Assert.Null(settings.HeadCommit);
        Assert.Empty(await RepositoryTrust.EligibleRevisions(db).ToListAsync());
        var run = await db.Runs.SingleAsync();
        Assert.Equal(revisionId, run.RevisionId);
        Assert.Equal(commit, run.Commit);
        Assert.Contains("Waiting for trust", await RepositoryTrust.BlockedReasonAsync(db, run, CancellationToken.None));
        Assert.Equal(1, await db.SecretResolutions.CountAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => migrator.MigrateAsync("20261021180000_CredentialLifecycle"));
    }
}
