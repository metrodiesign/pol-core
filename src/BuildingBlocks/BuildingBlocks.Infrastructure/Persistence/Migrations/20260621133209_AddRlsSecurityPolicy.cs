using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The multi-tenant isolation floor (PLAN decision #3), verified on live SQL Server 2025 before
    /// authoring: RLS applies to EVERY principal (even sysadmin), the ONLY bypass is membership in
    /// <c>pol_rls_bypass</c>, and an <c>EXECUTE AS &lt;bypass member&gt;</c> proc bypasses only its own
    /// query. Principals/role are created by docker/bootstrap/01-principals.sql before this runs.
    /// </summary>
    public partial class AddRlsSecurityPolicy : Migration
    {
        // Tables carrying TenantId: full FILTER (hide cross-tenant rows) + BLOCK (no cross-tenant write).
        private static readonly string[] TenantTables =
        {
            "PaymentSessions", "PspConnections", "Products", "CheckoutSessions",
            "Carts", "Orders", "VaultSecrets", "IdempotencyRecords",
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION VCentralPay.fn_tenant_predicate(@TenantId uniqueidentifier)
                RETURNS TABLE WITH SCHEMABINDING AS
                RETURN SELECT 1 AS allowed
                WHERE @TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier)
                   OR IS_ROLEMEMBER(N'pol_rls_bypass') = 1;
                """);

            // CartItems has no TenantId; scope it through its parent Cart (Carts.Id is the PK -> cheap).
            migrationBuilder.Sql("""
                CREATE FUNCTION VCentralPay.fn_cartitem_predicate(@CartId uniqueidentifier)
                RETURNS TABLE WITH SCHEMABINDING AS
                RETURN SELECT 1 AS allowed
                WHERE IS_ROLEMEMBER(N'pol_rls_bypass') = 1
                   OR EXISTS (SELECT 1 FROM VCentralPay.Carts c
                              WHERE c.Id = @CartId
                                AND c.TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier));
                """);

            // Webhook tenant resolution: the proc runs as a bypass-role member, so it can read the one
            // connection->tenant mapping while the caller (pol_app) stays RLS-blocked on the table.
            // Column aliased AS [Value] so EF's SqlQueryRaw<Guid> can materialise the scalar.
            migrationBuilder.Sql("""
                CREATE PROCEDURE VCentralPay.usp_resolve_webhook_tenant @PspConnectionId uniqueidentifier
                WITH EXECUTE AS 'pol_webhook_resolver' AS
                BEGIN
                    SET NOCOUNT ON;
                    SELECT TOP 1 TenantId AS [Value] FROM VCentralPay.PspConnections WHERE Id = @PspConnectionId;
                END
                """);

            var clauses = new List<string>();
            foreach (var t in TenantTables)
            {
                clauses.Add($"ADD FILTER PREDICATE VCentralPay.fn_tenant_predicate(TenantId) ON VCentralPay.{t}");
                clauses.Add($"ADD BLOCK PREDICATE VCentralPay.fn_tenant_predicate(TenantId) ON VCentralPay.{t} AFTER INSERT");
                clauses.Add($"ADD BLOCK PREDICATE VCentralPay.fn_tenant_predicate(TenantId) ON VCentralPay.{t} AFTER UPDATE");
            }
            clauses.Add("ADD FILTER PREDICATE VCentralPay.fn_cartitem_predicate(CartId) ON VCentralPay.CartItems");
            clauses.Add("ADD BLOCK PREDICATE VCentralPay.fn_cartitem_predicate(CartId) ON VCentralPay.CartItems AFTER INSERT");
            clauses.Add("ADD BLOCK PREDICATE VCentralPay.fn_cartitem_predicate(CartId) ON VCentralPay.CartItems AFTER UPDATE");
            // Outbox is NOT row-filtered (the dispatcher drains every tenant) but a BLOCK-on-insert
            // stops a tenant principal from forging another tenant's id onto an outbox row.
            clauses.Add("ADD BLOCK PREDICATE VCentralPay.fn_tenant_predicate(TenantId) ON VCentralPay.OutboxMessages AFTER INSERT");

            migrationBuilder.Sql(
                "CREATE SECURITY POLICY VCentralPay.TenantIsolationPolicy\n" +
                string.Join(",\n", clauses) +
                "\nWITH (STATE = ON);");

            migrationBuilder.Sql("""
                -- pol_app (TenantConsole): own-tenant CRUD (RLS-filtered), idempotency claim,
                -- outbox WRITE-ONLY (cannot read other tenants' payloads), resolve-proc execute.
                GRANT SELECT, INSERT, UPDATE, DELETE ON VCentralPay.PaymentSessions  TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON VCentralPay.PspConnections   TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON VCentralPay.Products         TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON VCentralPay.CheckoutSessions TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON VCentralPay.Carts            TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON VCentralPay.CartItems        TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON VCentralPay.Orders           TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON VCentralPay.VaultSecrets     TO pol_app;
                GRANT SELECT, INSERT                 ON VCentralPay.IdempotencyRecords TO pol_app;
                GRANT INSERT                         ON VCentralPay.OutboxMessages    TO pol_app;
                GRANT EXECUTE ON VCentralPay.usp_resolve_webhook_tenant TO pol_app;

                -- pol_webhook_resolver: ONLY the connection->tenant lookup (proc runs as this user).
                GRANT SELECT ON VCentralPay.PspConnections TO pol_webhook_resolver;

                -- pol_worker (dispatcher): drain the outbox + let consumers update Orders (RLS-scoped).
                GRANT SELECT, UPDATE ON VCentralPay.OutboxMessages TO pol_worker;
                GRANT SELECT, UPDATE ON VCentralPay.Orders         TO pol_worker;

                -- pol_admin (AdminConsole, bypass role): cross-tenant READ; never vault plaintext.
                GRANT SELECT ON VCentralPay.PaymentSessions   TO pol_admin;
                GRANT SELECT ON VCentralPay.PspConnections    TO pol_admin;
                GRANT SELECT ON VCentralPay.Products          TO pol_admin;
                GRANT SELECT ON VCentralPay.CheckoutSessions  TO pol_admin;
                GRANT SELECT ON VCentralPay.Carts             TO pol_admin;
                GRANT SELECT ON VCentralPay.CartItems         TO pol_admin;
                GRANT SELECT ON VCentralPay.Orders            TO pol_admin;
                GRANT SELECT ON VCentralPay.IdempotencyRecords TO pol_admin;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse order: policy first (it depends on the predicate functions), then proc/functions.
            migrationBuilder.Sql("DROP SECURITY POLICY IF EXISTS VCentralPay.TenantIsolationPolicy;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS VCentralPay.usp_resolve_webhook_tenant;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS VCentralPay.fn_cartitem_predicate;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS VCentralPay.fn_tenant_predicate;");

            // Revoke object grants; the logins/role themselves are owned by the bootstrap script.
            migrationBuilder.Sql("""
                REVOKE SELECT, INSERT, UPDATE, DELETE ON VCentralPay.PaymentSessions  FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON VCentralPay.PspConnections   FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON VCentralPay.Products         FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON VCentralPay.CheckoutSessions FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON VCentralPay.Carts            FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON VCentralPay.CartItems        FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON VCentralPay.Orders           FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON VCentralPay.VaultSecrets     FROM pol_app;
                REVOKE SELECT, INSERT                 ON VCentralPay.IdempotencyRecords FROM pol_app;
                REVOKE INSERT                         ON VCentralPay.OutboxMessages    FROM pol_app;
                REVOKE SELECT ON VCentralPay.PspConnections FROM pol_webhook_resolver;
                REVOKE SELECT, UPDATE ON VCentralPay.OutboxMessages FROM pol_worker;
                REVOKE SELECT, UPDATE ON VCentralPay.Orders         FROM pol_worker;
                REVOKE SELECT ON VCentralPay.PaymentSessions   FROM pol_admin;
                REVOKE SELECT ON VCentralPay.PspConnections    FROM pol_admin;
                REVOKE SELECT ON VCentralPay.Products          FROM pol_admin;
                REVOKE SELECT ON VCentralPay.CheckoutSessions  FROM pol_admin;
                REVOKE SELECT ON VCentralPay.Carts             FROM pol_admin;
                REVOKE SELECT ON VCentralPay.CartItems         FROM pol_admin;
                REVOKE SELECT ON VCentralPay.Orders            FROM pol_admin;
                REVOKE SELECT ON VCentralPay.IdempotencyRecords FROM pol_admin;
                """);
        }
    }
}
