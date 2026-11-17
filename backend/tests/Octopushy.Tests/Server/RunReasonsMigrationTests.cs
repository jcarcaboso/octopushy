using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Data.Migrations;
using Octopushy.Server.Inventory;

namespace Octopushy.Tests.Server;

/// <summary>
/// The upgrade records on existing rows what the server used to recognise in their words: a superseded run, a run blocked
/// for an installation's trust, and the source of every stored issue. Rows written at the previous schema, with the raw SQL
/// that schema takes.
/// </summary>
public class RunReasonsMigrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Previous = "20261117104206_SessionStamps";

    private async Task<OctopushyDb> NewDbAsync() => new(new DbContextOptionsBuilder<OctopushyDb>()
        .UseNpgsql(await postgres.NewDatabaseAsync()).UseSnakeCaseNamingConvention().Options);

    [Fact]
    public async Task Upgrade_records_why_existing_runs_ended_or_wait_from_their_words()
    {
        await using var db = await NewDbAsync();
        await db.Database.GetService<IMigrator>().MigrateAsync(Previous);
        var superseded = await InsertRunAsync(db, "cancelled", "Superseded by 0123456789.");
        var supersededProtecting = await InsertRunAsync(db, "cancelled",
            "Superseded by 0123456789. The protection of VM 101 was restored.");
        // Superseded while it restores the protection it lifted: the run goes on, its cancelled step has the words.
        var restoring = await InsertRunAsync(db, "running", "Restoring the protection of VM 101: the run was cancelled.");
        await InsertStepAsync(db, restoring, "cancelled", "Superseded by 0123456789.");
        var cancelled = await InsertRunAsync(db, "cancelled", "Cancelled by alice.");
        var rejected = await InsertRunAsync(db, "cancelled", "Rejected by alice.");
        // Words alone, on a run that did not end, never made it superseded.
        var unfinished = await InsertRunAsync(db, "running", "Superseded by nothing yet");
        var otherStep = await InsertRunAsync(db, "running", null);
        await InsertStepAsync(db, otherStep, "cancelled", "The run was cancelled.");
        var trust = await InsertRunAsync(db, "blocked", "Waiting for trust: lab/pve is a new Proxmox installation. Trust it on its page.");
        var endpoint = await InsertRunAsync(db, "blocked", "Waiting for trust: https://pve:8006 is a new endpoint for lab/pve.");
        var credentials = await InsertRunAsync(db, "blocked", "Blocked credentials: lab/token.");
        var source = await InsertRunAsync(db, "blocked", "Waiting for the current repository source: this work was built from a previous source.");
        // A run no longer blocked was not waiting for trust, whatever its words.
        var queued = await InsertRunAsync(db, "queued", "Waiting for trust: lab/pve is a new Proxmox installation.");

        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();

        var runs = await db.Runs.AsNoTracking().ToDictionaryAsync(r => r.Id);
        Assert.Equal(RunEndReasons.Superseded, runs[superseded].EndReason);
        Assert.Equal(RunEndReasons.Superseded, runs[supersededProtecting].EndReason);
        Assert.Equal(RunEndReasons.Superseded, runs[restoring].EndReason);
        Assert.All(new[] { cancelled, rejected, unfinished, otherStep, trust, credentials }, id => Assert.Null(runs[id].EndReason));
        Assert.Equal(RunBlockReasons.InstallationTrust, runs[trust].BlockReason);
        Assert.Equal(RunBlockReasons.InstallationTrust, runs[endpoint].BlockReason);
        Assert.All(new[] { credentials, source, queued, superseded }, id => Assert.Null(runs[id].BlockReason));
        // The words stay as they were.
        Assert.Equal("Superseded by 0123456789.", runs[superseded].Detail);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Upgrade_gives_every_stored_issue_its_source_and_can_run_twice()
    {
        await using var db = await NewDbAsync();
        await db.Database.GetService<IMigrator>().MigrateAsync(Previous);
        // As the server stored them before sources: camelCase, the severity a number, no source.
        const string issues = """
            [{"severity": 0, "file": "lab/pve/web/guest.yaml", "line": 4, "message": "hardware/memory: must be at least 512 (workflow fedora-vm@2.3)", "address": "lab/pve/web"},
             {"severity": 0, "file": "lab/pve/web/guest.yaml", "line": 2, "message": "The template 'fedora' is not defined.", "address": "lab/pve/web"},
             {"severity": 1, "file": "lab/pve/web/guest.yaml", "line": null, "message": "A workflow (workflowless) note.", "address": null}]
            """;
        var resource = Guid.NewGuid();
        var revision = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO resources (id, address, kind, lab, issues, first_seen_at, observed_only, valid, observed_only_store_keys, observed_only_endpoints)
            VALUES ({0}, 'lab/pve/web', 'guest', 'lab', {1}::jsonb, now(), false, false, '{{}}', '{{}}');
            INSERT INTO revisions (id, commit, issues, resolutions, workflows_digest, loaded_at, repository_valid, error_count, warning_count, source_id)
            VALUES ({2}, 'abc', {1}::jsonb, '[]', '', now(), false, 2, 1, {3});
            INSERT INTO revision_issues (resource_id, revision_id, issues) VALUES ({0}, {2}, {1}::jsonb);
            INSERT INTO resources (id, address, kind, lab, issues, first_seen_at, observed_only, valid, observed_only_store_keys, observed_only_endpoints)
            VALUES ({4}, 'lab/pve/db', 'guest', 'lab', '[]', now(), false, true, '{{}}', '{{}}');
            """, resource, issues, revision, Guid.NewGuid(), Guid.NewGuid());

        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();

        var stored = new[]
        {
            (await db.Resources.AsNoTracking().SingleAsync(r => r.Id == resource)).Issues,
            (await db.Revisions.AsNoTracking().SingleAsync(r => r.Id == revision)).Issues,
            (await db.RevisionIssues.AsNoTracking().SingleAsync()).Issues,
        };
        foreach (var json in stored)
        {
            var read = JsonSerializer.Deserialize<List<DefinitionIssue>>(json, InventoryUpdater.IssueJson)!;
            Assert.Equal([IssueSources.WorkflowSchema, IssueSources.Definition, IssueSources.Definition], read.Select(i => i.Source));
            Assert.Equal("hardware/memory: must be at least 512 (workflow fedora-vm@2.3)", read[0].Message);
            Assert.Equal(4, read[0].Line);
        }
        Assert.Equal("[]", (await db.Resources.AsNoTracking().SingleAsync(r => r.Id != resource)).Issues);

        // Applied again, every statement finds nothing left to fill.
        Assert.Equal(0, await db.Database.ExecuteSqlRawAsync(RunReasonsAndIssueSources.SupersededRuns));
        Assert.Equal(0, await db.Database.ExecuteSqlRawAsync(RunReasonsAndIssueSources.TrustBlockedRuns));
        foreach (var table in RunReasonsAndIssueSources.IssueTables)
            Assert.Equal(0, await db.Database.ExecuteSqlRawAsync(RunReasonsAndIssueSources.IssueSourcesOf(table).Replace("{", "{{").Replace("}", "}}")));

        // Down leaves the issues as the previous version stored them.
        await db.Database.GetService<IMigrator>().MigrateAsync(Previous);
        var issuesNow = await db.Database.SqlQueryRaw<string>("SELECT issues::text AS \"Value\" FROM revision_issues").SingleAsync();
        Assert.DoesNotContain("source", issuesNow);
        Assert.Contains("(workflow fedora-vm@2.3)", issuesNow);
    }

    [Fact]
    public void An_issue_stored_without_a_source_reads_as_the_definitions_own()
    {
        var read = JsonSerializer.Deserialize<List<DefinitionIssue>>(
            """[{"severity": 0, "file": "a.yaml", "line": 1, "message": "x (workflow y@1)", "address": null}]""", InventoryUpdater.IssueJson)!;
        Assert.Equal(IssueSources.Definition, Assert.Single(read).Source);
    }

    private static async Task<Guid> InsertRunAsync(OctopushyDb db, string status, string? detail)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($$"""
            INSERT INTO runs (id, resource_id, operation, reason, status, detail, context, created_at)
            VALUES ({{id}}, {{Guid.NewGuid()}}, 'provision', 'revision', {{status}}, NULLIF({{detail ?? ""}}, ''), '{}', now());
            """);
        return id;
    }

    private static Task InsertStepAsync(OctopushyDb db, Guid run, string status, string detail) =>
        db.Database.ExecuteSqlRawAsync("""
            INSERT INTO steps (id, run_id, name, kind, status, detail, ordinal, approval_required)
            VALUES ({0}, {1}, 'apply', 'agent', {2}, {3}, 1, false);
            """, Guid.NewGuid(), run, status, detail);
}
