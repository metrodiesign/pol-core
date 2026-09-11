using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LegacyVaultExpiryRemediation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // merchant-psp-settings task 9, design "Migration and cutover" step 2: clear the staged 24h expiry
            // off every ACTIVE (2) and RETIRED (3) vault secret version. The old rotation flow stamped an
            // ExpiresAt on the candidate and never cleared it on activate, so an approved credential silently
            // expired after 24h and a pinned/retired version could stop being readable (breaking webhook verify
            // on a live attempt). The domain now clears ExpiresAt on Activate and only enforces expiry for the
            // Staged state; this fixes the rows that predate that. Idempotent: re-running touches nothing once
            // the values are null. State is mapped HasConversion<int> so the numeric literals match the column.
            migrationBuilder.Sql(
                "UPDATE [merch].[VaultSecretVersions] SET [ExpiresAt] = NULL " +
                "WHERE [State] IN (2, 3) AND [ExpiresAt] IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op by design: this is a one-way data repair, and the rollback window must not delete or
            // reintroduce a lapsed expiry (design "Rollback ... must not remove ... vault versions"). The
            // rollback build reads the corrected rows exactly as the forward build does.
        }
    }
}
