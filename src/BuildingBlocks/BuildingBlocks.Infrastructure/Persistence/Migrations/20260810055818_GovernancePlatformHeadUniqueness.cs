using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GovernancePlatformHeadUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_AuditHeads_PlatformScope",
                schema: "admin",
                table: "AuditHeads",
                column: "ScopeKind",
                unique: true,
                filter: "[MerchantId] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_AuditHeads_PlatformScope",
                schema: "admin",
                table: "AuditHeads");
        }
    }
}
