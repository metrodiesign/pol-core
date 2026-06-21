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
                CREATE FUNCTION producer.fn_tenant_predicate(@TenantId uniqueidentifier)
                RETURNS TABLE WITH SCHEMABINDING AS
                RETURN SELECT 1 AS allowed
                WHERE @TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier)
                   OR IS_ROLEMEMBER(N'pol_rls_bypass') = 1;
                """);

            // CartItems has no TenantId; scope it through its parent Cart (Carts.Id is the PK -> cheap).
            migrationBuilder.Sql("""
                CREATE FUNCTION producer.fn_cartitem_predicate(@CartId uniqueidentifier)
                RETURNS TABLE WITH SCHEMABINDING AS
                RETURN SELECT 1 AS allowed
                WHERE IS_ROLEMEMBER(N'pol_rls_bypass') = 1
                   OR EXISTS (SELECT 1 FROM producer.Carts c
                              WHERE c.Id = @CartId
                                AND c.TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier));
                """);

            // Webhook tenant resolution: the proc runs as a bypass-role member, so it can read the one
            // connection->tenant mapping while the caller (pol_app) stays RLS-blocked on the table.
            // Column aliased AS [Value] so EF's SqlQueryRaw<Guid> can materialise the scalar.
            migrationBuilder.Sql("""
                CREATE PROCEDURE producer.usp_resolve_webhook_tenant @PspConnectionId uniqueidentifier
                WITH EXECUTE AS 'pol_webhook_resolver' AS
                BEGIN
                    SET NOCOUNT ON;
                    SELECT TOP 1 TenantId AS [Value] FROM producer.PspConnections WHERE Id = @PspConnectionId;
                END
                """);

            var clauses = new List<string>();
            foreach (var t in TenantTables)
            {
                clauses.Add($"ADD FILTER PREDICATE producer.fn_tenant_predicate(TenantId) ON producer.{t}");
                clauses.Add($"ADD BLOCK PREDICATE producer.fn_tenant_predicate(TenantId) ON producer.{t} AFTER INSERT");
                clauses.Add($"ADD BLOCK PREDICATE producer.fn_tenant_predicate(TenantId) ON producer.{t} AFTER UPDATE");
            }
            clauses.Add("ADD FILTER PREDICATE producer.fn_cartitem_predicate(CartId) ON producer.CartItems");
            clauses.Add("ADD BLOCK PREDICATE producer.fn_cartitem_predicate(CartId) ON producer.CartItems AFTER INSERT");
            clauses.Add("ADD BLOCK PREDICATE producer.fn_cartitem_predicate(CartId) ON producer.CartItems AFTER UPDATE");
            // Outbox is NOT row-filtered (the dispatcher drains every tenant) but a BLOCK-on-insert
            // stops a tenant principal from forging another tenant's id onto an outbox row.
            clauses.Add("ADD BLOCK PREDICATE producer.fn_tenant_predicate(TenantId) ON producer.OutboxMessages AFTER INSERT");

            migrationBuilder.Sql(
                "CREATE SECURITY POLICY producer.TenantIsolationPolicy\n" +
                string.Join(",\n", clauses) +
                "\nWITH (STATE = ON);");

            migrationBuilder.Sql("""
                -- pol_app (TenantConsole): own-tenant CRUD (RLS-filtered), idempotency claim,
                -- outbox WRITE-ONLY (cannot read other tenants' payloads), resolve-proc execute.
                GRANT SELECT, INSERT, UPDATE, DELETE ON producer.PaymentSessions  TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON producer.PspConnections   TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON producer.Products         TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON producer.CheckoutSessions TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON producer.Carts            TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON producer.CartItems        TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON producer.Orders           TO pol_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON producer.VaultSecrets     TO pol_app;
                GRANT SELECT, INSERT                 ON producer.IdempotencyRecords TO pol_app;
                GRANT INSERT                         ON producer.OutboxMessages    TO pol_app;
                GRANT EXECUTE ON producer.usp_resolve_webhook_tenant TO pol_app;

                -- pol_webhook_resolver: ONLY the connection->tenant lookup (proc runs as this user).
                GRANT SELECT ON producer.PspConnections TO pol_webhook_resolver;

                -- pol_worker (dispatcher): drain the outbox + let consumers update Orders (RLS-scoped).
                GRANT SELECT, UPDATE ON producer.OutboxMessages TO pol_worker;
                GRANT SELECT, UPDATE ON producer.Orders         TO pol_worker;

                -- pol_admin (AdminConsole, bypass role): cross-tenant READ; never vault plaintext.
                GRANT SELECT ON producer.PaymentSessions   TO pol_admin;
                GRANT SELECT ON producer.PspConnections    TO pol_admin;
                GRANT SELECT ON producer.Products          TO pol_admin;
                GRANT SELECT ON producer.CheckoutSessions  TO pol_admin;
                GRANT SELECT ON producer.Carts             TO pol_admin;
                GRANT SELECT ON producer.CartItems         TO pol_admin;
                GRANT SELECT ON producer.Orders            TO pol_admin;
                GRANT SELECT ON producer.IdempotencyRecords TO pol_admin;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse order: policy first (it depends on the predicate functions), then proc/functions.
            migrationBuilder.Sql("DROP SECURITY POLICY IF EXISTS producer.TenantIsolationPolicy;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS producer.usp_resolve_webhook_tenant;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS producer.fn_cartitem_predicate;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS producer.fn_tenant_predicate;");

            // Revoke object grants; the logins/role themselves are owned by the bootstrap script.
            migrationBuilder.Sql("""
                REVOKE SELECT, INSERT, UPDATE, DELETE ON producer.PaymentSessions  FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON producer.PspConnections   FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON producer.Products         FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON producer.CheckoutSessions FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON producer.Carts            FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON producer.CartItems        FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON producer.Orders           FROM pol_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON producer.VaultSecrets     FROM pol_app;
                REVOKE SELECT, INSERT                 ON producer.IdempotencyRecords FROM pol_app;
                REVOKE INSERT                         ON producer.OutboxMessages    FROM pol_app;
                REVOKE SELECT ON producer.PspConnections FROM pol_webhook_resolver;
                REVOKE SELECT, UPDATE ON producer.OutboxMessages FROM pol_worker;
                REVOKE SELECT, UPDATE ON producer.Orders         FROM pol_worker;
                REVOKE SELECT ON producer.PaymentSessions   FROM pol_admin;
                REVOKE SELECT ON producer.PspConnections    FROM pol_admin;
                REVOKE SELECT ON producer.Products          FROM pol_admin;
                REVOKE SELECT ON producer.CheckoutSessions  FROM pol_admin;
                REVOKE SELECT ON producer.Carts             FROM pol_admin;
                REVOKE SELECT ON producer.CartItems         FROM pol_admin;
                REVOKE SELECT ON producer.Orders            FROM pol_admin;
                REVOKE SELECT ON producer.IdempotencyRecords FROM pol_admin;
                """);
        }
    }
}
