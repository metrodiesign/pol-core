using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminTenantPspRoutingControlPlane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ActiveSecretVersionId",
                schema: "txn",
                table: "PspConnections",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Health",
                schema: "txn",
                table: "PspConnections",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "LastTestResult",
                schema: "txn",
                table: "PspConnections",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastTestedAt",
                schema: "txn",
                table: "PspConnections",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PendingApprovalId",
                schema: "txn",
                table: "PspConnections",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PendingSecretVersionId",
                schema: "txn",
                table: "PspConnections",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                schema: "txn",
                table: "PspConnections",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                schema: "merch",
                table: "Merchants",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_PspConnections_MerchantId_Id",
                schema: "txn",
                table: "PspConnections",
                columns: new[] { "MerchantId", "Id" });

            migrationBuilder.CreateTable(
                name: "AdminOperationRecords",
                schema: "txn",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IntentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    HttpStatus = table.Column<int>(type: "int", nullable: true),
                    Result = table.Column<string>(type: "nvarchar(max)", maxLength: 16384, nullable: true),
                    ResourceId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminOperationRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Originators",
                schema: "merch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    SaleCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ApiClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Originators", x => x.Id);
                    table.UniqueConstraint("AK_Originators_MerchantId_Id", x => new { x.MerchantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Originators_Merchants_MerchantId",
                        column: x => x.MerchantId,
                        principalSchema: "merch",
                        principalTable: "Merchants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RoutingRulesets",
                schema: "txn",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ApprovalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoutingRulesets", x => x.Id);
                    table.UniqueConstraint("AK_RoutingRulesets_MerchantId_Id", x => new { x.MerchantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "VaultSecretVersions",
                schema: "merch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SecretName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    SecretKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EncryptedDek = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    EncryptedSecret = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Hint = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ActivatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RetiredAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultSecretVersions", x => x.Id);
                    table.UniqueConstraint("AK_VaultSecretVersions_MerchantId_Id", x => new { x.MerchantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "RoutingRules",
                schema: "txn",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RulesetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    OriginatorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MinAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    MaxAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    TargetConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FallbackConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoutingRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoutingRules_Originators_MerchantId_OriginatorId",
                        columns: x => new { x.MerchantId, x.OriginatorId },
                        principalSchema: "merch",
                        principalTable: "Originators",
                        principalColumns: new[] { "MerchantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RoutingRules_PspConnections_MerchantId_FallbackConnectionId",
                        columns: x => new { x.MerchantId, x.FallbackConnectionId },
                        principalSchema: "txn",
                        principalTable: "PspConnections",
                        principalColumns: new[] { "MerchantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RoutingRules_PspConnections_MerchantId_TargetConnectionId",
                        columns: x => new { x.MerchantId, x.TargetConnectionId },
                        principalSchema: "txn",
                        principalTable: "PspConnections",
                        principalColumns: new[] { "MerchantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RoutingRules_RoutingRulesets_MerchantId_RulesetId",
                        columns: x => new { x.MerchantId, x.RulesetId },
                        principalSchema: "txn",
                        principalTable: "RoutingRulesets",
                        principalColumns: new[] { "MerchantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PspConnections_MerchantId_ActiveSecretVersionId",
                schema: "txn",
                table: "PspConnections",
                columns: new[] { "MerchantId", "ActiveSecretVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_PspConnections_MerchantId_PendingSecretVersionId",
                schema: "txn",
                table: "PspConnections",
                columns: new[] { "MerchantId", "PendingSecretVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_AdminOperationRecords_ExpiresAt",
                schema: "txn",
                table: "AdminOperationRecords",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_AdminOperationRecords_MerchantId_ActorId_Operation_IdempotencyKey",
                schema: "txn",
                table: "AdminOperationRecords",
                columns: new[] { "MerchantId", "ActorId", "Operation", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Originators_MerchantId_Code",
                schema: "merch",
                table: "Originators",
                columns: new[] { "MerchantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoutingRules_MerchantId_FallbackConnectionId",
                schema: "txn",
                table: "RoutingRules",
                columns: new[] { "MerchantId", "FallbackConnectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_RoutingRules_MerchantId_OriginatorId",
                schema: "txn",
                table: "RoutingRules",
                columns: new[] { "MerchantId", "OriginatorId" });

            migrationBuilder.CreateIndex(
                name: "IX_RoutingRules_MerchantId_RulesetId_Priority",
                schema: "txn",
                table: "RoutingRules",
                columns: new[] { "MerchantId", "RulesetId", "Priority" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoutingRules_MerchantId_TargetConnectionId",
                schema: "txn",
                table: "RoutingRules",
                columns: new[] { "MerchantId", "TargetConnectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_RoutingRulesets_MerchantId_Status",
                schema: "txn",
                table: "RoutingRulesets",
                columns: new[] { "MerchantId", "Status" },
                unique: true,
                filter: "[Status] = 3");

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecretVersions_MerchantId_SecretName_State",
                schema: "merch",
                table: "VaultSecretVersions",
                columns: new[] { "MerchantId", "SecretName", "State" },
                unique: true,
                filter: "[State] = 2");

            migrationBuilder.CreateIndex(
                name: "IX_VaultSecretVersions_MerchantId_SecretName_Version",
                schema: "merch",
                table: "VaultSecretVersions",
                columns: new[] { "MerchantId", "SecretName", "Version" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_PspConnections_VaultSecretVersions_MerchantId_ActiveSecretVersionId",
                schema: "txn",
                table: "PspConnections",
                columns: new[] { "MerchantId", "ActiveSecretVersionId" },
                principalSchema: "merch",
                principalTable: "VaultSecretVersions",
                principalColumns: new[] { "MerchantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PspConnections_VaultSecretVersions_MerchantId_PendingSecretVersionId",
                schema: "txn",
                table: "PspConnections",
                columns: new[] { "MerchantId", "PendingSecretVersionId" },
                principalSchema: "merch",
                principalTable: "VaultSecretVersions",
                principalColumns: new[] { "MerchantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(
                """
                ALTER TABLE txn.PspConnections ADD CONSTRAINT CK_PspConnections_AdminControl
                    CHECK ([Health] IN (1,2,3) AND [Version] >= 1
                       AND (([PendingSecretVersionId] IS NULL AND [PendingApprovalId] IS NULL)
                         OR ([PendingSecretVersionId] IS NOT NULL AND [PendingApprovalId] IS NOT NULL))
                       AND ([ActiveSecretVersionId] IS NULL OR [PendingSecretVersionId] IS NULL
                         OR [ActiveSecretVersionId] <> [PendingSecretVersionId]));
                ALTER TABLE merch.Merchants ADD CONSTRAINT CK_Merchants_Version CHECK ([Version] >= 1);
                ALTER TABLE txn.AdminOperationRecords ADD CONSTRAINT CK_AdminOperationRecords_State
                    CHECK ([MerchantId] <> '00000000-0000-0000-0000-000000000000'
                       AND [ActorId] <> '00000000-0000-0000-0000-000000000000'
                       AND [State] IN (1,2,3));
                ALTER TABLE merch.Originators ADD CONSTRAINT CK_Originators_Control
                    CHECK ([MerchantId] <> '00000000-0000-0000-0000-000000000000'
                       AND [Type] IN (1,2,3,4,5) AND [Status] IN (1,2) AND [Version] >= 1);
                ALTER TABLE txn.RoutingRulesets ADD CONSTRAINT CK_RoutingRulesets_Control
                    CHECK ([MerchantId] <> '00000000-0000-0000-0000-000000000000'
                       AND [Status] IN (1,2,3,4) AND [Version] >= 1);
                ALTER TABLE txn.RoutingRules ADD CONSTRAINT CK_RoutingRules_Control
                    CHECK ([MerchantId] <> '00000000-0000-0000-0000-000000000000'
                       AND [Priority] > 0 AND [TargetConnectionId] <> '00000000-0000-0000-0000-000000000000'
                       AND ([FallbackConnectionId] IS NULL OR [FallbackConnectionId] <> [TargetConnectionId])
                       AND ([MinAmount] IS NULL OR [MinAmount] >= 0)
                       AND ([MaxAmount] IS NULL OR [MaxAmount] >= 0)
                       AND ([MinAmount] IS NULL OR [MaxAmount] IS NULL OR [MinAmount] <= [MaxAmount]));
                ALTER TABLE merch.VaultSecretVersions ADD CONSTRAINT CK_VaultSecretVersions_Control
                    CHECK ([MerchantId] <> '00000000-0000-0000-0000-000000000000'
                       AND [Version] >= 1 AND [State] IN (1,2,3,4));

                GRANT UPDATE ON merch.Merchants TO pol_app;
                GRANT UPDATE ON txn.PspConnections TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON merch.Originators TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON txn.RoutingRulesets TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON txn.RoutingRules TO pol_app;
                GRANT SELECT, INSERT, UPDATE ON merch.VaultSecretVersions TO pol_app;
                GRANT SELECT, INSERT, DELETE ON txn.AdminOperationRecords TO pol_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                REVOKE SELECT, INSERT, DELETE ON txn.AdminOperationRecords FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE ON merch.VaultSecretVersions FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON txn.RoutingRules FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON txn.RoutingRulesets FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON merch.Originators FROM pol_app;
                REVOKE UPDATE ON txn.PspConnections FROM pol_app;
                REVOKE UPDATE ON merch.Merchants FROM pol_app;

                ALTER TABLE txn.PspConnections DROP CONSTRAINT CK_PspConnections_AdminControl;
                ALTER TABLE merch.Merchants DROP CONSTRAINT CK_Merchants_Version;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_PspConnections_VaultSecretVersions_MerchantId_ActiveSecretVersionId",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropForeignKey(
                name: "FK_PspConnections_VaultSecretVersions_MerchantId_PendingSecretVersionId",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropTable(
                name: "AdminOperationRecords",
                schema: "txn");

            migrationBuilder.DropTable(
                name: "RoutingRules",
                schema: "txn");

            migrationBuilder.DropTable(
                name: "VaultSecretVersions",
                schema: "merch");

            migrationBuilder.DropTable(
                name: "Originators",
                schema: "merch");

            migrationBuilder.DropTable(
                name: "RoutingRulesets",
                schema: "txn");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_PspConnections_MerchantId_Id",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropIndex(
                name: "IX_PspConnections_MerchantId_ActiveSecretVersionId",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropIndex(
                name: "IX_PspConnections_MerchantId_PendingSecretVersionId",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropColumn(
                name: "ActiveSecretVersionId",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropColumn(
                name: "Health",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropColumn(
                name: "LastTestResult",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropColumn(
                name: "LastTestedAt",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropColumn(
                name: "PendingApprovalId",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropColumn(
                name: "PendingSecretVersionId",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "txn",
                table: "PspConnections");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "merch",
                table: "Merchants");
        }
    }
}
