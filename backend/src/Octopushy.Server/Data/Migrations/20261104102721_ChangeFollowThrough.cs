using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChangeFollowThrough : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "edited",
                table: "changes",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "superseded_by",
                table: "changes",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_changes_status",
                table: "changes",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_changes_status",
                table: "changes");

            migrationBuilder.DropColumn(
                name: "edited",
                table: "changes");

            migrationBuilder.DropColumn(
                name: "superseded_by",
                table: "changes");
        }
    }
}
