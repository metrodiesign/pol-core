using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Proves the merchant control-plane isolation + grant floor for the provisioning slice against live SQL
/// Server 2025 with the real principals and the SecurityObjects migration applied. Regression guards for
/// that migration's RLS predicate (FILTER+BLOCK on sec.fn_merchant_predicate(Id)) and grants: a merchant reads
/// only its own master row, cannot write the table at all, pol_admin provisions cross-merchant but can
/// NEVER read vault plaintext, the audit log is admin-only, and a duplicate code is rejected.
/// Tagged Integration so the default unit run skips them; CI runs them against a migrated SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MerchantProvisioningIntegrationTests
{
    private static int AsInt(object? o) => (int)o!;
    private static string UniqueCode() => "t" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task Admin_can_insert_and_read_a_merchant_cross_merchant()
    {
        // T5: merch.Merchants carries the merchant BLOCK-on-insert predicate, so provisioning needs a bound
        // Super platform user, not a bare pol_admin connection (see IntegrationDb.OpenAsNewSuperUserAsync).
        var id = Guid.NewGuid();
        await using var admin = await IntegrationDb.OpenAsNewSuperUserAsync();
        await IntegrationDb.InsertMerchantAsync(admin, id, UniqueCode());

        var seen = AsInt(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM merch.Merchants WHERE Id=@id", ("@id", id)));

        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task Merchant_reads_only_its_own_master_row()
    {
        // The PK IS the merchant identity, so a merchant bound to its own id reads the row; another merchant
        // (a different SESSION_CONTEXT) is filtered to zero — REQ-10 + cross-merchant isolation.
        var id = Guid.NewGuid();
        await using (var admin = await IntegrationDb.OpenAsNewSuperUserAsync())
            await IntegrationDb.InsertMerchantAsync(admin, id, UniqueCode());

        await using var own = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, id);
        Assert.Equal(1, AsInt(await IntegrationDb.ScalarAsync(own,
            "SELECT COUNT(*) FROM merch.Merchants WHERE Id=@id", ("@id", id))));

        await using var other = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, Guid.NewGuid());
        Assert.Equal(0, AsInt(await IntegrationDb.ScalarAsync(other,
            "SELECT COUNT(*) FROM merch.Merchants WHERE Id=@id", ("@id", id))));
    }

    [Fact]
    public async Task App_cannot_write_the_merchant_table()
    {
        // pol_app has SELECT only on Merchants (no INSERT grant); the BLOCK predicate is the belt-and-braces.
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, Guid.NewGuid());
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.InsertMerchantAsync(app, Guid.NewGuid(), UniqueCode()));
    }

    [Fact]
    public async Task Admin_cannot_select_vault_secrets()
    {
        // pol_admin is INSERT-only on VaultSecrets: it provisions a secret but can NEVER read plaintext
        // back (REQ-6.5). The masked read-back path reads PspConnection.Metadata hints, not the vault.
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ScalarAsync(admin, "SELECT COUNT(*) FROM merch.VaultSecrets"));
    }

    [Fact]
    public async Task Provisioning_audits_are_admin_only()
    {
        // pol_app has no grant on the control-plane audit log; pol_admin reads it (REQ-11).
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, Guid.NewGuid());
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM merch.ProvisioningAudits"));

        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        var count = await IntegrationDb.ScalarAsync(admin, "SELECT COUNT(*) FROM merch.ProvisioningAudits");
        Assert.True(AsInt(count) >= 0); // readable (no throw) is the assertion
    }

    [Fact]
    public async Task Duplicate_merchant_code_is_rejected()
    {
        // Idempotency backstop: the unique index on Code rejects a second provisioning of the same code
        // even with a different id (REQ-5.3). T5: needs a bound Super platform user (see above).
        var code = UniqueCode();
        await using var admin = await IntegrationDb.OpenAsNewSuperUserAsync();
        await IntegrationDb.InsertMerchantAsync(admin, Guid.NewGuid(), code);

        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.InsertMerchantAsync(admin, Guid.NewGuid(), code));
    }
}
