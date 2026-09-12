using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MerchantUserPaymentMethodAccessExpand : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PaymentProviderId",
                schema: "txn",
                table: "PspConnections",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InitiatingAudience",
                schema: "shop",
                table: "Orders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InitiatingMerchantUserId",
                schema: "shop",
                table: "Orders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PaymentAuthorizationStates",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Mode = table.Column<int>(type: "int", nullable: false),
                    CutoffAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentAuthorizationStates", x => x.Id);
                    table.CheckConstraint("CK_PaymentAuthorizationStates_Singleton", "[Id] = 'f9000000-0000-4000-8000-000000000001'");
                });

            migrationBuilder.CreateTable(
                name: "PaymentCapabilityMigrationConflicts",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentCapabilityMigrationConflicts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentMethods",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentMethods", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentProviders",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    AdapterCode = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentProviders", x => x.Id);
                    table.UniqueConstraint("AK_PaymentProviders_Id_AdapterCode", x => new { x.Id, x.AdapterCode });
                });

            migrationBuilder.CreateTable(
                name: "MerchantPaymentMethods",
                schema: "txn",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentMethodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantPaymentMethods", x => x.Id);
                    table.UniqueConstraint("AK_MerchantPaymentMethods_MerchantId_PaymentMethodId", x => new { x.MerchantId, x.PaymentMethodId });
                    table.ForeignKey(
                        name: "FK_MerchantPaymentMethods_Merchants_MerchantId",
                        column: x => x.MerchantId,
                        principalSchema: "merch",
                        principalTable: "Merchants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MerchantPaymentMethods_PaymentMethods_PaymentMethodId",
                        column: x => x.PaymentMethodId,
                        principalSchema: "cfg",
                        principalTable: "PaymentMethods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaymentMethodOptionGroups",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentMethodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentMethodOptionGroups", x => x.Id);
                    table.UniqueConstraint("AK_PaymentMethodOptionGroups_Id_PaymentMethodId", x => new { x.Id, x.PaymentMethodId });
                    table.ForeignKey(
                        name: "FK_PaymentMethodOptionGroups_PaymentMethods_PaymentMethodId",
                        column: x => x.PaymentMethodId,
                        principalSchema: "cfg",
                        principalTable: "PaymentMethods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaymentProviderMethods",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentProviderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentMethodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentProviderMethods", x => x.Id);
                    table.UniqueConstraint("AK_PaymentProviderMethods_Id_PaymentMethodId", x => new { x.Id, x.PaymentMethodId });
                    table.UniqueConstraint("AK_PaymentProviderMethods_Id_PaymentProviderId_PaymentMethodId", x => new { x.Id, x.PaymentProviderId, x.PaymentMethodId });
                    table.ForeignKey(
                        name: "FK_PaymentProviderMethods_PaymentMethods_PaymentMethodId",
                        column: x => x.PaymentMethodId,
                        principalSchema: "cfg",
                        principalTable: "PaymentMethods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentProviderMethods_PaymentProviders_PaymentProviderId",
                        column: x => x.PaymentProviderId,
                        principalSchema: "cfg",
                        principalTable: "PaymentProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MerchantUserPaymentMethods",
                schema: "txn",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentMethodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantUserPaymentMethods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MerchantUserPaymentMethods_MerchantPaymentMethods_MerchantId_PaymentMethodId",
                        columns: x => new { x.MerchantId, x.PaymentMethodId },
                        principalSchema: "txn",
                        principalTable: "MerchantPaymentMethods",
                        principalColumns: new[] { "MerchantId", "PaymentMethodId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaymentMethodOptions",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentMethodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OptionGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentMethodOptions", x => x.Id);
                    table.UniqueConstraint("AK_PaymentMethodOptions_Id_PaymentMethodId", x => new { x.Id, x.PaymentMethodId });
                    table.ForeignKey(
                        name: "FK_PaymentMethodOptions_PaymentMethodOptionGroups_OptionGroupId_PaymentMethodId",
                        columns: x => new { x.OptionGroupId, x.PaymentMethodId },
                        principalSchema: "cfg",
                        principalTable: "PaymentMethodOptionGroups",
                        principalColumns: new[] { "Id", "PaymentMethodId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentMethodOptions_PaymentMethods_PaymentMethodId",
                        column: x => x.PaymentMethodId,
                        principalSchema: "cfg",
                        principalTable: "PaymentMethods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MerchantProviderAccountMethods",
                schema: "txn",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PspConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentProviderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentProviderMethodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentMethodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantProviderAccountMethods", x => x.Id);
                    table.UniqueConstraint("AK_MerchantProviderAccountMethods_Id_MerchantId_PspConnectionId_PaymentProviderId_PaymentProviderMethodId_PaymentMethodId", x => new { x.Id, x.MerchantId, x.PspConnectionId, x.PaymentProviderId, x.PaymentProviderMethodId, x.PaymentMethodId });
                    table.ForeignKey(
                        name: "FK_MerchantProviderAccountMethods_Merchants_MerchantId",
                        column: x => x.MerchantId,
                        principalSchema: "merch",
                        principalTable: "Merchants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MerchantProviderAccountMethods_PaymentMethods_PaymentMethodId",
                        column: x => x.PaymentMethodId,
                        principalSchema: "cfg",
                        principalTable: "PaymentMethods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MerchantProviderAccountMethods_PaymentProviderMethods_PaymentProviderMethodId_PaymentProviderId_PaymentMethodId",
                        columns: x => new { x.PaymentProviderMethodId, x.PaymentProviderId, x.PaymentMethodId },
                        principalSchema: "cfg",
                        principalTable: "PaymentProviderMethods",
                        principalColumns: new[] { "Id", "PaymentProviderId", "PaymentMethodId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MerchantProviderAccountMethods_PspConnections_MerchantId_PspConnectionId",
                        columns: x => new { x.MerchantId, x.PspConnectionId },
                        principalSchema: "txn",
                        principalTable: "PspConnections",
                        principalColumns: new[] { "MerchantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaymentProviderMethodOptions",
                schema: "cfg",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentProviderMethodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentMethodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentMethodOptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentProviderMethodOptions", x => x.Id);
                    table.UniqueConstraint("AK_PaymentProviderMethodOptions_Id_PaymentProviderMethodId_PaymentMethodId_PaymentMethodOptionId", x => new { x.Id, x.PaymentProviderMethodId, x.PaymentMethodId, x.PaymentMethodOptionId });
                    table.ForeignKey(
                        name: "FK_PaymentProviderMethodOptions_PaymentMethodOptions_PaymentMethodOptionId_PaymentMethodId",
                        columns: x => new { x.PaymentMethodOptionId, x.PaymentMethodId },
                        principalSchema: "cfg",
                        principalTable: "PaymentMethodOptions",
                        principalColumns: new[] { "Id", "PaymentMethodId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentProviderMethodOptions_PaymentProviderMethods_PaymentProviderMethodId_PaymentMethodId",
                        columns: x => new { x.PaymentProviderMethodId, x.PaymentMethodId },
                        principalSchema: "cfg",
                        principalTable: "PaymentProviderMethods",
                        principalColumns: new[] { "Id", "PaymentMethodId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MerchantProviderAccountMethodOptions",
                schema: "txn",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantProviderAccountMethodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PspConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentProviderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentProviderMethodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentMethodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentProviderMethodOptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentMethodOptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantProviderAccountMethodOptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MerchantProviderAccountMethodOptions_MerchantProviderAccountMethods_MerchantProviderAccountMethodId_MerchantId_PspConnection~",
                        columns: x => new { x.MerchantProviderAccountMethodId, x.MerchantId, x.PspConnectionId, x.PaymentProviderId, x.PaymentProviderMethodId, x.PaymentMethodId },
                        principalSchema: "txn",
                        principalTable: "MerchantProviderAccountMethods",
                        principalColumns: new[] { "Id", "MerchantId", "PspConnectionId", "PaymentProviderId", "PaymentProviderMethodId", "PaymentMethodId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MerchantProviderAccountMethodOptions_PaymentProviderMethodOptions_PaymentProviderMethodOptionId_PaymentProviderMethodId_Paym~",
                        columns: x => new { x.PaymentProviderMethodOptionId, x.PaymentProviderMethodId, x.PaymentMethodId, x.PaymentMethodOptionId },
                        principalSchema: "cfg",
                        principalTable: "PaymentProviderMethodOptions",
                        principalColumns: new[] { "Id", "PaymentProviderMethodId", "PaymentMethodId", "PaymentMethodOptionId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "cfg",
                table: "PaymentAuthorizationStates",
                columns: new[] { "Id", "CutoffAt", "Mode", "Version" },
                values: new object[] { new Guid("f9000000-0000-4000-8000-000000000001"), null, 1, 1L });

            migrationBuilder.InsertData(
                schema: "cfg",
                table: "PaymentMethods",
                columns: new[] { "Id", "Code", "IsActive", "Name", "Version" },
                values: new object[,]
                {
                    { new Guid("f1000000-0000-4000-8000-000000000001"), "card", true, "Card", 1L },
                    { new Guid("f1000000-0000-4000-8000-000000000002"), "promptpay", true, "PromptPay", 1L },
                    { new Guid("f1000000-0000-4000-8000-000000000003"), "installment", true, "Installment", 1L }
                });

            migrationBuilder.InsertData(
                schema: "cfg",
                table: "PaymentProviders",
                columns: new[] { "Id", "AdapterCode", "Code", "IsEnabled", "Name", "Version" },
                values: new object[,]
                {
                    { new Guid("f4000000-0000-4000-8000-000000000001"), 1, "2c2p", true, "2C2P", 1L },
                    { new Guid("f4000000-0000-4000-8000-000000000002"), 2, "omise", true, "Omise", 1L }
                });

            migrationBuilder.InsertData(
                schema: "cfg",
                table: "PaymentMethodOptionGroups",
                columns: new[] { "Id", "Code", "Name", "PaymentMethodId" },
                values: new object[] { new Guid("f2000000-0000-4000-8000-000000000001"), "BANK", "Bank", new Guid("f1000000-0000-4000-8000-000000000003") });

            migrationBuilder.InsertData(
                schema: "cfg",
                table: "PaymentProviderMethods",
                columns: new[] { "Id", "CreatedAt", "CreatedBy", "IsActive", "PaymentMethodId", "PaymentProviderId", "UpdatedAt", "UpdatedBy", "Version" },
                values: new object[,]
                {
                    { new Guid("f5000000-0000-4000-8000-000000000001"), new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), new Guid("f9000000-0000-4000-8000-000000000002"), true, new Guid("f1000000-0000-4000-8000-000000000001"), new Guid("f4000000-0000-4000-8000-000000000001"), null, null, 1L },
                    { new Guid("f5000000-0000-4000-8000-000000000002"), new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), new Guid("f9000000-0000-4000-8000-000000000002"), true, new Guid("f1000000-0000-4000-8000-000000000002"), new Guid("f4000000-0000-4000-8000-000000000001"), null, null, 1L },
                    { new Guid("f5000000-0000-4000-8000-000000000003"), new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), new Guid("f9000000-0000-4000-8000-000000000002"), true, new Guid("f1000000-0000-4000-8000-000000000003"), new Guid("f4000000-0000-4000-8000-000000000001"), null, null, 1L },
                    { new Guid("f5000000-0000-4000-8000-000000000004"), new DateTime(2026, 8, 17, 0, 0, 0, 0, DateTimeKind.Utc), new Guid("f9000000-0000-4000-8000-000000000002"), true, new Guid("f1000000-0000-4000-8000-000000000001"), new Guid("f4000000-0000-4000-8000-000000000002"), null, null, 1L }
                });

            migrationBuilder.InsertData(
                schema: "cfg",
                table: "PaymentMethodOptions",
                columns: new[] { "Id", "Code", "Name", "OptionGroupId", "PaymentMethodId" },
                values: new object[,]
                {
                    { new Guid("f3000000-0000-4000-8000-000000000001"), "KBANK", "KBANK", new Guid("f2000000-0000-4000-8000-000000000001"), new Guid("f1000000-0000-4000-8000-000000000003") },
                    { new Guid("f3000000-0000-4000-8000-000000000002"), "SCB", "SCB", new Guid("f2000000-0000-4000-8000-000000000001"), new Guid("f1000000-0000-4000-8000-000000000003") },
                    { new Guid("f3000000-0000-4000-8000-000000000003"), "KTC", "KTC", new Guid("f2000000-0000-4000-8000-000000000001"), new Guid("f1000000-0000-4000-8000-000000000003") },
                    { new Guid("f3000000-0000-4000-8000-000000000004"), "BAY", "BAY", new Guid("f2000000-0000-4000-8000-000000000001"), new Guid("f1000000-0000-4000-8000-000000000003") }
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Users_ActorMerchant",
                schema: "merch",
                table: "Users",
                sql: "[Status] NOT IN (2, 4) OR ([MerchantId] IS NOT NULL AND [MerchantId] <> '00000000-0000-0000-0000-000000000000')");

            migrationBuilder.CreateIndex(
                name: "IX_PspConnections_MerchantId_PaymentProviderId",
                schema: "txn",
                table: "PspConnections",
                columns: new[] { "MerchantId", "PaymentProviderId" },
                unique: true,
                filter: "[PaymentProviderId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PspConnections_PaymentProviderId_Psp",
                schema: "txn",
                table: "PspConnections",
                columns: new[] { "PaymentProviderId", "Psp" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_InitiatingMerchantUserId_MerchantId",
                schema: "shop",
                table: "Orders",
                columns: new[] { "InitiatingMerchantUserId", "MerchantId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Orders_InitiatingAudience",
                schema: "shop",
                table: "Orders",
                sql: "([InitiatingAudience] IS NULL AND [InitiatingMerchantUserId] IS NULL) OR ([InitiatingAudience] = 1 AND [InitiatingMerchantUserId] IS NOT NULL AND [OriginatorId] IS NULL) OR ([InitiatingAudience] = 2 AND [InitiatingMerchantUserId] IS NULL AND [OriginatorId] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Orders_PaymentChannel_Canonical",
                schema: "shop",
                table: "Orders",
                sql: "[PaymentChannel] IS NULL OR [PaymentChannel] IN ('card', 'promptpay', 'installment')");

            migrationBuilder.CreateIndex(
                name: "IX_MerchantPaymentMethods_PaymentMethodId",
                schema: "txn",
                table: "MerchantPaymentMethods",
                column: "PaymentMethodId");

            migrationBuilder.CreateIndex(
                name: "IX_MerchantProviderAccountMethodOptions_MerchantProviderAccountMethodId_MerchantId_PspConnectionId_PaymentProviderId_PaymentPro~",
                schema: "txn",
                table: "MerchantProviderAccountMethodOptions",
                columns: new[] { "MerchantProviderAccountMethodId", "MerchantId", "PspConnectionId", "PaymentProviderId", "PaymentProviderMethodId", "PaymentMethodId" });

            migrationBuilder.CreateIndex(
                name: "IX_MerchantProviderAccountMethodOptions_MerchantProviderAccountMethodId_PaymentMethodOptionId",
                schema: "txn",
                table: "MerchantProviderAccountMethodOptions",
                columns: new[] { "MerchantProviderAccountMethodId", "PaymentMethodOptionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MerchantProviderAccountMethodOptions_PaymentProviderMethodOptionId_PaymentProviderMethodId_PaymentMethodId_PaymentMethodOpti~",
                schema: "txn",
                table: "MerchantProviderAccountMethodOptions",
                columns: new[] { "PaymentProviderMethodOptionId", "PaymentProviderMethodId", "PaymentMethodId", "PaymentMethodOptionId" });

            migrationBuilder.CreateIndex(
                name: "IX_MerchantProviderAccountMethods_MerchantId_PspConnectionId",
                schema: "txn",
                table: "MerchantProviderAccountMethods",
                columns: new[] { "MerchantId", "PspConnectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_MerchantProviderAccountMethods_PaymentMethodId",
                schema: "txn",
                table: "MerchantProviderAccountMethods",
                column: "PaymentMethodId");

            migrationBuilder.CreateIndex(
                name: "IX_MerchantProviderAccountMethods_PaymentProviderMethodId_PaymentProviderId_PaymentMethodId",
                schema: "txn",
                table: "MerchantProviderAccountMethods",
                columns: new[] { "PaymentProviderMethodId", "PaymentProviderId", "PaymentMethodId" });

            migrationBuilder.CreateIndex(
                name: "IX_MerchantProviderAccountMethods_PspConnectionId_PaymentMethodId",
                schema: "txn",
                table: "MerchantProviderAccountMethods",
                columns: new[] { "PspConnectionId", "PaymentMethodId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MerchantUserPaymentMethods_MerchantId_PaymentMethodId",
                schema: "txn",
                table: "MerchantUserPaymentMethods",
                columns: new[] { "MerchantId", "PaymentMethodId" });

            migrationBuilder.CreateIndex(
                name: "IX_MerchantUserPaymentMethods_MerchantUserId_PaymentMethodId",
                schema: "txn",
                table: "MerchantUserPaymentMethods",
                columns: new[] { "MerchantUserId", "PaymentMethodId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentCapabilityMigrationConflicts_ResolvedAt_Kind",
                schema: "cfg",
                table: "PaymentCapabilityMigrationConflicts",
                columns: new[] { "ResolvedAt", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentMethodOptionGroups_PaymentMethodId_Code",
                schema: "cfg",
                table: "PaymentMethodOptionGroups",
                columns: new[] { "PaymentMethodId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentMethodOptions_OptionGroupId_Code",
                schema: "cfg",
                table: "PaymentMethodOptions",
                columns: new[] { "OptionGroupId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentMethodOptions_OptionGroupId_PaymentMethodId",
                schema: "cfg",
                table: "PaymentMethodOptions",
                columns: new[] { "OptionGroupId", "PaymentMethodId" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentMethodOptions_PaymentMethodId",
                schema: "cfg",
                table: "PaymentMethodOptions",
                column: "PaymentMethodId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentMethods_Code",
                schema: "cfg",
                table: "PaymentMethods",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProviderMethodOptions_PaymentMethodOptionId_PaymentMethodId",
                schema: "cfg",
                table: "PaymentProviderMethodOptions",
                columns: new[] { "PaymentMethodOptionId", "PaymentMethodId" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProviderMethodOptions_PaymentProviderMethodId_PaymentMethodId",
                schema: "cfg",
                table: "PaymentProviderMethodOptions",
                columns: new[] { "PaymentProviderMethodId", "PaymentMethodId" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProviderMethodOptions_PaymentProviderMethodId_PaymentMethodOptionId",
                schema: "cfg",
                table: "PaymentProviderMethodOptions",
                columns: new[] { "PaymentProviderMethodId", "PaymentMethodOptionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProviderMethods_PaymentMethodId",
                schema: "cfg",
                table: "PaymentProviderMethods",
                column: "PaymentMethodId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProviderMethods_PaymentProviderId_PaymentMethodId",
                schema: "cfg",
                table: "PaymentProviderMethods",
                columns: new[] { "PaymentProviderId", "PaymentMethodId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProviders_AdapterCode",
                schema: "cfg",
                table: "PaymentProviders",
                column: "AdapterCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProviders_Code",
                schema: "cfg",
                table: "PaymentProviders",
                column: "Code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_PspConnections_Merchants_MerchantId",
                schema: "txn",
                table: "PspConnections",
                column: "MerchantId",
                principalSchema: "merch",
                principalTable: "Merchants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PspConnections_PaymentProviders_PaymentProviderId_Psp",
                schema: "txn",
                table: "PspConnections",
                columns: new[] { "PaymentProviderId", "Psp" },
                principalSchema: "cfg",
                principalTable: "PaymentProviders",
                principalColumns: new[] { "Id", "AdapterCode" },
                onDelete: ReferentialAction.Restrict);

            // EF alternate keys make nullable key properties required. Keep the expand columns nullable,
            // then express the candidate keys and tenant-bound FKs directly in SQL Server.
            migrationBuilder.Sql("""
                ALTER TABLE merch.Users ADD CONSTRAINT UQ_Users_Id_MerchantId
                    UNIQUE (Id, MerchantId);
                ALTER TABLE txn.PspConnections ADD CONSTRAINT UQ_PspConnections_Id_MerchantId_PaymentProviderId
                    UNIQUE (Id, MerchantId, PaymentProviderId);

                ALTER TABLE txn.MerchantUserPaymentMethods ADD CONSTRAINT FK_MerchantUserPaymentMethods_Users_User_Merchant
                    FOREIGN KEY (MerchantUserId, MerchantId)
                    REFERENCES merch.Users (Id, MerchantId);
                ALTER TABLE txn.MerchantProviderAccountMethods ADD CONSTRAINT FK_MerchantProviderAccountMethods_PspConnections_Account_Provider
                    FOREIGN KEY (PspConnectionId, MerchantId, PaymentProviderId)
                    REFERENCES txn.PspConnections (Id, MerchantId, PaymentProviderId);
                ALTER TABLE shop.Orders ADD CONSTRAINT FK_Orders_Users_Initiator_Merchant
                    FOREIGN KEY (InitiatingMerchantUserId, MerchantId)
                    REFERENCES merch.Users (Id, MerchantId);
                """);

            migrationBuilder.Sql("""
                IF USER_ID(N'pol_app') IS NULL
                    THROW 51003, N'MerchantUserPaymentMethodAccessExpand requires database user [pol_app].', 1;

                GRANT SELECT, INSERT, UPDATE ON cfg.PaymentMethods TO pol_app;
                GRANT SELECT, INSERT, UPDATE ON cfg.PaymentMethodOptionGroups TO pol_app;
                GRANT SELECT, INSERT, UPDATE ON cfg.PaymentMethodOptions TO pol_app;
                GRANT SELECT, INSERT, UPDATE ON cfg.PaymentProviders TO pol_app;
                GRANT SELECT, INSERT, UPDATE ON cfg.PaymentProviderMethods TO pol_app;
                GRANT SELECT, INSERT, UPDATE ON cfg.PaymentProviderMethodOptions TO pol_app;
                GRANT SELECT, UPDATE ON cfg.PaymentAuthorizationStates TO pol_app;
                GRANT SELECT, INSERT, UPDATE ON cfg.PaymentCapabilityMigrationConflicts TO pol_app;

                GRANT SELECT, INSERT, UPDATE ON txn.MerchantProviderAccountMethods TO pol_app;
                GRANT SELECT, INSERT, UPDATE ON txn.MerchantProviderAccountMethodOptions TO pol_app;
                GRANT SELECT, INSERT, UPDATE ON txn.MerchantPaymentMethods TO pol_app;
                GRANT SELECT, INSERT, UPDATE ON txn.MerchantUserPaymentMethods TO pol_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF USER_ID(N'pol_app') IS NOT NULL
                BEGIN
                    REVOKE SELECT, INSERT, UPDATE ON cfg.PaymentMethods FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE ON cfg.PaymentMethodOptionGroups FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE ON cfg.PaymentMethodOptions FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE ON cfg.PaymentProviders FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE ON cfg.PaymentProviderMethods FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE ON cfg.PaymentProviderMethodOptions FROM pol_app;
                    REVOKE SELECT, UPDATE ON cfg.PaymentAuthorizationStates FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE ON cfg.PaymentCapabilityMigrationConflicts FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE ON txn.MerchantProviderAccountMethods FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE ON txn.MerchantProviderAccountMethodOptions FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE ON txn.MerchantPaymentMethods FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE ON txn.MerchantUserPaymentMethods FROM pol_app;
                END

                ALTER TABLE shop.Orders DROP CONSTRAINT FK_Orders_Users_Initiator_Merchant;
                ALTER TABLE txn.MerchantProviderAccountMethods DROP CONSTRAINT FK_MerchantProviderAccountMethods_PspConnections_Account_Provider;
                ALTER TABLE txn.MerchantUserPaymentMethods DROP CONSTRAINT FK_MerchantUserPaymentMethods_Users_User_Merchant;
                ALTER TABLE txn.PspConnections DROP CONSTRAINT UQ_PspConnections_Id_MerchantId_PaymentProviderId;
                ALTER TABLE merch.Users DROP CONSTRAINT UQ_Users_Id_MerchantId;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_PspConnections_Merchants_MerchantId",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropForeignKey(
                name: "FK_PspConnections_PaymentProviders_PaymentProviderId_Psp",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropTable(
                name: "MerchantProviderAccountMethodOptions",
                schema: "txn");

            migrationBuilder.DropTable(
                name: "MerchantUserPaymentMethods",
                schema: "txn");

            migrationBuilder.DropTable(
                name: "PaymentAuthorizationStates",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "PaymentCapabilityMigrationConflicts",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "MerchantProviderAccountMethods",
                schema: "txn");

            migrationBuilder.DropTable(
                name: "PaymentProviderMethodOptions",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "MerchantPaymentMethods",
                schema: "txn");

            migrationBuilder.DropTable(
                name: "PaymentMethodOptions",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "PaymentProviderMethods",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "PaymentMethodOptionGroups",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "PaymentProviders",
                schema: "cfg");

            migrationBuilder.DropTable(
                name: "PaymentMethods",
                schema: "cfg");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Users_ActorMerchant",
                schema: "merch",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_PspConnections_MerchantId_PaymentProviderId",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropIndex(
                name: "IX_PspConnections_PaymentProviderId_Psp",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropIndex(
                name: "IX_Orders_InitiatingMerchantUserId_MerchantId",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Orders_InitiatingAudience",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Orders_PaymentChannel_Canonical",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PaymentProviderId",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropColumn(
                name: "InitiatingAudience",
                schema: "shop",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "InitiatingMerchantUserId",
                schema: "shop",
                table: "Orders");
        }
    }
}
