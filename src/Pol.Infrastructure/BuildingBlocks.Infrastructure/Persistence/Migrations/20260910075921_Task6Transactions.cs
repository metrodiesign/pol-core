using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pol.Infrastructure.BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Task6Transactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SuccessfulTransactionId",
                schema: "shop",
                table: "Orders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Transactions",
                schema: "txn",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TransactionNo = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    AttemptNo = table.Column<int>(type: "int", nullable: false),
                    PaymentMethod = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Environment = table.Column<int>(type: "int", nullable: false),
                    CredentialVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfigurationVersion = table.Column<long>(type: "bigint", nullable: false),
                    ProviderRequestReference = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: false),
                    ProviderReference = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: true),
                    RedirectUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    ReturnBinding = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ProviderStatus = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true),
                    OrderSnapshot = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SafeProviderMetadata = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    NeedsReview = table.Column<bool>(type: "bit", nullable: false),
                    ReviewCode = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SucceededAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastInquiryAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NextInquiryAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    InquiryAttempts = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    AmountAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    AmountCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transactions", x => x.Id);
                    table.UniqueConstraint("AK_Transactions_Id_MerchantId", x => new { x.Id, x.MerchantId });
                    table.CheckConstraint("CK_Transactions_AttemptNo", "[AttemptNo] >= 1");
                    table.CheckConstraint("CK_Transactions_PaymentMethod", "[PaymentMethod] IN ('card', 'promptpay', 'installment')");
                    table.CheckConstraint("CK_Transactions_Status", "[Status] IN (1, 2, 3, 4, 5, 6)");
                    table.ForeignKey(
                        name: "FK_Transactions_Orders_OrderId_MerchantId",
                        columns: x => new { x.OrderId, x.MerchantId },
                        principalSchema: "shop",
                        principalTable: "Orders",
                        principalColumns: new[] { "Id", "MerchantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TransactionEvents",
                schema: "txn",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    EventReference = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: true),
                    ProviderStatus = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true),
                    EvidenceCode = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true),
                    SafeDetails = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TransactionEvents_Transactions_TransactionId_MerchantId",
                        columns: x => new { x.TransactionId, x.MerchantId },
                        principalSchema: "txn",
                        principalTable: "Transactions",
                        principalColumns: new[] { "Id", "MerchantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_SuccessfulTransactionId",
                schema: "shop",
                table: "Orders",
                column: "SuccessfulTransactionId",
                filter: "[SuccessfulTransactionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_SuccessfulTransactionId_MerchantId",
                schema: "shop",
                table: "Orders",
                columns: new[] { "SuccessfulTransactionId", "MerchantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransactionEvents_TransactionId_EventReference_Source",
                schema: "txn",
                table: "TransactionEvents",
                columns: new[] { "TransactionId", "EventReference", "Source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransactionEvents_TransactionId_MerchantId",
                schema: "txn",
                table: "TransactionEvents",
                columns: new[] { "TransactionId", "MerchantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransactionEvents_TransactionId_ReceivedAt",
                schema: "txn",
                table: "TransactionEvents",
                columns: new[] { "TransactionId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_NextInquiryAt",
                schema: "txn",
                table: "Transactions",
                column: "NextInquiryAt",
                filter: "[NextInquiryAt] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_OrderId_AttemptNo",
                schema: "txn",
                table: "Transactions",
                columns: new[] { "OrderId", "AttemptNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_OrderId_MerchantId",
                schema: "txn",
                table: "Transactions",
                columns: new[] { "OrderId", "MerchantId" });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_OrderId_Potential",
                schema: "txn",
                table: "Transactions",
                column: "OrderId",
                unique: true,
                filter: "[Status] IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_ProviderAccountId_Environment_ProviderReference",
                schema: "txn",
                table: "Transactions",
                columns: new[] { "ProviderAccountId", "Environment", "ProviderReference" },
                unique: true,
                filter: "[ProviderReference] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_ProviderAccountId_Environment_ProviderRequestReference",
                schema: "txn",
                table: "Transactions",
                columns: new[] { "ProviderAccountId", "Environment", "ProviderRequestReference" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Transactions_SuccessfulTransactionId_MerchantId",
                schema: "shop",
                table: "Orders",
                columns: new[] { "SuccessfulTransactionId", "MerchantId" },
                principalSchema: "txn",
                principalTable: "Transactions",
                principalColumns: new[] { "Id", "MerchantId" },
                onDelete: ReferentialAction.Restrict);

            // Runtime writes use the single pol_app principal. The base grants predate these tables, so
            // the forward migration must extend them without touching the legacy PaymentSessions grant.
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON txn.Transactions TO pol_app;");
            migrationBuilder.Sql("GRANT SELECT, INSERT ON txn.TransactionEvents TO pol_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Orders_Transactions_SuccessfulTransactionId_MerchantId",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropTable(
                name: "TransactionEvents",
                schema: "txn");

            migrationBuilder.DropTable(
                name: "Transactions",
                schema: "txn");

            migrationBuilder.DropIndex(
                name: "IX_Orders_SuccessfulTransactionId",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_SuccessfulTransactionId_MerchantId",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "SuccessfulTransactionId",
                schema: "shop",
                table: "Orders");
        }
    }
}
