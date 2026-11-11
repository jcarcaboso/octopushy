using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class BuiltTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "provision_waiting",
                table: "resources",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "image_builds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    template_id = table.Column<string>(type: "text", nullable: false),
                    workflow_id = table.Column<string>(type: "text", nullable: false),
                    workflow_version = table.Column<string>(type: "text", nullable: false),
                    workflow_commit = table.Column<string>(type: "text", nullable: false),
                    workflow_repository = table.Column<string>(type: "text", nullable: true),
                    output = table.Column<string>(type: "text", nullable: false),
                    lock_path = table.Column<string>(type: "text", nullable: true),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_commit = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    size = table.Column<long>(type: "bigint", nullable: true),
                    file_name = table.Column<string>(type: "text", nullable: true),
                    upload_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    built_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_image_builds", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_image_builds_attempt_id",
                table: "image_builds",
                column: "attempt_id");

            migrationBuilder.CreateIndex(
                name: "ix_image_builds_key",
                table: "image_builds",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_image_builds_template_id",
                table: "image_builds",
                column: "template_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "image_builds");

            migrationBuilder.DropColumn(
                name: "provision_waiting",
                table: "resources");
        }
    }
}
