using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "update_applied_at",
                table: "agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "update_change_id",
                table: "agents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "update_detail",
                table: "agents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "update_paused",
                table: "agents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "update_requested_at",
                table: "agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "update_requested_by",
                table: "agents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "update_state",
                table: "agents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "update_target_version",
                table: "agents",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "update_applied_at",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "update_change_id",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "update_detail",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "update_paused",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "update_requested_at",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "update_requested_by",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "update_state",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "update_target_version",
                table: "agents");
        }
    }
}
