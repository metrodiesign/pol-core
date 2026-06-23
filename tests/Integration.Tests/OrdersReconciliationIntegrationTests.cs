using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Proves the reconciliation aggregate is confined to the bound tenant by the RLS floor (REQ-4.2): rows are
/// filtered BEFORE aggregation, so one tenant's totals never include another's orders. Uses a marker
/// currency so the assertion is robust against any other orders in the shared test DB. Tagged Integration.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OrdersReconciliationIntegrationTests
{
    private const int Paid = 1;
    private const string Marker = "QQQ"; // a currency code no other test uses

    private static long AsLong(object? o) => Convert.ToInt64(o);

    [Fact]
    public async Task The_aggregate_excludes_another_tenants_orders()
    {
        // Tenant B owns a distinctive Paid order (inserted under B, so the BLOCK predicate allows it).
        await using (var b = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantB))
            await IntegrationDb.InsertOrderAsync(b, Guid.NewGuid(), IntegrationDb.TenantB, Paid, 555, Marker);

        // Bound to A, the SUM over the marker currency sees none of B's rows...
        await using var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantA);
        Assert.Equal(0, AsLong(await IntegrationDb.ScalarAsync(a,
            "SELECT ISNULL(SUM(AmountMinorUnits),0) FROM producer.Orders WHERE AmountCurrency=@c AND Status=@s",
            ("@c", Marker), ("@s", Paid))));

        // ...while the owner B does.
        await using var owner = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantB);
        Assert.Equal(555, AsLong(await IntegrationDb.ScalarAsync(owner,
            "SELECT ISNULL(SUM(AmountMinorUnits),0) FROM producer.Orders WHERE AmountCurrency=@c AND Status=@s",
            ("@c", Marker), ("@s", Paid))));
    }

    [Fact]
    public async Task App_cannot_insert_an_order_for_another_tenant()
    {
        // The BLOCK predicate stops a tenant bound to A from forging a B-owned order.
        await using var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantA);
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.InsertOrderAsync(a, Guid.NewGuid(), IntegrationDb.TenantB, Paid, 100, Marker));
    }
}
