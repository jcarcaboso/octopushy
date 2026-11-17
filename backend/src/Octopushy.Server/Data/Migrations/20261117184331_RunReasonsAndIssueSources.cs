using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RunReasonsAndIssueSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "block_reason",
                table: "runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "end_reason",
                table: "runs",
                type: "text",
                nullable: true);

            // What the server recognised in a run's words until now, recorded on the run. Each statement only fills what is
            // still empty, so running it again changes nothing.
            migrationBuilder.Sql(SupersededRuns);
            migrationBuilder.Sql(TrustBlockedRuns);
            foreach (var table in IssueTables)
                migrationBuilder.Sql(IssueSourcesOf(table));
        }

        /// <summary>
        /// A run cancelled for a later revision. One superseded while it restores a protection it lifted is not cancelled yet:
        /// its cancelled step carries the words, and the run ends with them.
        /// </summary>
        internal const string SupersededRuns = """
            UPDATE runs SET end_reason = 'superseded'
            WHERE end_reason IS NULL
              AND (status = 'cancelled' AND detail LIKE 'Superseded by %'
                   OR status NOT IN ('succeeded', 'failed', 'cancelled')
                      AND EXISTS (SELECT 1 FROM steps s WHERE s.run_id = runs.id AND s.status = 'cancelled' AND s.detail LIKE 'Superseded by %'));
            """;

        /// <summary>A run blocked until an operator trusts its installation, or the endpoint it would send credentials to.</summary>
        internal const string TrustBlockedRuns = """
            UPDATE runs SET block_reason = 'installation-trust'
            WHERE block_reason IS NULL AND status = 'blocked' AND detail LIKE 'Waiting for trust%';
            """;

        /// <summary>Every stored issue of <paramref name="table"/> gets its source: a workflow schema's error is the one whose words name the workflow.</summary>
        internal static string IssueSourcesOf(string table) => $$"""
            UPDATE {{table}} SET issues = (
                SELECT jsonb_agg(CASE
                    WHEN jsonb_typeof(e) <> 'object' OR e ? 'source' THEN e
                    WHEN e->>'message' LIKE '%(workflow %' THEN e || '{"source": "workflow-schema"}'
                    ELSE e || '{"source": "definition"}' END ORDER BY i)
                FROM jsonb_array_elements(issues) WITH ORDINALITY AS t(e, i))
            WHERE jsonb_typeof(issues) = 'array'
              AND EXISTS (SELECT 1 FROM jsonb_array_elements(issues) AS t(e) WHERE jsonb_typeof(e) = 'object' AND NOT e ? 'source');
            """;

        internal static readonly string[] IssueTables = ["resources", "revisions", "revision_issues"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in IssueTables)
            {
                migrationBuilder.Sql($$"""
                    UPDATE {{table}} SET issues = (
                        SELECT jsonb_agg(CASE WHEN jsonb_typeof(e) = 'object' THEN e - 'source' ELSE e END ORDER BY i)
                        FROM jsonb_array_elements(issues) WITH ORDINALITY AS t(e, i))
                    WHERE jsonb_typeof(issues) = 'array'
                      AND EXISTS (SELECT 1 FROM jsonb_array_elements(issues) AS t(e) WHERE jsonb_typeof(e) = 'object' AND e ? 'source');
                    """);
            }

            migrationBuilder.DropColumn(
                name: "block_reason",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "end_reason",
                table: "runs");
        }
    }
}
