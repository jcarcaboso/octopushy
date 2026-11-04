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

            // The work each resource wants now is one occurrence, and the run that stands for it, by the rules before
            // occurrences existed, carries it: a retire run made since a deleted guest went missing, or a run with those inputs
            // for the revision that recorded them. Work with no such run is proposed at the next sync.
            migrationBuilder.Sql("""
                UPDATE resources SET desired_occurrence = gen_random_uuid() WHERE desired_inputs_hash IS NOT NULL;
                UPDATE runs SET occurrence = s.desired_occurrence
                FROM resources s
                WHERE runs.resource_id = s.id AND runs.inputs_hash = s.desired_inputs_hash
                  AND ((s.missing_since IS NOT NULL AND runs.operation = 'retire' AND runs.created_at >= s.missing_since)
                    OR (s.missing_since IS NULL AND runs.operation IN ('provision', 'retire') AND runs.revision_id = s.desired_revision_id));
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
