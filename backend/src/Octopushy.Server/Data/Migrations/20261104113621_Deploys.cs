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
            migrationBuilder.AlterColumn<Guid>(
                name: "resource_id",
                table: "changes",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<List<string>>(
                name: "generated_keys",
                table: "changes",
                type: "text[]",
                nullable: false,
                // Changes made before deploys generated nothing.
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<List<Guid>>(
                name: "generated_tokens",
                table: "changes",
                type: "uuid[]",
                nullable: false,
                // Changes made before deploys generated nothing.
                defaultValueSql: "'{}'");

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

            migrationBuilder.AddColumn<int>(
                name: "vmid",
                table: "changes",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "generated_keys",
                table: "changes");

            migrationBuilder.DropColumn(
                name: "generated_tokens",
                table: "changes");

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
