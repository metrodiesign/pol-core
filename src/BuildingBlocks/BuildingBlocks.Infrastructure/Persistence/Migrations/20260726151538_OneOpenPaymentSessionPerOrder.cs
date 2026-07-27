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
            // REQ-2.7: any database the PREVIOUS create-session ran against may already hold several open
            // sessions for one order, and a bare CREATE UNIQUE INDEX would abort the migration chain mid
            // deployment. Reconcile first, deterministically: keep the session most likely to be the one the
            // customer is actually paying on (a PSP charge attached wins over none, then newest, then Id) and
            // expire the losers that never reached the PSP. Status 4 = Expired; sessions are never DELETEd.
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT Id, Status, UpdatedAt, PspExternalChargeId,
                           ROW_NUMBER() OVER (
                               PARTITION BY OrderId
                               ORDER BY CASE WHEN PspExternalChargeId IS NULL THEN 1 ELSE 0 END,
                                        CreatedAt DESC, Id) AS rn
                    FROM txn.PaymentSessions
                    WHERE Status IN (0, 1))
                UPDATE ranked
                SET Status = 4, UpdatedAt = SYSUTCDATETIME()
                WHERE rn > 1 AND PspExternalChargeId IS NULL;
                """);

            // A loser that DOES carry a charge is left alone: expiring it would make the confirming webhook's
            // MarkPaid throw on a terminal status forever (a poison outbox row this system has no DLQ for).
            // So if any order still holds more than one open session, that means several charged attempts —
            // a human call, not a migration's. Stop with the OrderIds so it can be resolved and re-run.
            migrationBuilder.Sql("""
                DECLARE @ids nvarchar(max) = (
                    SELECT STRING_AGG(CONVERT(nvarchar(36), OrderId), N', ')
                    FROM (SELECT OrderId
                          FROM txn.PaymentSessions
                          WHERE Status IN (0, 1)
                          GROUP BY OrderId
                          HAVING COUNT(*) > 1) d);

                IF @ids IS NOT NULL
                BEGIN
                    DECLARE @msg nvarchar(2048) = N'OneOpenPaymentSessionPerOrder: cannot create '
                        + N'IX_PaymentSessions_OrderId_Open. These orders still have more than one open '
                        + N'payment session and every one of them has a PSP charge attached, so none can be '
                        + N'expired automatically. Resolve by hand, then re-run the migration. OrderIds: '
                        + LEFT(@ids, 1500);
                    RAISERROR(@msg, 16, 1);
                END
                """);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSessions_OrderId_Open",
                schema: "txn",
                table: "PaymentSessions",
                column: "OrderId",
                unique: true,
                filter: "[Status] IN (0, 1)");
        }

        /// <inheritdoc />
        // Down drops the index only. The sessions Up expired are NOT restored: their pre-migration status
        // (Created vs Redirected) is not recorded anywhere, so reverting would have to guess business state,
        // and an index's Down is the wrong place to guess it. Rolling back leaves those sessions Expired —
        // the order can simply open a fresh one.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentSessions_OrderId_Open",
                schema: "txn",
                table: "PaymentSessions");
        }
    }
}
