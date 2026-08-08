using BuildingBlocks.Application;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Orders;

namespace Integration.Tests;

/// <summary>
/// <c>DoubleSellAuditor</c> against the real SQL Server (products-external-source-of-truth REQ-5.16). The
/// auditor is the platform's answer to a double sale that has ALREADY happened at the PSP: it reads
/// <c>shop.OrderItems</c> ACROSS merchants (the second buyer is very often another company, so a
/// merchant-filtered read would report nothing exactly when there IS something) and pages a human at Critical.
/// Both halves of the rule are properties of live rows — the cross-merchant read and the "do not count my own
/// order" carve-out — so a fake context proves neither.
/// Tagged Integration: the default unit run skips these; CI runs them against a live SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DoubleSellAuditorIntegrationTests
{
    private const string Group = "VMI";

    [Fact]
    public async Task A_document_paid_for_by_two_orders_pages_a_human_at_critical()
    {
        var documentNo = NewDocumentNo();
        var orders = new List<Guid>();
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        try
        {
            // The order under audit, plus a SECOND paid order — belonging to another merchant — holding the very
            // same document. This is the double sale the auditor exists to catch.
            var mine = await SeedOrderAsync(c, orders, IntegrationDb.MerchantA, OrderStatusPaid, documentNo);
            var other = await SeedOrderAsync(c, orders, IntegrationDb.MerchantB, OrderStatusPaid, documentNo);

            var logger = new RecordingLogger<DoubleSellAuditor>();
            await RunAsync(mine, logger);

            var critical = Assert.Single(logger.Critical);
            Assert.Contains(documentNo, critical, StringComparison.Ordinal);
            // The clashing order really is the other one — proves the cross-merchant read, not a self-match.
            Assert.Contains(other.ToString(), critical, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAsync(orders);
        }
    }

    [Fact]
    public async Task An_order_that_alone_holds_its_document_does_not_page_anyone()
    {
        var documentNo = NewDocumentNo();
        var orders = new List<Guid>();
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        try
        {
            // The common case and an outbox redelivery both look like this: one paid order holds its own
            // documents. The "item.OrderId != orderId" carve-out is what stops the auditor paging someone on
            // every retry — without it this order would clash with its own row.
            var mine = await SeedOrderAsync(c, orders, IntegrationDb.MerchantA, OrderStatusPaid, documentNo);

            var logger = new RecordingLogger<DoubleSellAuditor>();
            await RunAsync(mine, logger);

            Assert.Empty(logger.Critical);
        }
        finally
        {
            await CleanupAsync(orders);
        }
    }

    [Fact]
    public async Task A_second_holder_that_has_not_paid_is_not_a_double_sale()
    {
        var documentNo = NewDocumentNo();
        var orders = new List<Guid>();
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        try
        {
            // Only a PAID clash is a double sale: a second order still awaiting payment for the same document is
            // the ordinary in-flight state the sold-check already guards, not something to wake a human for.
            var mine = await SeedOrderAsync(c, orders, IntegrationDb.MerchantA, OrderStatusPaid, documentNo);
            await SeedOrderAsync(c, orders, IntegrationDb.MerchantB, OrderStatusAwaitingPayment, documentNo);

            var logger = new RecordingLogger<DoubleSellAuditor>();
            await RunAsync(mine, logger);

            Assert.Empty(logger.Critical);
        }
        finally
        {
            await CleanupAsync(orders);
        }
    }

    // ---- harness -------------------------------------------------------------------------------------

    private const int OrderStatusAwaitingPayment = 0;
    private const int OrderStatusPaid = 1;

    /// <summary>Run-unique so parallel runs (and leftovers from a crashed run) cannot answer for each other.</summary>
    private static string NewDocumentNo() => $"69100/{Guid.NewGuid():N}";

    private static async Task RunAsync(Guid orderId, ILogger<DoubleSellAuditor> logger)
    {
        await using var db = NewContext();
        var sut = new DoubleSellAuditor(db, logger);
        await sut.ReportIfDoubleSoldAsync(orderId, CancellationToken.None);
    }

    /// <summary>Bound to merchant A always — the cross-merchant assertion depends on the auditor ignoring that
    /// binding (IgnoreQueryFilters), so it must be a real one.</summary>
    private static MerchantRuntimeDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlServer(IntegrationDb.AppConn);
        return new MerchantRuntimeDbContext(
            options.Options, new FakeActor(IntegrationDb.MerchantA), AllowAllWriteAuthorizer.Instance,
            NoOpSecurityTelemetry.Instance);
    }

    private static async Task<Guid> SeedOrderAsync(
        SqlConnection c, List<Guid> orders, Guid merchantId, int status, string documentNo, string group = Group)
    {
        var orderId = Guid.NewGuid();
        orders.Add(orderId);
        await IntegrationDb.ExecAsync(c,
            """
            INSERT shop.Orders
                (Id, MerchantId, OrderNo, AmountAmount, AmountCurrency, Status, CreatedAt,
                 SummaryToken, SummaryTokenExpiresAt, CustomerName, CustomerPhone)
            VALUES (@id, @m, @orderNo, 15000, N'THB', @status, SYSUTCDATETIME(),
                    @token, DATEADD(hour, 72, SYSUTCDATETIME()), N'Probe', '0800000000');
            """,
            ("@id", orderId), ("@m", merchantId), ("@orderNo", $"ORD69{Random.Shared.Next(10_000_000, 99_999_999)}"),
            ("@status", status), ("@token", Guid.NewGuid().ToString("N")));

        await IntegrationDb.ExecAsync(c,
            """
            INSERT shop.OrderItems
                (Id, OrderId, MerchantId, Quantity, UnitPriceAmount, UnitPriceCurrency,
                 DiscountAmount, DiscountCurrency, ProductCode, VariantCode, VariantName)
            VALUES (@id, @orderId, @m, 1, 15000, N'THB', 0, N'THB', @documentNo, @group, N'ประกันรถยนต์');
            """,
            ("@id", Guid.NewGuid()), ("@orderId", orderId), ("@m", merchantId),
            ("@documentNo", documentNo), ("@group", group));

        return orderId;
    }

    private static async Task CleanupAsync(List<Guid> orders)
    {
        await using var sa = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        foreach (var orderId in orders)
        {
            await IntegrationDb.ExecAsync(sa, "DELETE shop.OrderItems WHERE OrderId = @id;", ("@id", orderId));
            await IntegrationDb.ExecAsync(sa, "DELETE shop.Orders WHERE Id = @id;", ("@id", orderId));
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<string> Critical =>
            _entries.Where(e => e.Level == LogLevel.Critical).Select(e => e.Message).ToList();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Add((logLevel, formatter(state, exception)));
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
