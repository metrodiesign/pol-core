using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Regression guards for the provisioning grant posture + idempotency backstop the Codex review surfaced.
/// Tagged Integration: the default unit run skips them; CI runs them against a live SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TenantProvisioningGrantsTests
{
    // Codex P1 — the provisioning principal writes secrets but must NEVER read plaintext back, so the
    // insert-only vault path cannot do a read-before-write. The grant enforces that: a SELECT is denied.
    [Fact]
    public async Task Admin_cannot_select_vault_secrets()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ScalarAsync(admin, "SELECT COUNT(*) FROM producer.VaultSecrets"));
    }

    // Sibling of P1 — GET /admin/tenants/{code} reads masked hints from PspConnection.Metadata under
    // pol_admin, so that principal MUST hold SELECT on PspConnections (the grant the read-back depends on).
    [Fact]
    public async Task Admin_can_select_psp_connections()
    {
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        var count = Convert.ToInt32(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM producer.PspConnections"));
        Assert.True(count >= 0); // the point is that it does NOT throw a permission error
    }

    // Codex P2 — a duplicate tenant code that races past the application pre-check hits the unique index.
    // EfUnitOfWork translates this SqlException into a ConflictException (HTTP 409); here we prove the
    // index itself rejects the second insert.
    [Fact]
    public async Task Duplicate_tenant_code_is_rejected_by_the_unique_index()
    {
        var code = "dup-" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);

        await IntegrationDb.InsertTenantAsync(admin, Guid.NewGuid(), code);
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.InsertTenantAsync(admin, Guid.NewGuid(), code));
    }
}
