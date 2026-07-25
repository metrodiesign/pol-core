using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderItemPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrderItemPolicies",
                schema: "shop",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InsuranceCategory = table.Column<int>(type: "int", nullable: true),
                    ReferenceNumberType = table.Column<int>(type: "int", nullable: true),
                    ReferenceNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    EndorsementNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RenewalReminderNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    InsuredObjectReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    NetPremiumAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    NetPremiumCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true),
                    GrossPremiumAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    GrossPremiumCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true),
                    PremiumRemittanceStatus = table.Column<int>(type: "int", nullable: false),
                    DeductedAt = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderItemPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrderItemPolicyAudits",
                schema: "shop",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ActorKind = table.Column<int>(type: "int", nullable: false),
                    Operation = table.Column<int>(type: "int", nullable: false),
                    ChangeSummary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderItemPolicyAudits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderItemPolicies_MerchantId",
                schema: "shop",
                table: "OrderItemPolicies",
                column: "MerchantId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderItemPolicies_OrderItemId",
                schema: "shop",
                table: "OrderItemPolicies",
                column: "OrderItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderItemPolicyAudits_MerchantId_OccurredAt",
                schema: "shop",
                table: "OrderItemPolicyAudits",
                columns: new[] { "MerchantId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderItemPolicyAudits_OrderItemId",
                schema: "shop",
                table: "OrderItemPolicyAudits",
                column: "OrderItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderItemPolicies",
                schema: "shop");

            migrationBuilder.DropTable(
                name: "OrderItemPolicyAudits",
                schema: "shop");
        }
    }
}
