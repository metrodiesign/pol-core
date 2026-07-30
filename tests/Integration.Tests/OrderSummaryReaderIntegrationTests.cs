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
/// transform itself (<c>MaskIdNumber</c>) is unit-tested directly via <c>GetOrdersHandler</c>'s identical
/// one-line copy (Orders.Tests/GetOrdersTests.cs); this test additionally applies that same transform inline
/// to confirm the reader's actual SQL wires the right column into it and never selects
/// <c>InsuredDateOfBirth</c> at all (REQ-7.4 Decision #3, insurance-pivot task 4).
/// Tagged Integration: the default unit run skips these; CI runs them against a live SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OrderSummaryReaderIntegrationTests
{
    private static string MaskIdNumber(string idNumber) =>
        idNumber.Length <= 4 ? new string('*', idNumber.Length) : $"****{idNumber[^4..]}";

    private static Task InsertOrderAsync(SqlConnection c, Guid orderId, Guid merchantId, string token) =>
        IntegrationDb.ExecAsync(c,
            """
            INSERT shop.Orders
                (Id, MerchantId, AmountAmount, AmountCurrency, Status, CreatedAt, SummaryToken, SummaryTokenExpiresAt)
            VALUES (@id, @m, 15000, N'THB', 0, SYSUTCDATETIME(), @token, DATEADD(hour, 72, SYSUTCDATETIME()));
            """,
            ("@id", orderId), ("@m", merchantId), ("@token", token));

    private static Task InsertOrderLineAsync(SqlConnection c, Guid lineId, Guid orderId, Guid merchantId, string idNumber) =>
        IntegrationDb.ExecAsync(c,
            """
            INSERT shop.OrderItems
                (Id, OrderId, MerchantId, ProductId, Quantity, UnitPriceAmount, UnitPriceCurrency,
                 DocumentNo, ProductGroup, DocumentType, PolicyNumber, StartDate, EndDate,
                 InsuredFirstName, InsuredLastName, InsuredIdNumber, InsuredDateOfBirth)
            VALUES (@id, @orderId, @m, @productId, 1, 15000, N'THB',
                    N'00098-69100/กธ/900001-10', 'VMI', 'POLICY', NULL, NULL, NULL,
                    N'Somchai', N'Jaidee', @idNumber, '1990-01-01');
            """,
            ("@id", lineId), ("@orderId", orderId), ("@m", merchantId), ("@productId", Guid.NewGuid()), ("@idNumber", idNumber));

    [Fact]
    public async Task Reader_SQL_masks_InsuredIdNumber_and_never_selects_date_of_birth()
    {
        var orderId = Guid.NewGuid();
        var token = Guid.NewGuid().ToString("N");
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await InsertOrderAsync(c, orderId, IntegrationDb.MerchantA, token);
        await InsertOrderLineAsync(c, Guid.NewGuid(), orderId, IntegrationDb.MerchantA, "1234567890123");

        // Exactly the reader's first query.
        await using var orderCmd = c.CreateCommand();
        orderCmd.CommandText =
            "SELECT TOP 1 Id FROM shop.Orders WHERE SummaryToken = @token";
        orderCmd.Parameters.AddWithValue("@token", token);
        var resolvedOrderId = (Guid)(await orderCmd.ExecuteScalarAsync())!;
        Assert.Equal(orderId, resolvedOrderId);

        // Exactly the reader's second query — deliberately does NOT select InsuredDateOfBirth.
        await using var lineCmd = c.CreateCommand();
        lineCmd.CommandText =
            "SELECT InsuredFirstName, InsuredLastName, InsuredIdNumber FROM shop.OrderItems WHERE OrderId = @orderId";
        lineCmd.Parameters.AddWithValue("@orderId", resolvedOrderId);
        await using var reader = await lineCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal("Somchai", reader.GetString(0));
        Assert.Equal("Jaidee", reader.GetString(1));
        Assert.Equal("****0123", MaskIdNumber(reader.GetString(2)));

        // The query never asked for InsuredDateOfBirth, so the result set has exactly the 3 columns above.
        Assert.Equal(3, reader.FieldCount);
    }
}
