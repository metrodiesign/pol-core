using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Task7NotificationRuntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationInboxMessages",
                schema: "txn",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "varchar(160)", unicode: false, maxLength: 160, nullable: false),
                    PayloadSnapshot = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationInboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationReviewNotes",
                schema: "txn",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeliveryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationReviewNotes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                schema: "txn",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "varchar(160)", unicode: false, maxLength: 160, nullable: false),
                    PayloadSnapshot = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RegistrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RegistrationAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OrderNo = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    TransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TransactionNo = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.UniqueConstraint("AK_Notifications_Id_MerchantId", x => new { x.Id, x.MerchantId });
                });

            migrationBuilder.CreateTable(
                name: "TemplateVersions",
                schema: "txn",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "varchar(160)", unicode: false, maxLength: 160, nullable: false),
                    Channel = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Version = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Locale = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReleasedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemplateVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Deliveries",
                schema: "txn",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    RecipientSnapshot = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    RecipientFingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    TemplateVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TemplateVersion = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    TemplateLocale = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    TemplateSubjectSnapshot = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    TemplateContentSnapshot = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EndpointUrlSnapshot = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    ProtectedEndpointSecretSnapshot = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PayloadSnapshot = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ProviderMessageId = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: true),
                    FailureCode = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deliveries", x => x.Id);
                    table.UniqueConstraint("AK_Deliveries_Id_MerchantId", x => new { x.Id, x.MerchantId });
                    table.CheckConstraint("CK_Deliveries_AttemptCount", "[AttemptCount] >= 0");
                    table.CheckConstraint("CK_Deliveries_Channel", "[Channel] IN ('email', 'sms', 'business_webhook')");
                    table.CheckConstraint("CK_Deliveries_Status", "[Status] IN (1, 2, 3, 4, 5, 6, 7, 8)");
                    table.ForeignKey(
                        name: "FK_Deliveries_Notifications_NotificationId_MerchantId",
                        columns: x => new { x.NotificationId, x.MerchantId },
                        principalSchema: "txn",
                        principalTable: "Notifications",
                        principalColumns: new[] { "Id", "MerchantId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Deliveries_TemplateVersions_TemplateVersionId",
                        column: x => x.TemplateVersionId,
                        principalSchema: "txn",
                        principalTable: "TemplateVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeliveryAttempts",
                schema: "txn",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptNo = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    ProviderMessageId = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: true),
                    FailureCode = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true),
                    LatencyMs = table.Column<int>(type: "int", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeliveryAttempts_Deliveries_DeliveryId_MerchantId",
                        columns: x => new { x.DeliveryId, x.MerchantId },
                        principalSchema: "txn",
                        principalTable: "Deliveries",
                        principalColumns: new[] { "Id", "MerchantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_MerchantId_SourceEventId",
                schema: "txn",
                table: "Deliveries",
                columns: new[] { "MerchantId", "SourceEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_MerchantId_Status_NextAttemptAt_LeaseExpiresAt",
                schema: "txn",
                table: "Deliveries",
                columns: new[] { "MerchantId", "Status", "NextAttemptAt", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_NotificationId_Channel_RecipientFingerprint",
                schema: "txn",
                table: "Deliveries",
                columns: new[] { "NotificationId", "Channel", "RecipientFingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_NotificationId_MerchantId",
                schema: "txn",
                table: "Deliveries",
                columns: new[] { "NotificationId", "MerchantId" });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_TemplateVersionId",
                schema: "txn",
                table: "Deliveries",
                column: "TemplateVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryAttempts_DeliveryId_AttemptNo",
                schema: "txn",
                table: "DeliveryAttempts",
                columns: new[] { "DeliveryId", "AttemptNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryAttempts_DeliveryId_MerchantId",
                schema: "txn",
                table: "DeliveryAttempts",
                columns: new[] { "DeliveryId", "MerchantId" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryAttempts_MerchantId_CompletedAt",
                schema: "txn",
                table: "DeliveryAttempts",
                columns: new[] { "MerchantId", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationInboxMessages_MerchantId_ReceivedAt",
                schema: "txn",
                table: "NotificationInboxMessages",
                columns: new[] { "MerchantId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationInboxMessages_SourceEventId",
                schema: "txn",
                table: "NotificationInboxMessages",
                column: "SourceEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationReviewNotes_DeliveryId_CreatedAt",
                schema: "txn",
                table: "NotificationReviewNotes",
                columns: new[] { "DeliveryId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationReviewNotes_MerchantId_CreatedAt",
                schema: "txn",
                table: "NotificationReviewNotes",
                columns: new[] { "MerchantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationReviewNotes_NotificationId_CreatedAt",
                schema: "txn",
                table: "NotificationReviewNotes",
                columns: new[] { "NotificationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_MerchantId_CorrelationId",
                schema: "txn",
                table: "Notifications",
                columns: new[] { "MerchantId", "CorrelationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_MerchantId_CreatedAt",
                schema: "txn",
                table: "Notifications",
                columns: new[] { "MerchantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_MerchantId_OrderNo",
                schema: "txn",
                table: "Notifications",
                columns: new[] { "MerchantId", "OrderNo" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_MerchantId_TransactionNo",
                schema: "txn",
                table: "Notifications",
                columns: new[] { "MerchantId", "TransactionNo" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_SourceEventId",
                schema: "txn",
                table: "Notifications",
                column: "SourceEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TemplateVersions_EventType_Channel_Version_Locale",
                schema: "txn",
                table: "TemplateVersions",
                columns: new[] { "EventType", "Channel", "Version", "Locale" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeliveryAttempts",
                schema: "txn");

            migrationBuilder.DropTable(
                name: "NotificationInboxMessages",
                schema: "txn");

            migrationBuilder.DropTable(
                name: "NotificationReviewNotes",
                schema: "txn");

            migrationBuilder.DropTable(
                name: "Deliveries",
                schema: "txn");

            migrationBuilder.DropTable(
                name: "Notifications",
                schema: "txn");

            migrationBuilder.DropTable(
                name: "TemplateVersions",
                schema: "txn");
        }
    }
}
