using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Metadata",
                schema: "producer",
                table: "PspConnections",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(4000)",
                oldMaxLength: 4000,
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "ProvisioningAudits",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AdminSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisioningAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LegalEntityId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Country = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    EnabledChannels = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Metadata = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Code",
                schema: "producer",
                table: "Tenants",
                column: "Code",
                unique: true);

            // Join the EXISTING RLS floor additively (ALTER ... ADD, never DROP+CREATE, so
            // TenantIsolationPolicy is never momentarily off). The Tenant master record is scoped by its
            // OWN primary key (Id IS the tenant identity): FILTER lets a tenant read only its own row
            // (REQ-10); BLOCK stops a tenant principal from inserting or repointing a row to another id.
            // pol_admin (pol_rls_bypass member) bypasses both to provision cross-tenant. Reuses
            // fn_tenant_predicate, here bound to Id instead of TenantId.
            migrationBuilder.Sql(
                "ALTER SECURITY POLICY producer.TenantIsolationPolicy\n" +
                "    ADD FILTER PREDICATE producer.fn_tenant_predicate(Id) ON producer.Tenants,\n" +
                "    ADD BLOCK PREDICATE producer.fn_tenant_predicate(Id) ON producer.Tenants AFTER INSERT,\n" +
                "    ADD BLOCK PREDICATE producer.fn_tenant_predicate(Id) ON producer.Tenants AFTER UPDATE;");

            // ProvisioningAudits is control-plane only (no tenant-scoped read path) so it is deliberately
            // NOT under the tenant predicate; pol_app gets no grant at all and cannot touch it.
            migrationBuilder.Sql("""
                -- pol_app (TenantConsole): read its OWN tenant master record (RLS-filtered to its row).
                GRANT SELECT ON producer.Tenants TO pol_app;

                -- pol_admin (provisioning, bypass role): cross-tenant write of the master record + read-back,
                -- plus INSERT into the per-PSP connection + vault rows it creates in the same transaction.
                -- SELECT on PspConnections is already granted by AddRlsSecurityPolicy (the read-back relies on
                -- it); this migration adds only INSERT. INSERT-only on VaultSecrets -> admin can write a secret
                -- but can NEVER SELECT plaintext back (masked read-back uses PspConnection.Metadata, not the vault).
                GRANT SELECT, INSERT, UPDATE ON producer.Tenants           TO pol_admin;
                GRANT INSERT                 ON producer.PspConnections     TO pol_admin;
                GRANT INSERT                 ON producer.VaultSecrets       TO pol_admin;
                GRANT SELECT, INSERT         ON producer.ProvisioningAudits TO pol_admin;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Detach the Tenants predicates from the policy BEFORE dropping the table (a table under a
            // security policy cannot be dropped). Never drop the policy itself — other tables rely on it.
            migrationBuilder.Sql(
                "ALTER SECURITY POLICY producer.TenantIsolationPolicy\n" +
                "    DROP FILTER PREDICATE ON producer.Tenants,\n" +
                "    DROP BLOCK PREDICATE ON producer.Tenants AFTER INSERT,\n" +
                "    DROP BLOCK PREDICATE ON producer.Tenants AFTER UPDATE;");

            migrationBuilder.Sql("""
                REVOKE SELECT                ON producer.Tenants            FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE ON producer.Tenants           FROM pol_admin;
                REVOKE INSERT                ON producer.PspConnections     FROM pol_admin;
                REVOKE INSERT                ON producer.VaultSecrets       FROM pol_admin;
                REVOKE SELECT, INSERT        ON producer.ProvisioningAudits FROM pol_admin;
                """);

            migrationBuilder.DropTable(
                name: "ProvisioningAudits",
                schema: "producer");

            migrationBuilder.DropTable(
                name: "Tenants",
                schema: "producer");

            migrationBuilder.AlterColumn<string>(
                name: "Metadata",
                schema: "producer",
                table: "PspConnections",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);
        }
    }
}
