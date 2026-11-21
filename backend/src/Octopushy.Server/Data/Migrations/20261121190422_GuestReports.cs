using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class GuestReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "peer_reports",
                table: "guest_configurations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reports",
                table: "guest_configurations",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "peer_reports",
                table: "guest_configurations");

            migrationBuilder.DropColumn(
                name: "reports",
                table: "guest_configurations");
        }
    }
}
