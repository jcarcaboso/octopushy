using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RunHistoryIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Three previously unenforced parent links. Removing attempts first also
            // exposes their artifacts and secret-release records to the following cleanup.
            migrationBuilder.Sql("""
                DELETE FROM attempts a WHERE a.run_id IS NOT NULL
                    AND NOT EXISTS (SELECT 1 FROM runs r WHERE r.id = a.run_id);
                DELETE FROM artifacts a
                    WHERE NOT EXISTS (SELECT 1 FROM attempts t WHERE t.id = a.attempt_id);
                DELETE FROM secret_resolutions s
                    WHERE NOT EXISTS (SELECT 1 FROM attempts t WHERE t.id = s.attempt_id);
                """);

            migrationBuilder.CreateIndex(
                name: "ix_secret_resolutions_key",
                table: "secret_resolutions",
                column: "key");

            migrationBuilder.CreateIndex(
                name: "ix_runs_created_at",
                table: "runs",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_attempts_run_id",
                table: "attempts",
                column: "run_id");

            migrationBuilder.AddForeignKey(
                name: "fk_artifacts_attempts_attempt_id",
                table: "artifacts",
                column: "attempt_id",
                principalTable: "attempts",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_attempts_runs_run_id",
                table: "attempts",
                column: "run_id",
                principalTable: "runs",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_secret_resolutions_attempts_attempt_id",
                table: "secret_resolutions",
                column: "attempt_id",
                principalTable: "attempts",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_artifacts_attempts_attempt_id",
                table: "artifacts");

            migrationBuilder.DropForeignKey(
                name: "fk_attempts_runs_run_id",
                table: "attempts");

            migrationBuilder.DropForeignKey(
                name: "fk_secret_resolutions_attempts_attempt_id",
                table: "secret_resolutions");

            migrationBuilder.DropIndex(
                name: "ix_secret_resolutions_key",
                table: "secret_resolutions");

            migrationBuilder.DropIndex(
                name: "ix_runs_created_at",
                table: "runs");

            migrationBuilder.DropIndex(
                name: "ix_attempts_run_id",
                table: "attempts");
        }
    }
}
