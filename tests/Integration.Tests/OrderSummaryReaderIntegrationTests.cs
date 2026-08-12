using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Proves <c>Persistence.MerchantRuntime.Orders.OrderSummaryReader</c>'s 2 raw <c>SqlQueryRaw</c> queries
/// (<c>shop.Orders</c> by <c>SummaryToken</c>, then <c>shop.OrderItems</c> by <c>OrderId</c>) are valid,
/// executable T-SQL that returns the exact columns the reader projects — this is real SQL-Server-only syntax
/// (<c>SELECT TOP 1 ...</c>) that cannot run against the SQLite substitution the rest of the Hosts.Tests
/// suite uses (confirmed: <c>SQLite Error 1: 'near "1": syntax error'</c>), so it is proven here instead,
/// against a live SQL Server, mirroring this project's raw-connection-only style (no
/// Persistence.MerchantRuntime reference — see Integration.Tests.csproj's own comment on why). The masking
/// The second query proves generic line fields while never selecting metadata.
/// Tagged Integration: the default unit run skips these; CI runs them against a live SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OrderSummaryReaderIntegrationTests
{
    private static Task InsertOrderAsync(SqlConnection c, Guid orderId, Guid merchantId, string token, string orderNo) =>
        IntegrationDb.ExecAsync(c,
            """
            INSERT shop.Orders
                (Id, MerchantId, OrderNo, AmountAmount, AmountCurrency, Status, PaymentChannel, CreatedAt,
                 SummaryToken, SummaryTokenExpiresAt, CustomerName, CustomerPhone)
            VALUES (@id, @m, @orderNo, 15000, N'THB', 1, 'promptpay', SYSUTCDATETIME(),
                    @token, DATEADD(hour, 72, SYSUTCDATETIME()), N'Probe', '0800000000');
            """,
            ("@id", orderId), ("@m", merchantId), ("@orderNo", orderNo), ("@token", token));

    private static Task InsertOrderLineAsync(SqlConnection c, Guid lineId, Guid orderId, Guid merchantId) =>
        IntegrationDb.ExecAsync(c,
            """
            INSERT shop.OrderItems
                (Id, OrderId, MerchantId, Quantity, UnitPriceAmount, UnitPriceCurrency,
                 DiscountAmount, DiscountCurrency, ProductCode, VariantCode, VariantName, Metadata)
            VALUES (@id, @orderId, @m, 1, 15000, N'THB',
                    0, N'THB', N'00098-69100/กธ/900001-10', 'VMI', N'ประกันรถยนต์',
                    '{"sourceType":"insurance_document","documentType":"POLICY","policyNumber":"POL-1"}');
            """,
            ("@id", lineId), ("@orderId", orderId), ("@m", merchantId));

    [Fact]
    public async Task Reader_SQL_returns_generic_line_and_never_selects_metadata()
    {
        var orderId = Guid.NewGuid();
        var token = Guid.NewGuid().ToString("N");
        var orderNo = $"ORD69{Random.Shared.Next(80_000_000, 89_999_999)}";
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await IntegrationDb.ExecAsync(c, "BEGIN TRANSACTION;");
        try
        {
            await InsertOrderAsync(c, orderId, IntegrationDb.MerchantA, token, orderNo);
            await InsertOrderLineAsync(c, Guid.NewGuid(), orderId, IntegrationDb.MerchantA);

            // Exactly the reader's first query (OrderSummaryReader.cs, column-for-column — purchase-flow-completion
            // REQ-7.3 added OrderNo, REQ-8 swapped PaymentSessionId for PaymentChannel); only the parameter syntax
            // differs ({0} -> @token). The channel is what the customer pay endpoint charges through, so a column
            // that does not exist (or is misspelled) has to fail HERE, not at a customer's first payment.
            await using var orderCmd = c.CreateCommand();
            orderCmd.CommandText =
                "SELECT TOP 1 Id, MerchantId, OrderNo, AmountAmount, AmountCurrency, Status, PaymentChannel, "
                + "SummaryTokenExpiresAt FROM shop.Orders WHERE SummaryToken = @token";
            orderCmd.Parameters.AddWithValue("@token", token);
            await using var orderReader = await orderCmd.ExecuteReaderAsync();
            Assert.True(await orderReader.ReadAsync());
            var resolvedOrderId = orderReader.GetGuid(0);
            Assert.Equal(orderId, resolvedOrderId);
            Assert.Equal(orderNo, orderReader.GetString(2));
            Assert.Equal("promptpay", orderReader.GetString(6));
            await orderReader.CloseAsync();

            // Exactly the reader's second query — deliberately does NOT select Metadata.
            await using var lineCmd = c.CreateCommand();
            lineCmd.CommandText =
                "SELECT ProductCode, VariantCode, VariantName, Quantity, UnitPriceAmount, UnitPriceCurrency "
                + "FROM shop.OrderItems WHERE OrderId = @orderId";
            lineCmd.Parameters.AddWithValue("@orderId", resolvedOrderId);
            await using var reader = await lineCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());

            Assert.Equal("00098-69100/กธ/900001-10", reader.GetString(0));
            Assert.Equal("VMI", reader.GetString(1));
            Assert.Equal("ประกันรถยนต์", reader.GetString(2));
            Assert.Equal(1, reader.GetInt32(3));
            Assert.Equal(15000m, reader.GetDecimal(4));
            Assert.Equal("THB", reader.GetString(5));
            Assert.Equal(6, reader.FieldCount);
        }
        finally
        {
            await IntegrationDb.ExecAsync(c, "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
        }
    }
}
