using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClusterTrust : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bound_cluster_domain",
                table: "resources",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bound_primary",
                table: "resources",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cluster_trusts",
                columns: table => new
                {
                    address = table.Column<string>(type: "text", nullable: false),
                    endpoints = table.Column<List<string>>(type: "text[]", nullable: false),
                    code_digest = table.Column<string>(type: "text", nullable: false),
                    trusted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    trusted_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cluster_trusts", x => x.address);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cluster_trusts");

            migrationBuilder.DropColumn(
                name: "bound_cluster_domain",
                table: "resources");

            migrationBuilder.DropColumn(
                name: "bound_primary",
                table: "resources");
        }
    }
}
