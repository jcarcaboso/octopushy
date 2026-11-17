using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class NotificationWebhook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "agent_id",
                table: "notifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "offline_notified_at",
                table: "agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "webhook",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    kinds = table.Column<string[]>(type: "text[]", nullable: false),
                    generation = table.Column<int>(type: "integer", nullable: false),
                    position = table.Column<long>(type: "bigint", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    refusals = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dropped = table.Column<long>(type: "bigint", nullable: false),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_event_id = table.Column<long>(type: "bigint", nullable: true),
                    last_delivered = table.Column<bool>(type: "boolean", nullable: false),
                    last_status = table.Column<int>(type: "integer", nullable: true),
                    last_result = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    last_delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    stopped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "webhook");

            migrationBuilder.DropColumn(
                name: "agent_id",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "offline_notified_at",
                table: "agents");
        }
    }
}
