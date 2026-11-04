using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Deploys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "generation_id",
                table: "store_versions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "resource_id",
                table: "changes",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "pre_approval",
                table: "changes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pre_approval_detail",
                table: "changes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "pre_approval_run_id",
                table: "changes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "store_keys",
                table: "changes",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "vmid",
                table: "changes",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "generated_values",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    generator = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    store_version = table.Column<int>(type: "integer", nullable: true),
                    store_description = table.Column<string>(type: "text", nullable: false),
                    token_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cleanup_pending = table.Column<bool>(type: "boolean", nullable: false),
                    cleanup_error = table.Column<string>(type: "text", nullable: true),
                    cleanup_attempts = table.Column<int>(type: "integer", nullable: false),
                    detail = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_generated_values", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_generated_values_change_id",
                table: "generated_values",
                column: "change_id");

            migrationBuilder.CreateIndex(
                name: "ix_generated_values_state_key",
                table: "generated_values",
                columns: new[] { "state", "key" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "generated_values");

            migrationBuilder.DropColumn(
                name: "generation_id",
                table: "store_versions");

            migrationBuilder.DropColumn(
                name: "pre_approval",
                table: "changes");

            migrationBuilder.DropColumn(
                name: "pre_approval_detail",
                table: "changes");

            migrationBuilder.DropColumn(
                name: "pre_approval_run_id",
                table: "changes");

            migrationBuilder.DropColumn(
                name: "store_keys",
                table: "changes");

            migrationBuilder.DropColumn(
                name: "vmid",
                table: "changes");

            migrationBuilder.AlterColumn<Guid>(
                name: "resource_id",
                table: "changes",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
