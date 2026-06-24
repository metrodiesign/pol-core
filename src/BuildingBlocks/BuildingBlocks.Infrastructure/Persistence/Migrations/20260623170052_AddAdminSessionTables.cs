using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminSessionTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminAuthAudits",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    AdminAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAuthAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AdminSessions",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    AdminAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                    table.PrimaryKey("PK_AdminSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminAuthAudits_AdminAccountId",
                schema: "producer",
                table: "AdminAuthAudits",
                column: "AdminAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminSessions_AbsoluteExpiresAt",
                schema: "producer",
                table: "AdminSessions",
                column: "AbsoluteExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_AdminSessions_AdminAccountId",
                schema: "producer",
                table: "AdminSessions",
                column: "AdminAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminSessions_FamilyId",
                schema: "producer",
                table: "AdminSessions",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminSessions_TokenHash",
                schema: "producer",
                table: "AdminSessions",
                column: "TokenHash",
                unique: true);

            // Control-plane: pol_admin only, NO tenant RLS predicate, pol_app gets NO grant (mirrors the admin
            // identity tables). AdminSessions needs UPDATE (rotate/revoke/slide) + DELETE (prune sweep, REQ-11.5).
            // AdminAuthAudits is append-only (SELECT, INSERT — no UPDATE/DELETE, REQ-12.2).
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE, DELETE ON producer.AdminSessions   TO pol_admin;
                GRANT SELECT, INSERT                 ON producer.AdminAuthAudits  TO pol_admin;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                REVOKE SELECT, INSERT, UPDATE, DELETE ON producer.AdminSessions   FROM pol_admin;
                REVOKE SELECT, INSERT                 ON producer.AdminAuthAudits  FROM pol_admin;
                """);

            migrationBuilder.DropTable(
                name: "AdminAuthAudits",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "AdminSessions",
                schema: "producer");
        }
    }
}
