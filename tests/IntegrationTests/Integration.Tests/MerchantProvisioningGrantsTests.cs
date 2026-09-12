namespace Integration.Tests;

/// <summary>
/// Regression guard for the provisioning grant posture, updated for rls-to-query-filter task 8
/// (RlsTeardownAndOnePrincipal): pol_admin is retired — pol_app is the sole runtime principal now, and it
/// already held SELECT/INSERT/UPDATE on <c>merch.VaultSecrets</c> even before the collapse, so the old
/// "the provisioning principal cannot read secrets back" invariant (which was specifically about pol_admin's
/// narrower INSERT-only grant) no longer applies to anything — there is no separate provisioning principal
/// left to hold it. What survives is the PspConnections SELECT grant the merchant-detail read-back depends
/// on. The duplicate-merchant-code unique-index guard is covered by
/// <c>MerchantProvisioningIntegrationTests.Duplicate_merchant_code_is_rejected</c> — not duplicated here.
/// Tagged Integration: the default unit run skips them; CI runs them against a live SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MerchantProvisioningGrantsTests
{
    // GET /api/v1/merchants/{code} reads masked hints from PspConnection.Metadata under pol_app, so that
    // principal MUST hold SELECT on PspConnections (the grant the read-back depends on).
    [Fact]
    public async Task PolApp_can_select_psp_connections()
    {
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        var count = Convert.ToInt32(await IntegrationDb.ScalarAsync(app,
            "SELECT COUNT(*) FROM txn.PspConnections"));
        Assert.True(count >= 0); // the point is that it does NOT throw a permission error
    }
}
