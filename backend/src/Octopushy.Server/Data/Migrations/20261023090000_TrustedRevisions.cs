using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations;

public partial class TrustedRevisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>("source_id", "repository_settings", type: "uuid", nullable: false, defaultValue: Guid.Empty);
        migrationBuilder.AddColumn<Guid>("source_id", "revisions", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>("trusted_at", "revisions", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<string>("trusted_by", "revisions", type: "text", nullable: true);
        // Old revisions and pending work have no provable source binding. Do not invent
        // provenance or authority, and leave all evidence/credential history untouched.
        migrationBuilder.Sql("UPDATE repository_settings SET source_id = gen_random_uuid(), head_commit = NULL;");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Revision trust cannot be discarded safely. Restore a matched pre-upgrade database and key backup instead.");
}
