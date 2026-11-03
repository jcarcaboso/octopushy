using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class GuestAccessRotation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "authorized_at",
                table: "guest_access_keys",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "promoted_at",
                table: "guest_access_keys",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "promoted_by",
                table: "guest_access_keys",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "promotion_evidence",
                table: "guest_access_keys",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "removed_at",
                table: "guest_access_keys",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "verified_at",
                table: "guest_access_keys",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "verified_run_id",
                table: "guest_access_keys",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "authorized_at",
                table: "guest_access_keys");

            migrationBuilder.DropColumn(
                name: "promoted_at",
                table: "guest_access_keys");

            migrationBuilder.DropColumn(
                name: "promoted_by",
                table: "guest_access_keys");

            migrationBuilder.DropColumn(
                name: "promotion_evidence",
                table: "guest_access_keys");

            migrationBuilder.DropColumn(
                name: "removed_at",
                table: "guest_access_keys");

            migrationBuilder.DropColumn(
                name: "verified_at",
                table: "guest_access_keys");

            migrationBuilder.DropColumn(
                name: "verified_run_id",
                table: "guest_access_keys");
        }
    }
}
