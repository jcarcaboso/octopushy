using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <summary>An attempt's log line count is the log store's: the column was never written.</summary>
    public partial class DropAttemptLogLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "log_lines",
                table: "attempts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "log_lines",
                table: "attempts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }
    }
}
