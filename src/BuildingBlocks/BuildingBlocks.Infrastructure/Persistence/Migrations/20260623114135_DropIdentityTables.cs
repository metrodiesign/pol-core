using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropIdentityTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Detach the TenantUsers predicates from the policy BEFORE dropping the table (a table under a
            // security policy cannot be dropped); never drop the policy itself. Mirrors AddIdentityTables.Down.
            migrationBuilder.Sql(
                "ALTER SECURITY POLICY producer.TenantIsolationPolicy\n" +
                "    DROP FILTER PREDICATE ON producer.TenantUsers,\n" +
                "    DROP BLOCK PREDICATE ON producer.TenantUsers AFTER INSERT,\n" +
                "    DROP BLOCK PREDICATE ON producer.TenantUsers AFTER UPDATE;");

            migrationBuilder.Sql("""
                REVOKE SELECT ON producer.TenantUsers FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE ON producer.TenantUsers         FROM pol_admin;
                REVOKE SELECT, INSERT         ON producer.ExternalLogins       FROM pol_admin;
                REVOKE SELECT, INSERT         ON producer.TenantUserProfiles   FROM pol_admin;
                REVOKE SELECT, INSERT, UPDATE ON producer.RegistrationTickets  FROM pol_admin;
                REVOKE SELECT, INSERT         ON producer.RegistrationAudits    FROM pol_admin;
                """);

            migrationBuilder.DropTable(
                name: "ExternalLogins",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "RegistrationAudits",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "RegistrationTickets",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "TenantUserProfiles",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "TenantUsers",
                schema: "producer");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExternalLogins",
                schema: "producer",
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
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AdminSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TargetSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrationAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegistrationTickets",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    HostedDomain = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    UsedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrationTickets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantUserProfiles",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TenantUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantUserProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantUsers",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Role = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantUsers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalLogins_Provider_Subject",
                schema: "producer",
                table: "ExternalLogins",
                columns: new[] { "Provider", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantUserProfiles_TenantUserId",
                schema: "producer",
                table: "TenantUserProfiles",
                column: "TenantUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantUsers_Subject",
                schema: "producer",
                table: "TenantUsers",
                column: "Subject",
                unique: true);

            // Re-attach TenantUsers to the RLS floor + restore the grants (mirror of AddIdentityTables.Up).
            migrationBuilder.Sql(
                "ALTER SECURITY POLICY producer.TenantIsolationPolicy\n" +
                "    ADD FILTER PREDICATE producer.fn_tenant_predicate(TenantId) ON producer.TenantUsers,\n" +
                "    ADD BLOCK PREDICATE producer.fn_tenant_predicate(TenantId) ON producer.TenantUsers AFTER INSERT,\n" +
                "    ADD BLOCK PREDICATE producer.fn_tenant_predicate(TenantId) ON producer.TenantUsers AFTER UPDATE;");

            migrationBuilder.Sql("""
                GRANT SELECT ON producer.TenantUsers TO pol_app;
                GRANT SELECT, INSERT, UPDATE ON producer.TenantUsers         TO pol_admin;
                GRANT SELECT, INSERT         ON producer.ExternalLogins       TO pol_admin;
                GRANT SELECT, INSERT         ON producer.TenantUserProfiles   TO pol_admin;
                GRANT SELECT, INSERT, UPDATE ON producer.RegistrationTickets  TO pol_admin;
                GRANT SELECT, INSERT         ON producer.RegistrationAudits    TO pol_admin;
                """);
        }
    }
}
