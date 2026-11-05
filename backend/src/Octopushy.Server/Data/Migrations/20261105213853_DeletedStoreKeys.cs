using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeletedStoreKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "deleted_store_keys",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    latest_version = table.Column<int>(type: "integer", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deleted_store_keys", x => x.key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deleted_store_keys");
        }
    }
}
