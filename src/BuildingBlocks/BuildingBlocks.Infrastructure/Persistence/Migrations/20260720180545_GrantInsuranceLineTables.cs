using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // Fixes a real gap the RlsTeardownAndOnePrincipal "1 principal" grant matrix (task 8) never covered:
    // shop.OrderLines/shop.CheckoutSessionLines (added by insurance-pivot task 3's own migration) and
    // shop.OrderLineRevealAudits (task 4) are BRAND NEW tables created after that matrix was written, so
    // pol_app — the sole runtime principal every host connects as — has no grant on any of them at all.
    // Caught by Integration.Tests.OrderSummaryReaderIntegrationTests against a real pol-db container
    // ("The INSERT permission was denied on the object 'OrderLines'") — every insurance checkout would have
    // failed against real SQL Server despite passing every SQLite-backed test, since SQLite has no
    // permission model to catch this. Grant levels mirror the closest existing precedent exactly: OrderLines/
    // CheckoutSessionLines are written once (at Order.Create/Session.Start) and never updated or deleted by
    // any handler in this codebase, so SELECT+INSERT only — same shape as merch.RegistrationAudits.
    // OrderLineRevealAudits is append-only (AppendOnlyDescriptor-guarded) — same SELECT+INSERT shape as
    // merch.VaultRevealAudits.
    public partial class GrantInsuranceLineTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT ON shop.OrderLines            TO pol_app;
                GRANT SELECT, INSERT ON shop.CheckoutSessionLines  TO pol_app;
                GRANT SELECT, INSERT ON shop.OrderLineRevealAudits TO pol_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                REVOKE SELECT, INSERT ON shop.OrderLines            FROM pol_app;
                REVOKE SELECT, INSERT ON shop.CheckoutSessionLines  FROM pol_app;
                REVOKE SELECT, INSERT ON shop.OrderLineRevealAudits FROM pol_app;
                """);
        }
    }
}
