using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class BuiltInOpenTofuState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "state_version",
                table: "attempts",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tofu_states",
                columns: table => new
                {
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    head_version = table.Column<int>(type: "integer", nullable: false),
                    lock_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    locked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tofu_states", x => x.resource_id);
                    table.ForeignKey(
                        name: "fk_tofu_states_resources_resource_id",
                        column: x => x.resource_id,
                        principalTable: "resources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tofu_state_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    serial = table.Column<long>(type: "bigint", nullable: false),
                    lineage = table.Column<string>(type: "text", nullable: false),
                    resource_count = table.Column<int>(type: "integer", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    size = table.Column<int>(type: "integer", nullable: false),
                    ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    wrapped_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    restored_from = table.Column<int>(type: "integer", nullable: true),
                    restored_by = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tofu_state_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_tofu_state_versions_tofu_states_resource_id",
                        column: x => x.resource_id,
                        principalTable: "tofu_states",
                        principalColumn: "resource_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tofu_state_versions_resource_id_version",
                table: "tofu_state_versions",
                columns: new[] { "resource_id", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tofu_state_versions");

            migrationBuilder.DropTable(
                name: "tofu_states");

            migrationBuilder.DropColumn(
                name: "state_version",
                table: "attempts");
        }
    }
}
