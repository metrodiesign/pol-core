using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Regression guard for the merch.Merchants unique-code constraint against live SQL Server 2025 with the
/// RlsTeardownAndOnePrincipal migration applied. Updated for rls-to-query-filter task 8: pol_admin is
/// retired — pol_app is the sole runtime principal and now holds full CRUD on merch.Merchants (Provisioning
/// writes through it via <c>Persistence.Provisioning</c>'s elevated <c>IWriteAuthorizer</c>, an app-layer
/// capability, not a DB grant), so the old RLS-predicate cross-merchant isolation tests and the
/// pol_admin-vs-pol_app grant-separation tests are gone — there is no SQL-level filtering or separate
/// provisioning principal left to prove. What survives is the idempotency backstop below; the rest of the
/// provisioning slice's isolation is proven at the app layer (Architecture.Tests' write-authorizer suite) and
/// the read floor at <c>Architecture.Tests.ReadFloorTests</c>. Tagged Integration so the default unit run
/// skips them; CI runs them against a migrated SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MerchantProvisioningIntegrationTests
{
    private static string UniqueCode() => "t" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task Duplicate_merchant_code_is_rejected()
    {
        // Idempotency backstop: the unique index on Code rejects a second provisioning of the same code
        // even with a different id (REQ-5.3).
        var code = UniqueCode();
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await IntegrationDb.InsertMerchantAsync(app, Guid.NewGuid(), code);

        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.InsertMerchantAsync(app, Guid.NewGuid(), code));
    }
}
