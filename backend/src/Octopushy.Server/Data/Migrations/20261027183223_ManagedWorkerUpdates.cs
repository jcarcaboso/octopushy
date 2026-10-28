using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ManagedWorkerUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "worker_update_sha256",
                table: "steps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "draining",
                table: "agents",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Worker OS approval and drain evidence must be preserved. Restore a matched pre-upgrade database and key backup instead.");
        }
    }
}
