using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MerchantPaymentEnvironment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Expand step (design "Migration and cutover" #1). Existing rows default to Sandbox=1 — the safe
            // family — never 0 (not a PspEnvironment). Bootstrapping the environment from the retired global
            // Psp:UseSandbox flag is task 9's remediation, not a blind backfill here.
            migrationBuilder.AddColumn<int>(
                name: "ActiveSecretEnvironment",
                schema: "txn",
                table: "PspConnections",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "PendingSecretEnvironment",
                schema: "txn",
                table: "PspConnections",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingSecretTestResult",
                schema: "txn",
                table: "PspConnections",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PendingSecretTestedAt",
                schema: "txn",
                table: "PspConnections",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WebhookRegisteredAt",
                schema: "txn",
                table: "PspConnections",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WebhookRegisteredBy",
                schema: "txn",
                table: "PspConnections",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebhookRegistrationHash",
                schema: "txn",
                table: "PspConnections",
                type: "nchar(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PaymentEnvironment",
                schema: "merch",
                table: "Merchants",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "PaymentEnvironmentUpdatedAt",
                schema: "merch",
                table: "Merchants",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "SYSUTCDATETIME()");

            migrationBuilder.Sql("UPDATE merch.Merchants SET PaymentEnvironmentUpdatedAt = CreatedAt;");

            migrationBuilder.AddColumn<int>(
                name: "PendingPaymentEnvironment",
                schema: "merch",
                table: "Merchants",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PendingPaymentEnvironmentApprovalId",
                schema: "merch",
                table: "Merchants",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Merchants_PendingPaymentEnvironment",
                schema: "merch",
                table: "Merchants",
                sql: "([PendingPaymentEnvironment] IS NULL AND [PendingPaymentEnvironmentApprovalId] IS NULL) OR ([PendingPaymentEnvironment] IS NOT NULL AND [PendingPaymentEnvironmentApprovalId] IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Merchants_PendingPaymentEnvironment",
                schema: "merch",
                table: "Merchants");

            migrationBuilder.DropColumn(
                name: "ActiveSecretEnvironment",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropColumn(
                name: "PendingSecretEnvironment",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropColumn(
                name: "PendingSecretTestResult",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropColumn(
                name: "PendingSecretTestedAt",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropColumn(
                name: "WebhookRegisteredAt",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropColumn(
                name: "WebhookRegisteredBy",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropColumn(
                name: "WebhookRegistrationHash",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropColumn(
                name: "PaymentEnvironment",
                schema: "merch",
                table: "Merchants");

            migrationBuilder.DropColumn(
                name: "PaymentEnvironmentUpdatedAt",
                schema: "merch",
                table: "Merchants");

            migrationBuilder.DropColumn(
                name: "PendingPaymentEnvironment",
                schema: "merch",
                table: "Merchants");

            migrationBuilder.DropColumn(
                name: "PendingPaymentEnvironmentApprovalId",
                schema: "merch",
                table: "Merchants");
        }
    }
}
