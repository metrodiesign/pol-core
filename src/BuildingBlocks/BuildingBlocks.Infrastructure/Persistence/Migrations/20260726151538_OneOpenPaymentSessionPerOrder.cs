using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // captive-payment-alignment REQ-2.4: the DB floor for "one order is chargeable at most once at a time".
    // The create-session handler already refuses a second open session, but an application pre-check loses a
    // race between two concurrent requests — this index does not. Status 0/1 = Created/Redirected (the two
    // chargeable states); Paid/Failed/Expired sit outside the filter, so a declined attempt can still open a
    // fresh session for the same order (REQ-7.4) instead of killing it permanently. A violation surfaces as
    // SQL 2601/2627, which MerchantRuntimeUnitOfWork already translates to ConflictException -> 409 (REQ-2.5).
    public partial class OneOpenPaymentSessionPerOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_PaymentSessions_OrderId_Open",
                schema: "txn",
                table: "PaymentSessions",
                column: "OrderId",
                unique: true,
                filter: "[Status] IN (0, 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentSessions_OrderId_Open",
                schema: "txn",
                table: "PaymentSessions");
        }
    }
}
