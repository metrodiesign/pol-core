using BuildingBlocks.Application;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Orders.Application;
using Orders.Domain;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Orders;
using Persistence.MerchantRuntime.Reporting;
using Reporting.Application;

namespace Integration.Tests;

/// <summary>
/// Executes the reusable Admin reporting projection through SQL Server. Provider-backed coverage matters here:
/// EF can accept a LINQ expression at compile time while rejecting it only when SQL translation starts.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AdminReportingReaderIntegrationTests
{
    [Fact]
    public async Task Dashboard_list_and_detail_translate_with_exact_scope_and_item_count()
    {
        var createdAt = DateTime.UtcNow;
        var merchantA = Guid.NewGuid();
        var merchantB = Guid.NewGuid();
        var merchantAOrder = Guid.NewGuid();
        var merchantASession = Guid.NewGuid();
        var merchantBOrder = Guid.NewGuid();
        var merchantBSession = Guid.NewGuid();
        var orderIds = new[] { merchantAOrder, merchantBOrder };

        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        try
        {
            await SeedTransactionAsync(
                connection, merchantAOrder, merchantASession, merchantA,
                createdAt, amount: 637.25m, itemCount: 2);
            await SeedTransactionAsync(
                connection, merchantBOrder, merchantBSession, merchantB,
                createdAt, amount: 999m, itemCount: 1);

            await using var db = NewContext(merchantA);
            var sut = new AdminReportingReader(db);
            var access = new ReportingAccess(false, new HashSet<Guid> { merchantA });

            var dashboard = await sut.DashboardAsync(
                new ReportingPeriod(createdAt.AddMinutes(-1), createdAt.AddMinutes(1)),
                access, merchantId: null, CancellationToken.None);
            var page = await sut.ListTransactionsAsync(
                new AdminTransactionQuery(access) { Page = 1, Limit = 25 }, CancellationToken.None);
            var detail = await sut.GetTransactionAsync(merchantASession, access, CancellationToken.None);

            Assert.Equal(1, dashboard.TransactionCount);
            var total = Assert.Single(dashboard.Totals);
            Assert.Equal("THB", total.Currency);
            Assert.Equal(637.25m, total.Amount);

            Assert.Equal(1, page.Total);
            var row = Assert.Single(page.Items);
            Assert.Equal(merchantASession, row.TransactionId);
            Assert.Equal(merchantA, row.MerchantId);
            Assert.Equal(2, row.ItemCount);

            Assert.NotNull(detail);
            Assert.Equal(2, detail!.Transaction.ItemCount);
            Assert.Equal(2, detail.Lines.Count);
            Assert.Null(await sut.GetTransactionAsync(merchantBSession, access, CancellationToken.None));
        }
        finally
        {
            await CleanupAsync(orderIds);
        }
    }

    [Fact]
    public async Task Item_count_query_accepts_the_export_sentinel_size()
    {
        var orderId = Guid.NewGuid();
        var orderIds = Enumerable.Repeat(Guid.NewGuid(), 100_000).Prepend(orderId).ToArray();
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        try
        {
            await SeedTransactionAsync(
                connection, orderId, Guid.NewGuid(), IntegrationDb.MerchantA,
                DateTime.UtcNow, amount: 100m, itemCount: 1);

            await using var db = NewContext(IntegrationDb.MerchantA);
            var counts = await db.OrderItems.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => orderIds.Contains(x.OrderId)
                    && x.MerchantId == IntegrationDb.MerchantA)
                .GroupBy(x => new { x.OrderId, x.MerchantId })
                .Select(group => new
                {
                    group.Key.OrderId,
                    group.Key.MerchantId,
                    Count = group.Count(),
                })
                .ToListAsync();

            var count = Assert.Single(counts);
            Assert.Equal(orderId, count.OrderId);
            Assert.Equal(IntegrationDb.MerchantA, count.MerchantId);
            Assert.Equal(1, count.Count);
        }
        finally
        {
            await CleanupAsync([orderId]);
        }
    }

    [Fact]
    public async Task Reconciliation_translates_and_preserves_merchant_scope()
    {
        var createdAt = DateTime.UtcNow;
        var merchantA = Guid.NewGuid();
        var merchantB = Guid.NewGuid();
        var merchantAOrder = Guid.NewGuid();
        var merchantBOrder = Guid.NewGuid();
        var orderIds = new[] { merchantAOrder, merchantBOrder };
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        try
        {
            await SeedTransactionAsync(
                connection, merchantAOrder, Guid.NewGuid(), merchantA,
                createdAt, amount: 123.45m, itemCount: 1);
            await SeedTransactionAsync(
                connection, merchantBOrder, Guid.NewGuid(), merchantB,
                createdAt, amount: 999m, itemCount: 1);

            await using var db = NewContext(merchantA);
            var sut = new AdminOrderReader(db, NullLogger<AdminOrderReader>.Instance);
            var access = new AdminOrderAccess(false, new HashSet<Guid> { merchantA });

            var totals = await sut.ReconciliationAsync(
                access, merchantId: null, CancellationToken.None);

            var total = Assert.Single(totals);
            Assert.Equal(OrderStatus.Pending, total.Status);
            Assert.Equal("THB", total.Currency);
            Assert.Equal(1, total.Count);
            Assert.Equal(123.45m, total.Total);
            Assert.Empty(await sut.ReconciliationAsync(
                access, merchantB, CancellationToken.None));
        }
        finally
        {
            await CleanupAsync(orderIds);
        }
    }

    private static MerchantRuntimeDbContext NewContext(Guid merchantId) =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
                .UseSqlServer(IntegrationDb.AppConn).Options,
            new FakeActor(merchantId), AllowAllWriteAuthorizer.Instance,
            NoOpSecurityTelemetry.Instance);

    private static async Task SeedTransactionAsync(
        SqlConnection connection,
        Guid orderId,
        Guid sessionId,
        Guid merchantId,
        DateTime createdAt,
        decimal amount,
        int itemCount)
    {
        await IntegrationDb.ExecAsync(connection,
            """
            INSERT shop.Orders
                (Id, MerchantId, OrderNo, AmountAmount, AmountCurrency, Status, CreatedAt,
                 SummaryToken, SummaryTokenExpiresAt, CustomerName, CustomerPhone)
            VALUES (@id, @merchantId, @orderNo, @amount, N'THB', 1, @createdAt,
                    @token, DATEADD(hour, 72, @createdAt), N'Reporting probe', '0800000000');
            """,
            ("@id", orderId), ("@merchantId", merchantId),
            ("@orderNo", $"ORD69{Random.Shared.Next(10_000_000, 99_999_999)}"),
            ("@amount", amount), ("@createdAt", createdAt), ("@token", Guid.NewGuid().ToString("N")));

        for (var index = 0; index < itemCount; index++)
        {
            await IntegrationDb.ExecAsync(connection,
                """
                INSERT shop.OrderItems
                    (Id, OrderId, MerchantId, Quantity, UnitPriceAmount, UnitPriceCurrency,
                     DiscountAmount, DiscountCurrency, ProductCode, VariantCode, VariantName)
                VALUES (@id, @orderId, @merchantId, 1, 100, N'THB', 0, N'THB',
                        @productCode, 'VMI', N'Reporting probe');
                """,
                ("@id", Guid.NewGuid()), ("@orderId", orderId), ("@merchantId", merchantId),
                ("@productCode", $"REPORT-{orderId:N}-{index}"));
        }

        await IntegrationDb.ExecAsync(connection,
            """
            INSERT txn.PaymentSessions
                (Id, MerchantId, OrderId, Method, Psp, Status, CreatedAt, UpdatedAt,
                 AmountAmount, AmountCurrency)
            VALUES (@id, @merchantId, @orderId, N'card', 1, 1, @createdAt, @createdAt,
                    @amount, N'THB');
            """,
            ("@id", sessionId), ("@merchantId", merchantId), ("@orderId", orderId),
            ("@createdAt", createdAt), ("@amount", amount));
    }

    private static async Task CleanupAsync(IEnumerable<Guid> orderIds)
    {
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        foreach (var orderId in orderIds)
        {
            await IntegrationDb.ExecAsync(connection,
                "DELETE txn.PaymentSessions WHERE OrderId = @id;", ("@id", orderId));
            await IntegrationDb.ExecAsync(connection,
                "DELETE shop.OrderItems WHERE OrderId = @id;", ("@id", orderId));
            await IntegrationDb.ExecAsync(connection,
                "DELETE shop.Orders WHERE Id = @id;", ("@id", orderId));
        }
    }

    private sealed class FakeActor(Guid merchantId) : IActorContext
    {
        public Guid MerchantId => merchantId;
        public Guid? UserId => null;
        public bool HasActor => true;
    }

    private sealed class AllowAllWriteAuthorizer : IWriteAuthorizer
    {
        public static readonly AllowAllWriteAuthorizer Instance = new();
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }
}
