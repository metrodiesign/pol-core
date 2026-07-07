using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProducerSessionTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProducerAuthAudits",
                schema: "VCentralPay",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TenantUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProducerAuthAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProducerSessions",
                schema: "VCentralPay",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    TenantUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IdleExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AbsoluteExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SupersededAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SupersededBySessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedIp = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProducerSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProducerAuthAudits_TenantUserId",
                schema: "VCentralPay",
                table: "ProducerAuthAudits",
                column: "TenantUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProducerSessions_AbsoluteExpiresAt",
                schema: "VCentralPay",
                table: "ProducerSessions",
                column: "AbsoluteExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ProducerSessions_FamilyId",
                schema: "VCentralPay",
                table: "ProducerSessions",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_ProducerSessions_TenantUserId",
                schema: "VCentralPay",
                table: "ProducerSessions",
                column: "TenantUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProducerSessions_TokenHash",
                schema: "VCentralPay",
                table: "ProducerSessions",
                column: "TokenHash",
                unique: true);

            // Control-plane: pol_admin only, NO tenant RLS predicate, pol_app gets NO grant (mirrors the producer
            // identity tables). ProducerSessions needs UPDATE (rotate/revoke/slide) + DELETE (prune sweep, REQ-10.4).
            // ProducerAuthAudits is append-only (SELECT, INSERT — no UPDATE/DELETE, REQ-12.2/21).
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE, DELETE ON VCentralPay.ProducerSessions   TO pol_admin;
                GRANT SELECT, INSERT                 ON VCentralPay.ProducerAuthAudits  TO pol_admin;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                REVOKE SELECT, INSERT, UPDATE, DELETE ON VCentralPay.ProducerSessions   FROM pol_admin;
                REVOKE SELECT, INSERT                 ON VCentralPay.ProducerAuthAudits  FROM pol_admin;
                """);

            migrationBuilder.DropTable(
                name: "ProducerAuthAudits",
                schema: "VCentralPay");

            migrationBuilder.DropTable(
                name: "ProducerSessions",
                schema: "VCentralPay");
        }
    }
}
