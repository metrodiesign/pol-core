using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameDateColumnsDropUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "UpdatedAtUtc",
                schema: "producer",
                table: "VaultSecrets",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "CreatedAtUtc",
                schema: "producer",
                table: "VaultSecrets",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "RevealedAtUtc",
                schema: "producer",
                table: "VaultRevealAudits",
                newName: "RevealedAt");

            migrationBuilder.RenameColumn(
                name: "CreatedAtUtc",
                schema: "producer",
                table: "Tenants",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "CreatedAtUtc",
                schema: "producer",
                table: "PspConnections",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "OccurredAtUtc",
                schema: "producer",
                table: "ProvisioningAudits",
                newName: "OccurredAt");

            migrationBuilder.RenameColumn(
                name: "CreatedAtUtc",
                schema: "producer",
                table: "Products",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "UpdatedAtUtc",
                schema: "producer",
                table: "PaymentSessions",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "CreatedAtUtc",
                schema: "producer",
                table: "PaymentSessions",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "ProcessedAtUtc",
                schema: "producer",
                table: "OutboxMessages",
                newName: "ProcessedAt");

            migrationBuilder.RenameColumn(
                name: "OccurredAtUtc",
                schema: "producer",
                table: "OutboxMessages",
                newName: "OccurredAt");

            migrationBuilder.RenameColumn(
                name: "LeaseExpiresAtUtc",
                schema: "producer",
                table: "OutboxMessages",
                newName: "LeaseExpiresAt");

            migrationBuilder.RenameIndex(
                name: "IX_OutboxMessages_ProcessedAtUtc_LeaseExpiresAtUtc",
                schema: "producer",
                table: "OutboxMessages",
                newName: "IX_OutboxMessages_ProcessedAt_LeaseExpiresAt");

            migrationBuilder.RenameColumn(
                name: "SummaryTokenExpiresAtUtc",
                schema: "producer",
                table: "Orders",
                newName: "SummaryTokenExpiresAt");

            migrationBuilder.RenameColumn(
                name: "PaidAtUtc",
                schema: "producer",
                table: "Orders",
                newName: "PaidAt");

            migrationBuilder.RenameColumn(
                name: "CreatedAtUtc",
                schema: "producer",
                table: "Orders",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "CreatedAtUtc",
                schema: "producer",
                table: "IdempotencyRecords",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "CreatedAtUtc",
                schema: "producer",
                table: "CheckoutSessions",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "CreatedAtUtc",
                schema: "producer",
                table: "Carts",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "AssignedAtUtc",
                schema: "producer",
                table: "AdminTenantAssignments",
                newName: "AssignedAt");

            migrationBuilder.RenameColumn(
                name: "CreatedAtUtc",
                schema: "producer",
                table: "AdminAccounts",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "OccurredAtUtc",
                schema: "producer",
                table: "AdminAccountAudits",
                newName: "OccurredAt");

            // usp_resolve_order_summary (from AddOrderSummaryToken) selected SummaryTokenExpiresAtUtc; renaming
            // the column above does NOT update the proc body, so it must follow or it breaks at runtime
            // ("Invalid column name 'SummaryTokenExpiresAtUtc'").
            migrationBuilder.Sql("""
                ALTER PROCEDURE producer.usp_resolve_order_summary @Token nvarchar(64)
                WITH EXECUTE AS 'pol_webhook_resolver' AS
                BEGIN
                    SET NOCOUNT ON;
                    SELECT TOP 1 Id, TenantId, AmountMinorUnits, AmountCurrency, Status, PaymentSessionId, SummaryTokenExpiresAt
                    FROM producer.Orders WHERE SummaryToken = @Token;
                END
                """);

            // In-flight outbox payloads were serialized with the old camelCase key "occurredAtUtc" (the single
            // date field on the PaymentPaid / CheckoutConfirmed / CustomerOrderNotification events). Normalize
            // them to "occurredAt" so the renamed contract deserializes them instead of dropping the timestamp.
            migrationBuilder.Sql(
                "UPDATE producer.OutboxMessages SET Payload = REPLACE(Payload, '\"occurredAtUtc\"', '\"occurredAt\"') " +
                "WHERE Payload LIKE '%\"occurredAtUtc\"%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                schema: "producer",
                table: "VaultSecrets",
                newName: "UpdatedAtUtc");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "producer",
                table: "VaultSecrets",
                newName: "CreatedAtUtc");

            migrationBuilder.RenameColumn(
                name: "RevealedAt",
                schema: "producer",
                table: "VaultRevealAudits",
                newName: "RevealedAtUtc");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "producer",
                table: "Tenants",
                newName: "CreatedAtUtc");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "producer",
                table: "PspConnections",
                newName: "CreatedAtUtc");

            migrationBuilder.RenameColumn(
                name: "OccurredAt",
                schema: "producer",
                table: "ProvisioningAudits",
                newName: "OccurredAtUtc");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "producer",
                table: "Products",
                newName: "CreatedAtUtc");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                schema: "producer",
                table: "PaymentSessions",
                newName: "UpdatedAtUtc");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "producer",
                table: "PaymentSessions",
                newName: "CreatedAtUtc");

            migrationBuilder.RenameColumn(
                name: "ProcessedAt",
                schema: "producer",
                table: "OutboxMessages",
                newName: "ProcessedAtUtc");

            migrationBuilder.RenameColumn(
                name: "OccurredAt",
                schema: "producer",
                table: "OutboxMessages",
                newName: "OccurredAtUtc");

            migrationBuilder.RenameColumn(
                name: "LeaseExpiresAt",
                schema: "producer",
                table: "OutboxMessages",
                newName: "LeaseExpiresAtUtc");

            migrationBuilder.RenameIndex(
                name: "IX_OutboxMessages_ProcessedAt_LeaseExpiresAt",
                schema: "producer",
                table: "OutboxMessages",
                newName: "IX_OutboxMessages_ProcessedAtUtc_LeaseExpiresAtUtc");

            migrationBuilder.RenameColumn(
                name: "SummaryTokenExpiresAt",
                schema: "producer",
                table: "Orders",
                newName: "SummaryTokenExpiresAtUtc");

            migrationBuilder.RenameColumn(
                name: "PaidAt",
                schema: "producer",
                table: "Orders",
                newName: "PaidAtUtc");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "producer",
                table: "Orders",
                newName: "CreatedAtUtc");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "producer",
                table: "IdempotencyRecords",
                newName: "CreatedAtUtc");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "producer",
                table: "CheckoutSessions",
                newName: "CreatedAtUtc");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "producer",
                table: "Carts",
                newName: "CreatedAtUtc");

            migrationBuilder.RenameColumn(
                name: "AssignedAt",
                schema: "producer",
                table: "AdminTenantAssignments",
                newName: "AssignedAtUtc");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "producer",
                table: "AdminAccounts",
                newName: "CreatedAtUtc");

            migrationBuilder.RenameColumn(
                name: "OccurredAt",
                schema: "producer",
                table: "AdminAccountAudits",
                newName: "OccurredAtUtc");

            // Revert the proc to the restored column name (the column was renamed back to ...Utc above).
            migrationBuilder.Sql("""
                ALTER PROCEDURE producer.usp_resolve_order_summary @Token nvarchar(64)
                WITH EXECUTE AS 'pol_webhook_resolver' AS
                BEGIN
                    SET NOCOUNT ON;
                    SELECT TOP 1 Id, TenantId, AmountMinorUnits, AmountCurrency, Status, PaymentSessionId, SummaryTokenExpiresAtUtc
                    FROM producer.Orders WHERE SummaryToken = @Token;
                END
                """);

            migrationBuilder.Sql(
                "UPDATE producer.OutboxMessages SET Payload = REPLACE(Payload, '\"occurredAt\"', '\"occurredAtUtc\"') " +
                "WHERE Payload LIKE '%\"occurredAt\"%';");
        }
    }
}
