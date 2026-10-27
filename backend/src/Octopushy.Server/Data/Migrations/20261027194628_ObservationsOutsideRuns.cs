using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ObservationsOutsideRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "observe_error",
                table: "resources",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "observe_error_at",
                table: "resources",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "observe_requested_at",
                table: "resources",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "step_id",
                table: "attempts",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "run_id",
                table: "attempts",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "context",
                table: "attempts",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "resource_id",
                table: "attempts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "revision_id",
                table: "attempts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_attempts_resource_id_created_at",
                table: "attempts",
                columns: new[] { "resource_id", "created_at" },
                filter: "resource_id IS NOT NULL");

            // Observations are no longer runs; their steps and attempts go with them.
            migrationBuilder.Sql("DELETE FROM runs WHERE operation = 'observe';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM attempts WHERE step_id IS NULL;");

            migrationBuilder.DropIndex(
                name: "ix_attempts_resource_id_created_at",
                table: "attempts");

            migrationBuilder.DropColumn(
                name: "observe_error",
                table: "resources");

            migrationBuilder.DropColumn(
                name: "observe_error_at",
                table: "resources");

            migrationBuilder.DropColumn(
                name: "observe_requested_at",
                table: "resources");

            migrationBuilder.DropColumn(
                name: "context",
                table: "attempts");

            migrationBuilder.DropColumn(
                name: "resource_id",
                table: "attempts");

            migrationBuilder.DropColumn(
                name: "revision_id",
                table: "attempts");

            migrationBuilder.AlterColumn<Guid>(
                name: "step_id",
                table: "attempts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "run_id",
                table: "attempts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
