using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowSourceTrust : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "confirmed_workflows_credential_key",
                table: "repository_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "confirmed_workflows_credential_version",
                table: "repository_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "confirmed_workflows_repository",
                table: "repository_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pending_workflows_commit",
                table: "repository_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pending_workflows_credential_key",
                table: "repository_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "pending_workflows_credential_version",
                table: "repository_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pending_workflows_repository",
                table: "repository_settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "workflows_confirmed_at",
                table: "repository_settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "workflows_confirmed_by",
                table: "repository_settings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "confirmed_workflows_credential_key",
                table: "repository_settings");

            migrationBuilder.DropColumn(
                name: "confirmed_workflows_credential_version",
                table: "repository_settings");

            migrationBuilder.DropColumn(
                name: "confirmed_workflows_repository",
                table: "repository_settings");

            migrationBuilder.DropColumn(
                name: "pending_workflows_commit",
                table: "repository_settings");

            migrationBuilder.DropColumn(
                name: "pending_workflows_credential_key",
                table: "repository_settings");

            migrationBuilder.DropColumn(
                name: "pending_workflows_credential_version",
                table: "repository_settings");

            migrationBuilder.DropColumn(
                name: "pending_workflows_repository",
                table: "repository_settings");

            migrationBuilder.DropColumn(
                name: "workflows_confirmed_at",
                table: "repository_settings");

            migrationBuilder.DropColumn(
                name: "workflows_confirmed_by",
                table: "repository_settings");
        }
    }
}
