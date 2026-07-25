using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // Same gap the GrantInsuranceLineTables migration fixed for its own new tables (insurance-pivot task 3/4):
    // shop.OrderItemPolicies/OrderItemPolicyAudits are brand new (this task's own CreateTable migration), so
    // pol_app — the sole runtime principal — has no grant on either yet. shop.OrderItemPolicies is mutable
    // (ItemPolicy.Apply edits an existing record, REQ-3.4) -> SELECT+INSERT+UPDATE. OrderItemPolicyAudits is
    // append-only (AppendOnlyDescriptor-guarded) -> SELECT+INSERT only, same shape as OrderItemRevealAudits.
    public partial class GrantOrderItemPolicyTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE ON shop.OrderItemPolicies      TO pol_app;
                GRANT SELECT, INSERT         ON shop.OrderItemPolicyAudits TO pol_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                REVOKE SELECT, INSERT, UPDATE ON shop.OrderItemPolicies      FROM pol_app;
                REVOKE SELECT, INSERT         ON shop.OrderItemPolicyAudits FROM pol_app;
                """);
        }
    }
}
