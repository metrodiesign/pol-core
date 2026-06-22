using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVaultRevealAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VaultRevealAudits",
                schema: "producer",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SecretName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    PrevHash = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    Hash = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    RevealedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultRevealAudits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VaultRevealAudits_TenantId_Id",
                schema: "producer",
                table: "VaultRevealAudits",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_VaultRevealAudits_TenantId_Seq",
                schema: "producer",
                table: "VaultRevealAudits",
                columns: new[] { "TenantId", "Seq" },
                unique: true);

            // The append-only reveal audit joins the EXISTING RLS floor additively (ALTER ... ADD, never
            // DROP+CREATE, so the TenantIsolationPolicy is never momentarily off). BLOCK-after-insert stops a
            // tenant principal from stamping another tenant's id onto an audit row. There is deliberately NO
            // FILTER predicate: pol_app gets INSERT only (below), so it can append but can never SELECT,
            // UPDATE or DELETE — it cannot read, edit, or trim its own chain. Reuses fn_tenant_predicate.
            migrationBuilder.Sql(
                "ALTER SECURITY POLICY producer.TenantIsolationPolicy\n" +
                "    ADD BLOCK PREDICATE producer.fn_tenant_predicate(TenantId) ON producer.VaultRevealAudits AFTER INSERT;");

            migrationBuilder.Sql("""
                -- pol_app (TenantConsole): INSERT ONLY. No SELECT/UPDATE/DELETE -> append-only at the grant
                -- level, so a tenant principal can record a reveal but never read, rewrite, or delete the chain.
                GRANT INSERT ON producer.VaultRevealAudits TO pol_app;
                -- pol_vault_auditor (login-less, pol_rls_bypass member): the head-read proc runs AS this user
                -- so it can read the chain head while pol_app stays unable to SELECT the table.
                GRANT SELECT ON producer.VaultRevealAudits TO pol_vault_auditor;
                -- pol_admin (AdminConsole, bypass role): cross-tenant SELECT for the integrity verify routine.
                -- The table holds NO secret/plaintext/DEK/KEK/hint, so cross-tenant read leaks nothing sensitive.
                GRANT SELECT ON producer.VaultRevealAudits TO pol_admin;
                """);

            // Head-read: returns the tenant's last (Seq, Hash) so the writer can chain the next row. Runs AS
            // the bypass principal because pol_app cannot SELECT the table. sp_getapplock (transaction-owned)
            // serializes appends per tenant inside the writer's transaction, so two concurrent reveals cannot
            // fork the chain; the unique (TenantId, Seq) index is the backstop. Columns aliased for SqlQuery.
            migrationBuilder.Sql("""
                CREATE PROCEDURE producer.usp_vault_audit_head @TenantId uniqueidentifier
                WITH EXECUTE AS 'pol_vault_auditor' AS
                BEGIN
                    SET NOCOUNT ON;
                    DECLARE @res nvarchar(80) = CONCAT(N'vault-audit:', CONVERT(nvarchar(36), @TenantId));
                    DECLARE @lock int;
                    EXEC @lock = sp_getapplock
                        @Resource = @res, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
                    IF @lock < 0
                        THROW 50000, N'Could not acquire the vault audit chain lock for the tenant.', 1;
                    SELECT TOP 1 Seq AS LastSeq, Hash AS LastHash
                    FROM producer.VaultRevealAudits WHERE TenantId = @TenantId ORDER BY Seq DESC;
                END
                """);
            migrationBuilder.Sql("GRANT EXECUTE ON producer.usp_vault_audit_head TO pol_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Additive removal in reverse: detach the predicate from the policy (never drop the policy
            // itself), drop the proc, revoke the grants, THEN drop the table.
            // DROP grammar terminates at the table name — the operation (AFTER INSERT) is part of ADD/ALTER
            // only, never DROP. A predicate is uniquely identified by (table, operation) and dropped by table.
            migrationBuilder.Sql(
                "ALTER SECURITY POLICY producer.TenantIsolationPolicy\n" +
                "    DROP BLOCK PREDICATE ON producer.VaultRevealAudits;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS producer.usp_vault_audit_head;");
            migrationBuilder.Sql("""
                REVOKE INSERT ON producer.VaultRevealAudits FROM pol_app;
                REVOKE SELECT ON producer.VaultRevealAudits FROM pol_vault_auditor;
                REVOKE SELECT ON producer.VaultRevealAudits FROM pol_admin;
                """);

            migrationBuilder.DropTable(
                name: "VaultRevealAudits",
                schema: "producer");
        }
    }
}
