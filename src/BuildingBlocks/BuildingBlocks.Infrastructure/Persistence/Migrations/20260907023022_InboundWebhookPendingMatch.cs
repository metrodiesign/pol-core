using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InboundWebhookPendingMatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "SignatureValid",
                schema: "txn",
                table: "InboundWebhookEvents",
                type: "bit",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "bit");

            migrationBuilder.AddColumn<string>(
                name: "ExternalChargeId",
                schema: "txn",
                table: "InboundWebhookEvents",
                type: "varchar(256)",
                unicode: false,
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VerificationMode",
                schema: "txn",
                table: "InboundWebhookEvents",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboundWebhookEvents_PspConnectionId_ExternalChargeId_Status",
                schema: "txn",
                table: "InboundWebhookEvents",
                columns: new[] { "PspConnectionId", "ExternalChargeId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InboundWebhookEvents_PspConnectionId_ExternalChargeId_Status",
                schema: "txn",
                table: "InboundWebhookEvents");

            migrationBuilder.DropColumn(
                name: "ExternalChargeId",
                schema: "txn",
                table: "InboundWebhookEvents");

            migrationBuilder.DropColumn(
                name: "VerificationMode",
                schema: "txn",
                table: "InboundWebhookEvents");

            migrationBuilder.AlterColumn<bool>(
                name: "SignatureValid",
                schema: "txn",
                table: "InboundWebhookEvents",
                type: "bit",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "bit",
                oldNullable: true);
        }
    }
}
