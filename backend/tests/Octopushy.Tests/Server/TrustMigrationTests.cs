using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Octopushy.Server.Data;
using Octopushy.Server.Git;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class TrustMigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Forward_migration_preserves_evidence_but_grants_no_legacy_source_authority()
    {
        await using var db = await NewDbAsync();
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
        var settings = await db.RepositorySettings.SingleAsync();
        Assert.NotEqual(Guid.Empty, settings.SourceId);
        Assert.Null(settings.HeadCommit);
        Assert.Empty(await RepositoryTrust.EligibleRevisions(db).ToListAsync());
        Assert.Empty(await db.InstallationTrusts.ToListAsync());
        var run = await db.Runs.SingleAsync();
        Assert.Equal(revisionId, run.RevisionId);
        Assert.Equal(commit, run.Commit);
        Assert.Equal(1, await db.SecretResolutions.CountAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => migrator.MigrateAsync("20261021180000_CredentialLifecycle"));
    }

    [Fact]
    public async Task Installations_defined_by_a_trusted_revision_stay_trusted_and_trust_waits_are_rechecked()
    {
        await using var db = await NewDbAsync();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261028104029_RepositoryDrafts");
        var source = Guid.NewGuid();
        var trusted = Guid.NewGuid();
        var untrusted = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var cluster = """
            {"connection":{"endpoint":"https://10.1.0.10:8006/"},"hosts":{"a":{"endpoint":"https://10.1.0.11:8006/"},"b":{}}}
            """;
        var standalone = """{"connection":{"endpoint":"https://10.2.0.10:8006/"},"hosts":{"c":{}}}""";
        var waiting = Guid.NewGuid();
        var started = Guid.NewGuid();
        var other = Guid.NewGuid();
        var context = "{}";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO repository_settings (id, url, branch, source_id, poll_seconds, credential_kind, updated_at)
            VALUES (1, '/synthetic/repository', 'main', {source}, 60, 'none', {now});
            INSERT INTO revisions (id, source_id, commit, loaded_at, repository_valid, error_count, warning_count, issues, trusted_at, trusted_by)
            VALUES ({trusted}, {source}, {new string('a', 40)}, {now}, TRUE, 0, 0, '[]', {now}, 'ops'),
                   ({untrusted}, {source}, {new string('b', 40)}, {now}, TRUE, 0, 0, '[]', NULL, NULL);
            INSERT INTO resources (id, address, kind, lab, first_seen_at, valid, issues, definition, last_revision_id)
            VALUES ({Guid.NewGuid()}, 'home/korriban', 'installation', 'home', {now}, TRUE, '[]', {cluster}::jsonb, {trusted}),
                   ({Guid.NewGuid()}, 'home/moraband', 'installation', 'home', {now}, TRUE, '[]', {standalone}::jsonb, {untrusted});
            INSERT INTO runs (id, resource_id, operation, revision_id, commit, reason, status, detail, context, created_at, started_at)
            VALUES ({waiting}, {Guid.NewGuid()}, 'provision', {untrusted}, {new string('b', 40)}, 'revision', 'blocked', 'Waiting for trust: explicitly trust this exact revision.', {context}::jsonb, {now}, NULL),
                   ({started}, {Guid.NewGuid()}, 'provision', {untrusted}, {new string('b', 40)}, 'revision', 'blocked', 'Waiting for trust: explicitly trust this exact revision.', {context}::jsonb, {now}, {now}),
                   ({other}, {Guid.NewGuid()}, 'provision', {trusted}, {new string('a', 40)}, 'revision', 'blocked', 'Blocked credentials: fixture.', {context}::jsonb, {now}, NULL);
            """);
        await migrator.MigrateAsync();

        var trust = Assert.Single(await db.InstallationTrusts.ToListAsync());
        Assert.Equal("home/korriban", trust.Address);
        Assert.Equal(["https://10.1.0.10:8006/", "https://10.1.0.11:8006/"], trust.Endpoints);
        Assert.Equal("ops", trust.TrustedBy);
        var runs = await db.Runs.ToDictionaryAsync(r => r.Id);
        Assert.Equal((RunStatuses.Queued, (string?)null), (runs[waiting].Status, runs[waiting].Detail));
        Assert.Equal(RunStatuses.Running, runs[started].Status);
        Assert.Equal(RunStatuses.Blocked, runs[other].Status);
    }

    private async Task<OctopushyDb> NewDbAsync() =>
        new(new DbContextOptionsBuilder<OctopushyDb>().UseNpgsql(await postgres.NewDatabaseAsync()).UseSnakeCaseNamingConvention().Options);
}
