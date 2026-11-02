using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class GuestConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "guest_configurations",
                columns: table => new
                {
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    desired_hash = table.Column<string>(type: "text", nullable: true),
                    desired_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    proposed_hash = table.Column<string>(type: "text", nullable: true),
                    applied_hash = table.Column<string>(type: "text", nullable: true),
                    applied_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    applied_action = table.Column<string>(type: "text", nullable: true),
                    snapshot = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guest_configurations", x => x.resource_id);
                    table.ForeignKey(
                        name: "fk_guest_configurations_resources_resource_id",
                        column: x => x.resource_id,
                        principalTable: "resources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "guest_configurations");
        }
    }
}
