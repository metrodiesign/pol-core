using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // policy-reference-record task 1 (REQ-7.2/7.3): OrderLine -> OrderItem rename. Hand-edited from the
    // scaffolded Drop+Create (EF's model differ keys entities by CLR full name, so a renamed CLR type/
    // namespace reads as "remove old entity, add new entity") into RenameTable/RenameColumn/RenameIndex —
    // sp_rename preserves the existing rows AND the GRANTs pol_app already holds on these 3 tables
    // (design.md "Migration strategy": "GRANT บนตารางที่ rename คงอยู่อัตโนมัติ ไม่ต้อง re-grant"). PK/FK
    // constraint names are NOT renamed (sp_rename on a table never touches them, and leaving them is
    // purely cosmetic — SQL Server does not care that "PK_OrderLines" now sits on table "OrderItems").
    public partial class RenameOrderLinesToOrderItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "OrderLines", schema: "shop", newName: "OrderItems", newSchema: "shop");

            migrationBuilder.RenameTable(
                name: "OrderLineRevealAudits", schema: "shop", newName: "OrderItemRevealAudits", newSchema: "shop");

            migrationBuilder.RenameTable(
                name: "CheckoutSessionLines", schema: "shop", newName: "CheckoutSessionItems", newSchema: "shop");

            migrationBuilder.RenameColumn(
                name: "OrderLineId", schema: "shop", table: "OrderItemRevealAudits", newName: "OrderItemId");

            migrationBuilder.RenameIndex(
                schema: "shop", table: "OrderItems",
                name: "IX_OrderLines_OrderId_MerchantId", newName: "IX_OrderItems_OrderId_MerchantId");

            migrationBuilder.RenameIndex(
                schema: "shop", table: "OrderItemRevealAudits",
                name: "IX_OrderLineRevealAudits_MerchantId_RevealedAt", newName: "IX_OrderItemRevealAudits_MerchantId_RevealedAt");

            migrationBuilder.RenameIndex(
                schema: "shop", table: "OrderItemRevealAudits",
                name: "IX_OrderLineRevealAudits_OrderLineId", newName: "IX_OrderItemRevealAudits_OrderItemId");

            migrationBuilder.RenameIndex(
                schema: "shop", table: "CheckoutSessionItems",
                name: "IX_CheckoutSessionLines_SessionId_MerchantId", newName: "IX_CheckoutSessionItems_SessionId_MerchantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameIndex(
                schema: "shop", table: "CheckoutSessionItems",
                name: "IX_CheckoutSessionItems_SessionId_MerchantId", newName: "IX_CheckoutSessionLines_SessionId_MerchantId");

            migrationBuilder.RenameIndex(
                schema: "shop", table: "OrderItemRevealAudits",
                name: "IX_OrderItemRevealAudits_OrderItemId", newName: "IX_OrderLineRevealAudits_OrderLineId");

            migrationBuilder.RenameIndex(
                schema: "shop", table: "OrderItemRevealAudits",
                name: "IX_OrderItemRevealAudits_MerchantId_RevealedAt", newName: "IX_OrderLineRevealAudits_MerchantId_RevealedAt");

            migrationBuilder.RenameIndex(
                schema: "shop", table: "OrderItems",
                name: "IX_OrderItems_OrderId_MerchantId", newName: "IX_OrderLines_OrderId_MerchantId");

            migrationBuilder.RenameColumn(
                name: "OrderItemId", schema: "shop", table: "OrderItemRevealAudits", newName: "OrderLineId");

            migrationBuilder.RenameTable(
                name: "CheckoutSessionItems", schema: "shop", newName: "CheckoutSessionLines", newSchema: "shop");

            migrationBuilder.RenameTable(
                name: "OrderItemRevealAudits", schema: "shop", newName: "OrderLineRevealAudits", newSchema: "shop");

            migrationBuilder.RenameTable(
                name: "OrderItems", schema: "shop", newName: "OrderLines", newSchema: "shop");
        }
    }
}
