using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChangeOccurrences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "occurrence",
                table: "runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "desired_occurrence",
                table: "resources",
                type: "uuid",
                nullable: true);

            // The work each resource wants now is one occurrence, and only the run that clearly stood for it carries it: the
            // latest run matching it by the rules before occurrences existed (a retire run made since a deleted guest went
            // missing, or a run with those inputs for the revision that recorded them), and only when no provision or retire
            // run with other inputs came after it. After a rollback to the same commit, the original run is history, not
            // this work: it stays unstamped, and the next sync proposes the work again.
            migrationBuilder.Sql("""
                UPDATE resources SET desired_occurrence = gen_random_uuid() WHERE desired_inputs_hash IS NOT NULL;
                WITH matching AS (
                    SELECT runs.id, runs.number, runs.resource_id
                    FROM runs JOIN resources s ON runs.resource_id = s.id
                    WHERE runs.inputs_hash = s.desired_inputs_hash
                      AND ((s.missing_since IS NOT NULL AND runs.operation = 'retire' AND runs.created_at >= s.missing_since)
                        OR (s.missing_since IS NULL AND runs.operation IN ('provision', 'retire') AND runs.revision_id = s.desired_revision_id))
                ), latest AS (
                    SELECT DISTINCT ON (resource_id) id, number, resource_id FROM matching ORDER BY resource_id, number DESC
                )
                UPDATE runs SET occurrence = s.desired_occurrence
                FROM latest JOIN resources s ON s.id = latest.resource_id
                WHERE runs.id = latest.id
                  AND NOT EXISTS (
                    SELECT 1 FROM runs later
                    WHERE later.resource_id = latest.resource_id AND later.number > latest.number
                      AND later.operation IN ('provision', 'retire') AND later.inputs_hash IS DISTINCT FROM s.desired_inputs_hash);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "occurrence",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "desired_occurrence",
                table: "resources");
        }
    }
}
