using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class UptimeChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "uptime_buckets",
                columns: table => new
                {
                    check_key = table.Column<string>(type: "character varying(2600)", maxLength: 2600, nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    up = table.Column<int>(type: "integer", nullable: false),
                    down = table.Column<int>(type: "integer", nullable: false),
                    latency_ms_total = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_uptime_buckets", x => new { x.check_key, x.starts_at });
                });

            migrationBuilder.CreateTable(
                name: "uptime_checks",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(2600)", maxLength: 2600, nullable: false),
                    up = table.Column<bool>(type: "boolean", nullable: false),
                    since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    elapsed_ms = table.Column<int>(type: "integer", nullable: true),
                    detail = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    agent = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_uptime_checks", x => x.key);
                });

            migrationBuilder.CreateIndex(
                name: "ix_uptime_buckets_starts_at",
                table: "uptime_buckets",
                column: "starts_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "uptime_buckets");

            migrationBuilder.DropTable(
                name: "uptime_checks");
        }
    }
}
