using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// policy-reference-record task 4 — proves the two grants <c>GrantOrderItemPolicyTables</c> declares actually
/// land on live SQL Server, the same "insurance-pivot trap" <c>GrantInsuranceLineTables</c> exists for: a
/// brand-new table gets NO grant automatically, and SQLite-backed unit tests (Hosts.Tests) cannot catch a
/// missing one — only a real permission check can. <c>shop.OrderItemPolicies</c> is mutable (INSERT + UPDATE,
/// REQ-3.4); <c>shop.OrderItemPolicyAudits</c> is append-only (SELECT + INSERT only, mirroring
/// <c>shop.OrderItemRevealAudits</c>). The final read reopens a fresh connection — the closest an
/// integration test gets to proving REQ-1.9 (persists across a process restart) against a real database.
/// Tagged Integration: the default unit run skips these; CI runs them against a live SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OrderItemPolicyGrantsTests
{
    private static Task InsertOrderAsync(SqlConnection c, Guid orderId, Guid merchantId) =>
        IntegrationDb.ExecAsync(c,
            """
            INSERT shop.Orders
                (Id, MerchantId, OrderNo, AmountAmount, AmountCurrency, Status, CreatedAt, SummaryToken, SummaryTokenExpiresAt)
            VALUES (@id, @m, CONCAT('ORD', RIGHT(CONVERT(varchar(4), YEAR(GETUTCDATE()) + 543), 2),
                             FORMAT(NEXT VALUE FOR shop.OrderNoSeq, 'D8')),
                    2500, N'THB', 0, SYSUTCDATETIME(), @token, DATEADD(hour, 72, SYSUTCDATETIME()));
            """,
            ("@id", orderId), ("@m", merchantId), ("@token", Guid.NewGuid().ToString("N")));

    private static Task InsertOrderItemAsync(SqlConnection c, Guid itemId, Guid orderId, Guid merchantId) =>
        IntegrationDb.ExecAsync(c,
            """
            INSERT shop.OrderItems
                (Id, OrderId, MerchantId, ProductId, Quantity, UnitPriceAmount, UnitPriceCurrency,
                 DocumentNo, ProductGroup, DocumentType, PolicyNumber, StartDate, EndDate,
                 InsuredFirstName, InsuredLastName, InsuredIdNumber, InsuredDateOfBirth)
            VALUES (@id, @orderId, @m, @productId, 1, 2500, N'THB',
                    N'00098-69100/กธ/900001-10', 'VMI', 'POLICY', NULL, NULL, NULL,
                    N'Somchai', N'Jaidee', N'1234567890123', '1985-05-20');
            """,
            ("@id", itemId), ("@orderId", orderId), ("@m", merchantId), ("@productId", Guid.NewGuid()));

    [Fact]
    public async Task PolApp_can_insert_update_the_policy_and_insert_the_audit_row_and_the_write_survives_a_fresh_connection()
    {
        var merchantId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var policyId = Guid.NewGuid();

        await using (var setup = await IntegrationDb.OpenAsync(IntegrationDb.AppConn))
        {
            await InsertOrderAsync(setup, orderId, merchantId);
            await InsertOrderItemAsync(setup, itemId, orderId, merchantId);
        }

        // INSERT — the grant GrantOrderItemPolicyTables declares for shop.OrderItemPolicies.
        await using (var writer = await IntegrationDb.OpenAsync(IntegrationDb.AppConn))
        {
            await IntegrationDb.ExecAsync(writer,
                """
                INSERT shop.OrderItemPolicies
                    (Id, OrderItemId, MerchantId, ReferenceNumberType, ReferenceNumber,
                     PremiumRemittanceStatus, CreatedAt, UpdatedAt)
                VALUES (@id, @itemId, @m, 0, N'POL-0001', 0, SYSUTCDATETIME(), SYSUTCDATETIME());
                """,
                ("@id", policyId), ("@itemId", itemId), ("@m", merchantId));

            // UPDATE — the second half of the grant (ItemPolicy is mutable, REQ-3.4).
            await IntegrationDb.ExecAsync(writer,
                "UPDATE shop.OrderItemPolicies SET ReferenceNumber = N'POL-0002', UpdatedAt = SYSUTCDATETIME() WHERE Id = @id;",
                ("@id", policyId));

            // INSERT — the grant GrantOrderItemPolicyTables declares for shop.OrderItemPolicyAudits.
            await IntegrationDb.ExecAsync(writer,
                """
                INSERT shop.OrderItemPolicyAudits
                    (Id, OrderItemId, MerchantId, ActorId, ActorKind, Operation, ChangeSummary, CorrelationId, OccurredAt)
                VALUES (@id, @itemId, @m, N'user-1', 1, 0, N'ReferenceNumber', N'corr-1', SYSUTCDATETIME());
                """,
                ("@id", Guid.NewGuid()), ("@itemId", itemId), ("@m", merchantId));
        }

        // Fresh connection — the write was not just an artifact of the same session (REQ-1.9).
        await using var reader = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        var referenceNumber = (string)(await IntegrationDb.ScalarAsync(reader,
            "SELECT ReferenceNumber FROM shop.OrderItemPolicies WHERE Id = @id", ("@id", policyId)))!;
        Assert.Equal("POL-0002", referenceNumber);

        var auditCount = Convert.ToInt32(await IntegrationDb.ScalarAsync(reader,
            "SELECT COUNT(*) FROM shop.OrderItemPolicyAudits WHERE OrderItemId = @itemId", ("@itemId", itemId)));
        Assert.Equal(1, auditCount);
    }

    // OrderItemPolicyAudits is append-only at the app layer (AppendOnlyDescriptor); the DB grant itself is
    // narrower still — no UPDATE was ever granted, so even a bypass of the app-layer guard is denied here too.
    [Fact]
    public async Task PolApp_cannot_update_the_append_only_audit_table()
    {
        var merchantId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var auditId = Guid.NewGuid();

        await using (var setup = await IntegrationDb.OpenAsync(IntegrationDb.AppConn))
        {
            await InsertOrderAsync(setup, orderId, merchantId);
            await InsertOrderItemAsync(setup, itemId, orderId, merchantId);
            await IntegrationDb.ExecAsync(setup,
                """
                INSERT shop.OrderItemPolicyAudits
                    (Id, OrderItemId, MerchantId, ActorId, ActorKind, Operation, ChangeSummary, CorrelationId, OccurredAt)
                VALUES (@id, @itemId, @m, N'user-1', 1, 0, N'ReferenceNumber', N'corr-1', SYSUTCDATETIME());
                """,
                ("@id", auditId), ("@itemId", itemId), ("@m", merchantId));
        }

        await using var conn = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecAsync(conn,
            "UPDATE shop.OrderItemPolicyAudits SET ChangeSummary = N'tampered' WHERE Id = @id;", ("@id", auditId)));
    }
}
