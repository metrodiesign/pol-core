using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Proves the reconciliation aggregate is confined to the bound merchant by the RLS floor (REQ-4.2): rows are
/// filtered BEFORE aggregation, so one merchant's totals never include another's orders. Uses a marker
/// currency so the assertion is robust against any other orders in the shared test DB. Tagged Integration.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OrdersReconciliationIntegrationTests
{
    private const int Paid = 1;
    private const string Marker = "QQQ"; // a currency code no other test uses

    private static decimal AsDecimal(object? o) => Convert.ToDecimal(o);

    [Fact]
    public async Task The_aggregate_excludes_another_merchants_orders()
    {
        // Fresh merchant per run (not the shared MerchantA/MerchantB constants): the SUM below has no WHERE on
        // merchant id (RLS does that implicitly), so a fixed merchant would accumulate this marker order across
        // every local re-run against a persistent dev DB and the assertion would drift upward.
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();

        // The owner merchant owns a distinctive Paid order (inserted under it, so the BLOCK predicate allows it).
        await using (var b = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, owner))
            await IntegrationDb.InsertOrderAsync(b, Guid.NewGuid(), owner, Paid, 555m, Marker);

        // Bound to a different merchant, the SUM over the marker currency sees none of the owner's rows...
        await using var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, other);
        Assert.Equal(0m, AsDecimal(await IntegrationDb.ScalarAsync(a,
            "SELECT ISNULL(SUM(AmountAmount),0) FROM shop.Orders WHERE AmountCurrency=@c AND Status=@s",
            ("@c", Marker), ("@s", Paid))));

        // ...while the owner does.
        await using var ownerConn = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, owner);
        Assert.Equal(555m, AsDecimal(await IntegrationDb.ScalarAsync(ownerConn,
            "SELECT ISNULL(SUM(AmountAmount),0) FROM shop.Orders WHERE AmountCurrency=@c AND Status=@s",
            ("@c", Marker), ("@s", Paid))));
    }

    [Fact]
    public async Task App_cannot_insert_an_order_for_another_merchant()
    {
        // The BLOCK predicate stops a merchant bound to A from forging a B-owned order.
        await using var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantA);
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.InsertOrderAsync(a, Guid.NewGuid(), IntegrationDb.MerchantB, Paid, 100m, Marker));
    }
}
