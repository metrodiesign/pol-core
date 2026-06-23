using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderSummaryToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Per-row unique default so the UNIQUE index holds for any pre-existing rows (a constant default
            // would collide). New orders overwrite it with the domain-issued token.
            migrationBuilder.AddColumn<string>(
                name: "SummaryToken",
                schema: "producer",
                table: "Orders",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValueSql: "LOWER(REPLACE(CONVERT(varchar(36), NEWID()), '-', ''))");

            // Pre-existing rows get an already-past expiry (their links are dead — correct, they predate links).
            migrationBuilder.AddColumn<DateTime>(
                name: "SummaryTokenExpiresAtUtc",
                schema: "producer",
                table: "Orders",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(2000, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateIndex(
                name: "IX_Orders_SummaryToken",
                schema: "producer",
                table: "Orders",
                column: "SummaryToken",
                unique: true);

            // Public summary read: the customer has no tenant identity, so the token-keyed lookup runs as the
            // bypass resolver (mirrors usp_resolve_webhook_tenant) — reads ONLY the one order the token names.
            // Expiry is returned, not filtered, so the caller can tell "unknown" (404) from "expired" (410).
            migrationBuilder.Sql("""
                CREATE PROCEDURE producer.usp_resolve_order_summary @Token nvarchar(64)
                WITH EXECUTE AS 'pol_webhook_resolver' AS
                BEGIN
                    SET NOCOUNT ON;
                    SELECT TOP 1 Id, TenantId, AmountMinorUnits, AmountCurrency, Status, PaymentSessionId, SummaryTokenExpiresAtUtc
                    FROM producer.Orders WHERE SummaryToken = @Token;
                END
                """);

            migrationBuilder.Sql("""
                GRANT SELECT ON producer.Orders TO pol_webhook_resolver;
                GRANT EXECUTE ON producer.usp_resolve_order_summary TO pol_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE EXECUTE ON producer.usp_resolve_order_summary FROM pol_app;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS producer.usp_resolve_order_summary;");
            migrationBuilder.Sql("REVOKE SELECT ON producer.Orders FROM pol_webhook_resolver;");

            migrationBuilder.DropIndex(
                name: "IX_Orders_SummaryToken",
                schema: "producer",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "SummaryToken",
                schema: "producer",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "SummaryTokenExpiresAtUtc",
                schema: "producer",
                table: "Orders");
        }
    }
}
