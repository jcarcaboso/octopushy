using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChangeRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "inventory_applied_at",
                table: "revisions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "candidate_commits",
                table: "changes",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "dismissed_at",
                table: "changes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "dismissed_by",
                table: "changes",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "revision_issues",
                columns: table => new
                {
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issues = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_revision_issues", x => new { x.resource_id, x.revision_id });
                });

            // Revisions loaded before the marker existed were applied when they were loaded.
            migrationBuilder.Sql("UPDATE revisions SET inventory_applied_at = loaded_at WHERE inventory_applied_at IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "revision_issues");

            migrationBuilder.DropColumn(
                name: "inventory_applied_at",
                table: "revisions");

            migrationBuilder.DropColumn(
                name: "candidate_commits",
                table: "changes");

            migrationBuilder.DropColumn(
                name: "dismissed_at",
                table: "changes");

            migrationBuilder.DropColumn(
                name: "dismissed_by",
                table: "changes");
        }
    }
}
