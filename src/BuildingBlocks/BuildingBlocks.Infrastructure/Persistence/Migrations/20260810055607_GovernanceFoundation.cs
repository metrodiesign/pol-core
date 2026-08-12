using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GovernanceFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApprovalRequests",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScopeKind = table.Column<int>(type: "int", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<string>(type: "varchar(120)", unicode: false, maxLength: 120, nullable: false),
                    RequiredPermission = table.Column<string>(type: "varchar(120)", unicode: false, maxLength: 120, nullable: false),
                    MakerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetType = table.Column<string>(type: "varchar(120)", unicode: false, maxLength: 120, nullable: false),
                    TargetId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TargetVersion = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CheckerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecisionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExecutionOutcome = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ExecutedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CorrelationId = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalRequests", x => x.Id);
                    table.CheckConstraint("CK_ApprovalRequests_Scope", "([ScopeKind] = 1 AND [MerchantId] IS NULL) OR ([ScopeKind] = 2 AND [MerchantId] IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "AuditHeads",
                schema: "admin",
                columns: table => new
                {
                    ScopeKey = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: false),
                    ScopeKind = table.Column<int>(type: "int", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastSequence = table.Column<long>(type: "bigint", nullable: false),
                    LastHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditHeads", x => x.ScopeKey);
                    table.CheckConstraint("CK_AuditHeads_Scope", "([ScopeKind] = 1 AND [MerchantId] IS NULL) OR ([ScopeKind] = 2 AND [MerchantId] IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "GovernanceOutboxMessages",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScopeKind = table.Column<int>(type: "int", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Type = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    SchemaVersion = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GovernanceOutboxMessages", x => x.Id);
                    table.CheckConstraint("CK_GovernanceOutboxMessages_Scope", "([ScopeKind] = 1 AND [MerchantId] IS NULL) OR ([ScopeKind] = 2 AND [MerchantId] IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "OperationRecords",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "varchar(120)", unicode: false, maxLength: 120, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    RequestHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ScopeKind = table.Column<int>(type: "int", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ResponseStatus = table.Column<int>(type: "int", nullable: true),
                    ResponseBody = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationRecords", x => x.Id);
                    table.CheckConstraint("CK_OperationRecords_Scope", "([ScopeKind] = 1 AND [MerchantId] IS NULL) OR ([ScopeKind] = 2 AND [MerchantId] IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "ApprovalEvents",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApprovalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScopeKind = table.Column<int>(type: "int", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kind = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CorrelationId = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalEvents", x => x.Id);
                    table.CheckConstraint("CK_ApprovalEvents_Scope", "([ScopeKind] = 1 AND [MerchantId] IS NULL) OR ([ScopeKind] = 2 AND [MerchantId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_ApprovalEvents_ApprovalRequests_ApprovalId",
                        column: x => x.ApprovalId,
                        principalSchema: "admin",
                        principalTable: "ApprovalRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AuditRecords",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScopeKey = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: false),
                    ScopeKind = table.Column<int>(type: "int", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "varchar(120)", unicode: false, maxLength: 120, nullable: false),
                    ResourceType = table.Column<string>(type: "varchar(120)", unicode: false, maxLength: 120, nullable: false),
                    ResourceId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Result = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: false),
                    Changes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ApprovalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResourceVersion = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: true),
                    CorrelationId = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PreviousHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    Hash = table.Column<byte[]>(type: "binary(32)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditRecords", x => x.Id);
                    table.CheckConstraint("CK_AuditRecords_Scope", "([ScopeKind] = 1 AND [MerchantId] IS NULL) OR ([ScopeKind] = 2 AND [MerchantId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_AuditRecords_AuditHeads_ScopeKey",
                        column: x => x.ScopeKey,
                        principalSchema: "admin",
                        principalTable: "AuditHeads",
                        principalColumn: "ScopeKey",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalEvents_ApprovalId_OccurredAt",
                schema: "admin",
                table: "ApprovalEvents",
                columns: new[] { "ApprovalId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalEvents_SourceEventId",
                schema: "admin",
                table: "ApprovalEvents",
                column: "SourceEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_MerchantId_CreatedAt",
                schema: "admin",
                table: "ApprovalRequests",
                columns: new[] { "MerchantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_Status_CreatedAt",
                schema: "admin",
                table: "ApprovalRequests",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditHeads_ScopeKind_MerchantId",
                schema: "admin",
                table: "AuditHeads",
                columns: new[] { "ScopeKind", "MerchantId" },
                unique: true,
                filter: "[MerchantId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AuditRecords_Action_OccurredAt",
                schema: "admin",
                table: "AuditRecords",
                columns: new[] { "Action", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditRecords_ActorId_OccurredAt",
                schema: "admin",
                table: "AuditRecords",
                columns: new[] { "ActorId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditRecords_ScopeKey_PreviousHash",
                schema: "admin",
                table: "AuditRecords",
                columns: new[] { "ScopeKey", "PreviousHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditRecords_ScopeKey_Sequence",
                schema: "admin",
                table: "AuditRecords",
                columns: new[] { "ScopeKey", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GovernanceOutboxMessages_ProcessedAt_LeaseExpiresAt",
                schema: "admin",
                table: "GovernanceOutboxMessages",
                columns: new[] { "ProcessedAt", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OperationRecords_ActorId_Operation_IdempotencyKey",
                schema: "admin",
                table: "OperationRecords",
                columns: new[] { "ActorId", "Operation", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperationRecords_ExpiresAt",
                schema: "admin",
                table: "OperationRecords",
                column: "ExpiresAt");

            // Least privilege: immutable history has no UPDATE/DELETE grant; only bounded operation records
            // may be deleted by their TTL worker.
            migrationBuilder.Sql("""
                IF USER_ID(N'pol_app') IS NULL
                    THROW 51003, N'GovernanceFoundation requires database user [pol_app].', 1;

                GRANT SELECT, INSERT, UPDATE         ON admin.ApprovalRequests         TO pol_app;
                GRANT SELECT, INSERT                 ON admin.ApprovalEvents           TO pol_app;
                GRANT SELECT, INSERT, UPDATE         ON admin.GovernanceOutboxMessages TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON admin.OperationRecords         TO pol_app;
                GRANT SELECT, INSERT, UPDATE         ON admin.AuditHeads               TO pol_app;
                GRANT SELECT, INSERT                 ON admin.AuditRecords             TO pol_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF USER_ID(N'pol_app') IS NOT NULL
                BEGIN
                    REVOKE SELECT, INSERT, UPDATE         ON admin.ApprovalRequests         FROM pol_app;
                    REVOKE SELECT, INSERT                 ON admin.ApprovalEvents           FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE         ON admin.GovernanceOutboxMessages FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE, DELETE ON admin.OperationRecords         FROM pol_app;
                    REVOKE SELECT, INSERT, UPDATE         ON admin.AuditHeads               FROM pol_app;
                    REVOKE SELECT, INSERT                 ON admin.AuditRecords             FROM pol_app;
                END
                """);

            migrationBuilder.DropTable(
                name: "ApprovalEvents",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "AuditRecords",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "GovernanceOutboxMessages",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "OperationRecords",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "ApprovalRequests",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "AuditHeads",
                schema: "admin");
        }
    }
}
