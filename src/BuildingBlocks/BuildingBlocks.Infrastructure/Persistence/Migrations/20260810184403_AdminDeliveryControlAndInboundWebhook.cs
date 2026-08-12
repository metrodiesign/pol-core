using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminDeliveryControlAndInboundWebhook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiClients",
                schema: "iam",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PublicClientId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginatorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ScopesCsv = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    IpPolicy = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SecretHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    SecretHint = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    PendingRotationApprovalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PendingRotationTicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiClients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeliverySecretVersions",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProtectedSecret = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ActivatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RetiredAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliverySecretVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InboundWebhookEvents",
                schema: "txn",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PspConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PspCode = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    ExternalEventId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    PayloadFingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    SignatureValid = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    FailureCode = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboundWebhookEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InboundWebhookEvents_PspConnections_MerchantId_PspConnectionId",
                        columns: x => new { x.MerchantId, x.PspConnectionId },
                        principalSchema: "txn",
                        principalTable: "PspConnections",
                        principalColumns: new[] { "MerchantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NotificationDeliveries",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DestinationMasked = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    FailureCode = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDeliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationRules",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Destination = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    Threshold = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OneTimeSecretTickets",
                schema: "iam",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApiClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApprovalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TicketHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    ProtectedSecret = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OneTimeSecretTickets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebhookDeliveries",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalDeliveryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReplayKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EventType = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    TransactionId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LatencyMs = table.Column<int>(type: "int", nullable: true),
                    FailureCode = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookDeliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebhookEndpoints",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    EventsCsv = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    ActiveSecretVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SecretHint = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookEndpoints", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiClients_MerchantId_Status",
                schema: "iam",
                table: "ApiClients",
                columns: new[] { "MerchantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiClients_PendingRotationApprovalId",
                schema: "iam",
                table: "ApiClients",
                column: "PendingRotationApprovalId",
                unique: true,
                filter: "[PendingRotationApprovalId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ApiClients_PublicClientId",
                schema: "iam",
                table: "ApiClients",
                column: "PublicClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeliverySecretVersions_OwnerType_OwnerId_State",
                schema: "admin",
                table: "DeliverySecretVersions",
                columns: new[] { "OwnerType", "OwnerId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_InboundWebhookEvents_MerchantId_PspConnectionId",
                schema: "txn",
                table: "InboundWebhookEvents",
                columns: new[] { "MerchantId", "PspConnectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_InboundWebhookEvents_MerchantId_ReceivedAt",
                schema: "txn",
                table: "InboundWebhookEvents",
                columns: new[] { "MerchantId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InboundWebhookEvents_PspConnectionId_ExternalEventId",
                schema: "txn",
                table: "InboundWebhookEvents",
                columns: new[] { "PspConnectionId", "ExternalEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboundWebhookEvents_Status_ReceivedAt",
                schema: "txn",
                table: "InboundWebhookEvents",
                columns: new[] { "Status", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_MerchantId_SentAt",
                schema: "admin",
                table: "NotificationDeliveries",
                columns: new[] { "MerchantId", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_RuleId_SourceEventId",
                schema: "admin",
                table: "NotificationDeliveries",
                columns: new[] { "RuleId", "SourceEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRules_MerchantId_Enabled",
                schema: "admin",
                table: "NotificationRules",
                columns: new[] { "MerchantId", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_OneTimeSecretTickets_ApprovalId",
                schema: "iam",
                table: "OneTimeSecretTickets",
                column: "ApprovalId",
                unique: true,
                filter: "[ApprovalId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OneTimeSecretTickets_ExpiresAt",
                schema: "iam",
                table: "OneTimeSecretTickets",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_OneTimeSecretTickets_TicketHash",
                schema: "iam",
                table: "OneTimeSecretTickets",
                column: "TicketHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_EndpointId_SourceEventId",
                schema: "admin",
                table: "WebhookDeliveries",
                columns: new[] { "EndpointId", "SourceEventId" },
                unique: true,
                filter: "[OriginalDeliveryId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_MerchantId_Status_CreatedAt",
                schema: "admin",
                table: "WebhookDeliveries",
                columns: new[] { "MerchantId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_OriginalDeliveryId_ReplayKey",
                schema: "admin",
                table: "WebhookDeliveries",
                columns: new[] { "OriginalDeliveryId", "ReplayKey" },
                unique: true,
                filter: "[OriginalDeliveryId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_Status_NextAttemptAt_LeaseExpiresAt",
                schema: "admin",
                table: "WebhookDeliveries",
                columns: new[] { "Status", "NextAttemptAt", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEndpoints_MerchantId_Enabled",
                schema: "admin",
                table: "WebhookEndpoints",
                columns: new[] { "MerchantId", "Enabled" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiClients",
                schema: "iam");

            migrationBuilder.DropTable(
                name: "DeliverySecretVersions",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "InboundWebhookEvents",
                schema: "txn");

            migrationBuilder.DropTable(
                name: "NotificationDeliveries",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "NotificationRules",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "OneTimeSecretTickets",
                schema: "iam");

            migrationBuilder.DropTable(
                name: "WebhookDeliveries",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "WebhookEndpoints",
                schema: "admin");
        }
    }
}
