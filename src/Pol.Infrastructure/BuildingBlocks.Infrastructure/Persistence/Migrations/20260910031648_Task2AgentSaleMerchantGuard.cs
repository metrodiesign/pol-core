using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pol.Infrastructure.BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Task2AgentSaleMerchantGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_Agents_Originators_MerchantId_SaleId",
                schema: "acct",
                table: "Agents",
                columns: new[] { "MerchantId", "SaleId" },
                principalSchema: "merch",
                principalTable: "Originators",
                principalColumns: new[] { "MerchantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Agents_Originators_MerchantId_SaleId",
                schema: "acct",
                table: "Agents");
        }
    }
}
