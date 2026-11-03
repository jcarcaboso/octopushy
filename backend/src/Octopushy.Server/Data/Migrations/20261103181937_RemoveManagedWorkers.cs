using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveManagedWorkers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "managed_workers");

            migrationBuilder.DropColumn(
                name: "worker_update_sha256",
                table: "steps");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "worker_update_sha256",
                table: "steps",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "managed_workers",
                columns: table => new
                {
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    authenticated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ca_certificate_pem = table.Column<string>(type: "text", nullable: true),
                    certificate_pem = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    csr_der = table.Column<string>(type: "text", nullable: true),
                    generation = table.Column<Guid>(type: "uuid", nullable: false),
                    host = table.Column<string>(type: "text", nullable: false),
                    installation = table.Column<string>(type: "text", nullable: false),
                    installed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    prepare_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    prepared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ready_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_url = table.Column<string>(type: "text", nullable: false),
                    verified_os_image_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    verified_os_update_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    vmid = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_managed_workers", x => x.resource_id);
                    table.ForeignKey(
                        name: "fk_managed_workers_resources_resource_id",
                        column: x => x.resource_id,
                        principalTable: "resources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_managed_workers_agent_id",
                table: "managed_workers",
                column: "agent_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_managed_workers_generation",
                table: "managed_workers",
                column: "generation",
                unique: true);
        }
    }
}
