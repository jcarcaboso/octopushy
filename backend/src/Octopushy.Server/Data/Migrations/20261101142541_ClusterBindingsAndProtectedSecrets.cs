using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClusterBindingsAndProtectedSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "protected",
                table: "store_entries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "protected_at",
                table: "store_entries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "protected_by",
                table: "store_entries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "block_lists_refreshed",
                table: "resources",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "member_certificates",
                table: "resources",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "certificates",
                table: "cluster_trusts",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "cluster_bindings",
                columns: table => new
                {
                    cluster_id = table.Column<Guid>(type: "uuid", nullable: false),
                    guest_id = table.Column<Guid>(type: "uuid", nullable: false),
                    guest = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    address = table.Column<string>(type: "text", nullable: false),
                    peer_id = table.Column<long>(type: "bigint", nullable: true),
                    peer_name = table.Column<string>(type: "text", nullable: true),
                    bound_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    detached_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    detached_by = table.Column<string>(type: "text", nullable: true),
                    detach_note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cluster_bindings", x => new { x.cluster_id, x.guest_id });
                    table.ForeignKey(
                        name: "fk_cluster_bindings_resources_cluster_id",
                        column: x => x.cluster_id,
                        principalTable: "resources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cluster_bindings_resources_guest_id",
                        column: x => x.guest_id,
                        principalTable: "resources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "secret_grants",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_digest = table.Column<string>(type: "text", nullable: true),
                    granted_by = table.Column<string>(type: "text", nullable: false),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_secret_grants", x => new { x.key, x.resource_id });
                    table.ForeignKey(
                        name: "fk_secret_grants_resources_resource_id",
                        column: x => x.resource_id,
                        principalTable: "resources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cluster_bindings_guest_id",
                table: "cluster_bindings",
                column: "guest_id");

            migrationBuilder.CreateIndex(
                name: "ix_secret_grants_resource_id",
                table: "secret_grants",
                column: "resource_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cluster_bindings");

            migrationBuilder.DropTable(
                name: "secret_grants");

            migrationBuilder.DropColumn(
                name: "protected",
                table: "store_entries");

            migrationBuilder.DropColumn(
                name: "protected_at",
                table: "store_entries");

            migrationBuilder.DropColumn(
                name: "protected_by",
                table: "store_entries");

            migrationBuilder.DropColumn(
                name: "block_lists_refreshed",
                table: "resources");

            migrationBuilder.DropColumn(
                name: "member_certificates",
                table: "resources");

            migrationBuilder.DropColumn(
                name: "certificates",
                table: "cluster_trusts");
        }
    }
}
