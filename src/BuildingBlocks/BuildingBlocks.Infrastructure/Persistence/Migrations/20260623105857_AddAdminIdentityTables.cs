using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminIdentityTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminAccountAudits",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActorType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetAdminId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAccountAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AdminAccounts",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Tier = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AdminTenantAssignments",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AdminAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedByAdminId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminTenantAssignments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminAccounts_Email",
                schema: "producer",
                table: "AdminAccounts",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdminAccounts_Subject",
                schema: "producer",
                table: "AdminAccounts",
                column: "Subject",
                unique: true,
                filter: "[Subject] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AdminTenantAssignments_AdminAccountId_TenantId",
                schema: "producer",
                table: "AdminTenantAssignments",
                columns: new[] { "AdminAccountId", "TenantId" },
                unique: true);

            // Admin tables are control-plane: pol_admin only, NO tenant RLS predicate, pol_app gets NO grant
            // (mirrors ProvisioningAudits / the child identity tables). AdminAccountAudits is append-only
            // (SELECT, INSERT — no UPDATE/DELETE); AdminTenantAssignments needs DELETE for unassign (REQ-4.2).
            // No fn_tenant_predicate is added: admin is cross-tenant by nature, and pol_app cannot touch these.
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE         ON producer.AdminAccounts          TO pol_admin;
                GRANT SELECT, INSERT, UPDATE, DELETE ON producer.AdminTenantAssignments TO pol_admin;
                GRANT SELECT, INSERT                 ON producer.AdminAccountAudits      TO pol_admin;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                REVOKE SELECT, INSERT, UPDATE         ON producer.AdminAccounts          FROM pol_admin;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON producer.AdminTenantAssignments FROM pol_admin;
                REVOKE SELECT, INSERT                 ON producer.AdminAccountAudits      FROM pol_admin;
                """);

            migrationBuilder.DropTable(
                name: "AdminAccountAudits",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "AdminAccounts",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "AdminTenantAssignments",
                schema: "producer");
        }
    }
}
