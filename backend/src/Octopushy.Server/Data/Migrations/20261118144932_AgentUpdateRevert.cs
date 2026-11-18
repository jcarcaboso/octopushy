using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentUpdateRevert : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "update_applied_at",
                table: "agents",
                newName: "update_released_at");

            migrationBuilder.AddColumn<Guid>(
                name: "update_baseline_connection",
                table: "agents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "update_failure",
                table: "agents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "update_hold",
                table: "agents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "update_hold_attempt_id",
                table: "agents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "update_previous_executors",
                table: "agents",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "update_previous_version",
                table: "agents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "update_resource_id",
                table: "agents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "update_revert",
                table: "agents",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "update_baseline_connection",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "update_failure",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "update_hold",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "update_hold_attempt_id",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "update_previous_executors",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "update_previous_version",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "update_resource_id",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "update_revert",
                table: "agents");

            migrationBuilder.RenameColumn(
                name: "update_released_at",
                table: "agents",
                newName: "update_applied_at");
        }
    }
}
