using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class InstallationTrust : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "installation_trusts",
                columns: table => new
                {
                    address = table.Column<string>(type: "text", nullable: false),
                    endpoints = table.Column<List<string>>(type: "text[]", nullable: false),
                    trusted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    trusted_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_installation_trusts", x => x.address);
                });

            // An installation whose current definition came from a trusted revision keeps its
            // endpoints trusted, by the same operator; any other waits for an explicit trust.
            migrationBuilder.Sql("""
                INSERT INTO installation_trusts (address, endpoints, trusted_at, trusted_by)
                SELECT r.address,
                       ARRAY(SELECT DISTINCT e.endpoint COLLATE "C" FROM (
                               SELECT r.definition -> 'connection' ->> 'endpoint' AS endpoint
                               UNION
                               SELECT h.value ->> 'endpoint' FROM jsonb_each(CASE WHEN jsonb_typeof(r.definition -> 'hosts') = 'object'
                                   THEN r.definition -> 'hosts' ELSE '{}'::jsonb END) h) e
                             WHERE e.endpoint IS NOT NULL ORDER BY 1),
                       v.trusted_at, v.trusted_by
                FROM resources r JOIN revisions v ON v.id = r.last_revision_id
                WHERE r.kind = 'installation' AND r.retired_at IS NULL AND r.missing_since IS NULL AND r.valid
                  AND r.definition IS NOT NULL AND v.trusted_at IS NOT NULL AND v.trusted_by IS NOT NULL;
                """);

            // Runs waiting for a revision's trust are checked again against their installation's.
            migrationBuilder.Sql("""
                UPDATE runs SET status = CASE WHEN started_at IS NULL THEN 'queued' ELSE 'running' END, detail = NULL
                WHERE status = 'blocked' AND detail LIKE 'Waiting for trust%';
                """);

            migrationBuilder.DropColumn(
                name: "trusted_at",
                table: "revisions");

            migrationBuilder.DropColumn(
                name: "trusted_by",
                table: "revisions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "installation_trusts");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "trusted_at",
                table: "revisions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "trusted_by",
                table: "revisions",
                type: "text",
                nullable: true);
        }
    }
}
