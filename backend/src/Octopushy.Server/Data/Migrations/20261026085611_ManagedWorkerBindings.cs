using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ManagedWorkerBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "managed_workers",
                columns: table => new
                {
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation = table.Column<Guid>(type: "uuid", nullable: false),
                    installation = table.Column<string>(type: "text", nullable: false),
                    host = table.Column<string>(type: "text", nullable: false),
                    vmid = table.Column<int>(type: "integer", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    prepare_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    csr_der = table.Column<string>(type: "text", nullable: true),
                    certificate_pem = table.Column<string>(type: "text", nullable: true),
                    ca_certificate_pem = table.Column<string>(type: "text", nullable: true),
                    server_url = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    prepared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    installed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    authenticated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ready_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("Managed worker identity evidence cannot be discarded safely. Restore a matched pre-upgrade database and key backup instead.");
    }
}
