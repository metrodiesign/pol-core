using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// purchase-flow-completion task 6 (REQ-6.x/7.x). HAND-WRITTEN, not the scaffold: `migrations add` read
    /// the CheckoutSessions change as `RenameColumn NotificationRecipient -> CustomerEmail`, which would have
    /// silently relabelled every stored phone number as an email address; it also cannot know about the
    /// sequence, the backfill, or the DEFAULTs a rolling deploy needs. Five phases, in order:
    /// <para>
    /// (a) ADD every column nullable or with a DB DEFAULT; (b) BACKFILL — mint an OrderNo for every existing
    /// order and split the single recipient column into phone/email (guarded: a legacy value the split would
    /// truncate FAILS the migration rather than silently corrupting it); (c) ALTER OrderNo to NOT NULL now
    /// that every row has one; (d) UNIQUE index on OrderNo; (e) DROP the checkout's recipient column, whose
    /// content now lives in the two customer columns.
    /// </para>
    /// <para>
    /// Concurrency truth, not a zero-downtime claim (review PR #168): EF runs the whole migration in ONE
    /// transaction, and the phase-(a) ALTER TABLE ADD takes a Sch-M lock on each table that is held to
    /// COMMIT — so no concurrent INSERT can land between the backfill and the NOT NULL flip; writers block
    /// until the migration commits as a whole. The DEFAULTs exist for the window AFTER commit while the
    /// previous api build is still being replaced (docker-compose.prod.yml starts the new hosts only after
    /// `migrate` completes): that build can still INSERT CheckoutSessions rows, but its Orders INSERTs fail
    /// loudly on the NOT NULL OrderNo — deliberate, because no DEFAULT can satisfy a UNIQUE order number.
    /// </para>
    /// shop.Orders.NotificationRecipient is deliberately KEPT: it is still the single source of truth for
    /// where the summary link is sent, and the consumer derives it from phone/email (design F-03).
    /// </summary>
    public partial class CheckoutOrderEnrichment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- (a) the platform-wide order-number allocator (REQ-7.1) -------------------------------------
            // One sequence, never reset: the Buddhist year in the number is display formatting taken from the
            // minting date, so there is no per-year counter and no cross-year race. pol_app needs UPDATE on the
            // sequence OBJECT to consume values — a grant no SQLite test can catch, so it lives here.
            migrationBuilder.Sql("CREATE SEQUENCE shop.OrderNoSeq AS bigint START WITH 1 INCREMENT BY 1;");
            migrationBuilder.Sql("GRANT UPDATE ON OBJECT::shop.OrderNoSeq TO pol_app;");

            // --- (a) columns: every NOT NULL one carries a DEFAULT so the old build can still INSERT --------
            migrationBuilder.Sql(
                """
                ALTER TABLE shop.CheckoutSessions
                    ADD PaymentChannel varchar(20) NOT NULL CONSTRAINT DF_CheckoutSessions_PaymentChannel DEFAULT ('CARD'),
                        CustomerName nvarchar(200) NOT NULL CONSTRAINT DF_CheckoutSessions_CustomerName DEFAULT (N'(ไม่ระบุ)'),
                        CustomerPhone varchar(20) NOT NULL CONSTRAINT DF_CheckoutSessions_CustomerPhone DEFAULT (''),
                        CustomerEmail nvarchar(320) NULL;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE shop.CheckoutSessionItems
                    ADD DiscountAmount decimal(19,4) NOT NULL CONSTRAINT DF_CheckoutSessionItems_DiscountAmount DEFAULT (0),
                        DiscountCurrency char(3) NOT NULL CONSTRAINT DF_CheckoutSessionItems_DiscountCurrency DEFAULT ('THB');
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE shop.OrderItems
                    ADD DiscountAmount decimal(19,4) NOT NULL CONSTRAINT DF_OrderItems_DiscountAmount DEFAULT (0),
                        DiscountCurrency char(3) NOT NULL CONSTRAINT DF_OrderItems_DiscountCurrency DEFAULT ('THB');
                """);

            // OrderNo starts NULLable: no default could be correct, every row needs its own value.
            migrationBuilder.Sql(
                """
                ALTER TABLE shop.Orders
                    ADD OrderNo varchar(13) NULL,
                        PaymentChannel varchar(20) NULL,
                        CustomerName nvarchar(200) NOT NULL CONSTRAINT DF_Orders_CustomerName DEFAULT (N'(ไม่ระบุ)'),
                        CustomerPhone varchar(20) NOT NULL CONSTRAINT DF_Orders_CustomerPhone DEFAULT (''),
                        CustomerEmail nvarchar(320) NULL;
                """);

            // --- (b) backfill --------------------------------------------------------------------------------
            // One number per existing order, out of the same sequence new orders will use, formatted exactly as
            // OrderNoSequence.Format does: ORD + Buddhist year (2 digits) + the sequence value padded to 8.
            migrationBuilder.Sql(
                """
                UPDATE shop.Orders
                SET OrderNo = CONCAT(
                        'ORD',
                        RIGHT(CONVERT(varchar(4), YEAR(GETUTCDATE()) + 543), 2),
                        FORMAT(NEXT VALUE FOR shop.OrderNoSeq, 'D8'))
                WHERE OrderNo IS NULL;
                """);

            // The one recipient column held either an email or a phone; split it where the readers now look.
            // Rows with neither keep the column DEFAULTs, which is exactly what "unknown customer" means here.
            // A phone-shaped value longer than the 20 chars CustomerPhone holds is bad legacy data: FAIL the
            // migration naming the rows, exactly as CustomerContact.Of would refuse it in application code —
            // never silently truncate it into a different phone number (review PR #168).
            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1 FROM shop.Orders
                    WHERE NotificationRecipient IS NOT NULL
                      AND CHARINDEX('@', NotificationRecipient) = 0
                      AND LEN(NotificationRecipient) > 20)
                    THROW 50001, N'CheckoutOrderEnrichment: shop.Orders holds phone-shaped NotificationRecipient values longer than 20 chars; fix those rows before migrating.', 1;
                """);
            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1 FROM shop.CheckoutSessions
                    WHERE NotificationRecipient IS NOT NULL
                      AND CHARINDEX('@', NotificationRecipient) = 0
                      AND LEN(NotificationRecipient) > 20)
                    THROW 50001, N'CheckoutOrderEnrichment: shop.CheckoutSessions holds phone-shaped NotificationRecipient values longer than 20 chars; fix those rows before migrating.', 1;
                """);
            migrationBuilder.Sql(
                """
                UPDATE shop.Orders
                SET CustomerEmail = NotificationRecipient
                WHERE NotificationRecipient IS NOT NULL AND CHARINDEX('@', NotificationRecipient) > 0;
                """);
            migrationBuilder.Sql(
                """
                UPDATE shop.Orders
                SET CustomerPhone = NotificationRecipient
                WHERE NotificationRecipient IS NOT NULL AND CHARINDEX('@', NotificationRecipient) = 0;
                """);
            migrationBuilder.Sql(
                """
                UPDATE shop.CheckoutSessions
                SET CustomerEmail = NotificationRecipient
                WHERE NotificationRecipient IS NOT NULL AND CHARINDEX('@', NotificationRecipient) > 0;
                """);
            migrationBuilder.Sql(
                """
                UPDATE shop.CheckoutSessions
                SET CustomerPhone = NotificationRecipient
                WHERE NotificationRecipient IS NOT NULL AND CHARINDEX('@', NotificationRecipient) = 0;
                """);

            // --- (c) OrderNo is populated everywhere; make it mandatory --------------------------------------
            migrationBuilder.Sql("ALTER TABLE shop.Orders ALTER COLUMN OrderNo varchar(13) NOT NULL;");

            // --- (d) ...and unique (REQ-7.1) ------------------------------------------------------------------
            migrationBuilder.CreateIndex(
                name: "IX_Orders_OrderNo",
                schema: "shop",
                table: "Orders",
                column: "OrderNo",
                unique: true);

            // --- (e) the checkout's recipient column has no readers left -------------------------------------
            migrationBuilder.DropColumn(
                name: "NotificationRecipient",
                schema: "shop",
                table: "CheckoutSessions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE shop.CheckoutSessions ADD NotificationRecipient nvarchar(320) NULL;");
            migrationBuilder.Sql(
                """
                UPDATE shop.CheckoutSessions
                SET NotificationRecipient = COALESCE(NULLIF(CustomerPhone, ''), CustomerEmail);
                """);

            migrationBuilder.DropIndex(name: "IX_Orders_OrderNo", schema: "shop", table: "Orders");

            // Named DEFAULT constraints have to go before their columns can be dropped.
            migrationBuilder.Sql(
                """
                ALTER TABLE shop.Orders DROP CONSTRAINT DF_Orders_CustomerName, DF_Orders_CustomerPhone;
                ALTER TABLE shop.Orders DROP COLUMN OrderNo, PaymentChannel, CustomerName, CustomerPhone, CustomerEmail;
                """);
            migrationBuilder.Sql(
                """
                ALTER TABLE shop.OrderItems DROP CONSTRAINT DF_OrderItems_DiscountAmount, DF_OrderItems_DiscountCurrency;
                ALTER TABLE shop.OrderItems DROP COLUMN DiscountAmount, DiscountCurrency;
                """);
            migrationBuilder.Sql(
                """
                ALTER TABLE shop.CheckoutSessionItems DROP CONSTRAINT DF_CheckoutSessionItems_DiscountAmount, DF_CheckoutSessionItems_DiscountCurrency;
                ALTER TABLE shop.CheckoutSessionItems DROP COLUMN DiscountAmount, DiscountCurrency;
                """);
            migrationBuilder.Sql(
                """
                ALTER TABLE shop.CheckoutSessions DROP CONSTRAINT DF_CheckoutSessions_PaymentChannel, DF_CheckoutSessions_CustomerName, DF_CheckoutSessions_CustomerPhone;
                ALTER TABLE shop.CheckoutSessions DROP COLUMN PaymentChannel, CustomerName, CustomerPhone, CustomerEmail;
                """);

            migrationBuilder.Sql("DROP SEQUENCE shop.OrderNoSeq;");
        }
    }
}
