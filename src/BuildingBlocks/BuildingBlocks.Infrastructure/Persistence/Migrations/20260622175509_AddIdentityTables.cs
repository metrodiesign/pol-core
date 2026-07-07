using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentityTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExternalLogins",
                schema: "VCentralPay",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    TenantUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalLogins", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegistrationAudits",
                schema: "VCentralPay",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AdminSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    TargetSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrationAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegistrationTickets",
                schema: "VCentralPay",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    HostedDomain = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UsedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrationTickets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantUserProfiles",
                schema: "VCentralPay",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantUserProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantUsers",
                schema: "VCentralPay",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Role = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantUsers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalLogins_Provider_Subject",
                schema: "VCentralPay",
                table: "ExternalLogins",
                columns: new[] { "Provider", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantUserProfiles_TenantUserId",
                schema: "VCentralPay",
                table: "TenantUserProfiles",
                column: "TenantUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantUsers_Subject",
                schema: "VCentralPay",
                table: "TenantUsers",
                column: "Subject",
                unique: true);

            // TenantUsers joins the existing RLS floor additively, scoped on its (nullable) TenantId. A
            // PendingApproval row has TenantId NULL -> NULL = SESSION_CONTEXT is UNKNOWN -> hidden from
            // pol_app (a pending user is correctly invisible to tenants; only pol_admin bypass sees it for
            // approval). FILTER = own-tenant read once approved; BLOCK = a tenant cannot forge a foreign id.
            migrationBuilder.Sql(
                "ALTER SECURITY POLICY VCentralPay.TenantIsolationPolicy\n" +
                "    ADD FILTER PREDICATE VCentralPay.fn_tenant_predicate(TenantId) ON VCentralPay.TenantUsers,\n" +
                "    ADD BLOCK PREDICATE VCentralPay.fn_tenant_predicate(TenantId) ON VCentralPay.TenantUsers AFTER INSERT,\n" +
                "    ADD BLOCK PREDICATE VCentralPay.fn_tenant_predicate(TenantId) ON VCentralPay.TenantUsers AFTER UPDATE;");

            // The child identity tables (ExternalLogins/Profiles/RegistrationTickets/RegistrationAudits) are
            // admin-only control-plane: pol_app gets NO grant, so they need no per-tenant predicate (a tenant
            // principal cannot touch them at all). pol_admin (bypass) owns registration/approval/resolve.
            migrationBuilder.Sql("""
                -- pol_app (TenantConsole): read its OWN tenant's users (RLS-filtered). No write; no child tables.
                GRANT SELECT ON VCentralPay.TenantUsers TO pol_app;

                -- pol_admin (registration/approval/resolve, bypass role): cross-tenant on the identity tables.
                GRANT SELECT, INSERT, UPDATE ON VCentralPay.TenantUsers         TO pol_admin;
                GRANT SELECT, INSERT         ON VCentralPay.ExternalLogins       TO pol_admin;
                GRANT SELECT, INSERT         ON VCentralPay.TenantUserProfiles   TO pol_admin;
                GRANT SELECT, INSERT, UPDATE ON VCentralPay.RegistrationTickets  TO pol_admin;
                GRANT SELECT, INSERT         ON VCentralPay.RegistrationAudits    TO pol_admin;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Detach the TenantUsers predicates from the policy BEFORE dropping the table (a table under a
            // security policy cannot be dropped); never drop the policy itself.
            migrationBuilder.Sql(
                "ALTER SECURITY POLICY VCentralPay.TenantIsolationPolicy\n" +
                "    DROP FILTER PREDICATE ON VCentralPay.TenantUsers,\n" +
                "    DROP BLOCK PREDICATE ON VCentralPay.TenantUsers AFTER INSERT,\n" +
                "    DROP BLOCK PREDICATE ON VCentralPay.TenantUsers AFTER UPDATE;");

            migrationBuilder.Sql("""
                REVOKE SELECT ON VCentralPay.TenantUsers FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE ON VCentralPay.TenantUsers         FROM pol_admin;
                REVOKE SELECT, INSERT         ON VCentralPay.ExternalLogins       FROM pol_admin;
                REVOKE SELECT, INSERT         ON VCentralPay.TenantUserProfiles   FROM pol_admin;
                REVOKE SELECT, INSERT, UPDATE ON VCentralPay.RegistrationTickets  FROM pol_admin;
                REVOKE SELECT, INSERT         ON VCentralPay.RegistrationAudits    FROM pol_admin;
                """);

            migrationBuilder.DropTable(
                name: "ExternalLogins",
                schema: "VCentralPay");

            migrationBuilder.DropTable(
                name: "RegistrationAudits",
                schema: "VCentralPay");

            migrationBuilder.DropTable(
                name: "RegistrationTickets",
                schema: "VCentralPay");

            migrationBuilder.DropTable(
                name: "TenantUserProfiles",
                schema: "VCentralPay");

            migrationBuilder.DropTable(
                name: "TenantUsers",
                schema: "VCentralPay");
        }
    }
}
