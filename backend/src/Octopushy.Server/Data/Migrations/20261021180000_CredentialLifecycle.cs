using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Octopushy.Server.Data.Migrations;

public partial class CredentialLifecycle : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("latest_version", "store_entries", type: "integer", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<DateTimeOffset>("activated_at", "store_versions", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<string>("activated_by", "store_versions", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("activation_note", "store_versions", type: "text", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>("revoked_at", "store_versions", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<string>("revoked_by", "store_versions", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("revocation_note", "store_versions", type: "text", nullable: true);
        // Every old version was immediately active when it was written. Preserve that
        // history without decrypting or rewriting any ciphertext/AAD.
        migrationBuilder.Sql("""
            UPDATE store_entries SET latest_version = current_version;
            UPDATE store_versions
            SET activated_at = created_at, activated_by = 'legacy',
                activation_note = 'Existing immediately-active version; no credential verification was recorded.';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Credential lifecycle metadata cannot be discarded safely. Restore a matched pre-upgrade database and key backup instead.");
}
