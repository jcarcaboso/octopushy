using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ServicePipelines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_tofu_state_versions_tofu_states_resource_id",
                table: "tofu_state_versions");

            migrationBuilder.DropPrimaryKey(
                name: "pk_tofu_states",
                table: "tofu_states");

            migrationBuilder.DropIndex(
                name: "ix_tofu_state_versions_resource_id_version",
                table: "tofu_state_versions");

            migrationBuilder.AddColumn<string>(
                name: "stage",
                table: "tofu_states",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "stage",
                table: "tofu_state_versions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "document",
                table: "steps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stage",
                table: "steps",
                type: "text",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "pk_tofu_states",
                table: "tofu_states",
                columns: new[] { "resource_id", "stage" });

            migrationBuilder.CreateIndex(
                name: "ix_tofu_state_versions_resource_id_stage_version",
                table: "tofu_state_versions",
                columns: new[] { "resource_id", "stage", "version" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_tofu_state_versions_tofu_states_resource_id_stage",
                table: "tofu_state_versions",
                columns: new[] { "resource_id", "stage" },
                principalTable: "tofu_states",
                principalColumns: new[] { "resource_id", "stage" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_tofu_state_versions_tofu_states_resource_id_stage",
                table: "tofu_state_versions");

            migrationBuilder.DropPrimaryKey(
                name: "pk_tofu_states",
                table: "tofu_states");

            migrationBuilder.DropIndex(
                name: "ix_tofu_state_versions_resource_id_stage_version",
                table: "tofu_state_versions");

            migrationBuilder.DropColumn(
                name: "stage",
                table: "tofu_states");

            migrationBuilder.DropColumn(
                name: "stage",
                table: "tofu_state_versions");

            migrationBuilder.DropColumn(
                name: "document",
                table: "steps");

            migrationBuilder.DropColumn(
                name: "stage",
                table: "steps");

            migrationBuilder.AddPrimaryKey(
                name: "pk_tofu_states",
                table: "tofu_states",
                column: "resource_id");

            migrationBuilder.CreateIndex(
                name: "ix_tofu_state_versions_resource_id_version",
                table: "tofu_state_versions",
                columns: new[] { "resource_id", "version" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_tofu_state_versions_tofu_states_resource_id",
                table: "tofu_state_versions",
                column: "resource_id",
                principalTable: "tofu_states",
                principalColumn: "resource_id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
