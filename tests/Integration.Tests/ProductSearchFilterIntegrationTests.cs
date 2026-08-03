using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Products;
using Products.Application;
using Products.Application.Ports;
using Products.Domain;

namespace Integration.Tests;

/// <summary>
/// The sold-document filter (purchase-flow-completion REQ-5.1-5.4) end to end against the live catalogue:
/// a document is searched, sold (<c>DocumentPaidOnOrderPaidConsumer</c>), then searched again while the
/// upstream — which is never told about our payment — still reports it UNPAID. Only a real SQL Server can
/// prove this chain: that the upsert keeps the local PAID, that <c>SoldOrderId</c> round-trips through the
/// new <c>uniqueidentifier NULL</c> column, and that the page the merchant sees drops the row.
/// Every document here carries a run-unique <c>DocumentNo</c> prefix and its own SaleCode, and is deleted
/// again at the end, so the suite never touches the demo seed.
/// Tagged Integration: the default unit run skips these; CI runs them against a live SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProductSearchFilterIntegrationTests : IAsyncLifetime
{
    /// <summary>Products carry no merchant of their own (central catalogue, no query filter), so the bound
    /// actor only has to exist.</summary>
    private static readonly Guid AnyMerchant = IntegrationDb.MerchantA;

    /// <summary>Not the demo seed's 77001 — nothing here may depend on, or disturb, those rows.</summary>
    private const string SaleCode = "90001";

    private readonly string _prefix = $"IT-{Guid.NewGuid():N}";

    private string DocumentNo(string suffix) => $"{_prefix}/{suffix}";

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await IntegrationDb.ExecAsync(connection,
            "DELETE FROM shop.Products WHERE DocumentNo LIKE @prefix + N'%';", ("@prefix", _prefix));
    }

    private static MerchantRuntimeDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlServer(IntegrationDb.AppConn).Options,
            new FakeActor(AnyMerchant), AllowAllWriteAuthorizer.Instance, NoOpSecurityTelemetry.Instance);

    /// <summary>One upstream row, always UNPAID — the source system learns about our payments separately,
    /// if ever, so this is what a search keeps returning for a document we already sold.</summary>
    private static SpDocumentItem UnpaidRow(string documentNo) =>
        new("Motor", "VMI", "POLICY", documentNo,
            null, null, null, null, null, null, null, null,
            SaleCode, null, null, null, null, null, null, null,
            null, null, null,
            null, null, null, 15900m, null, null, null,
            null, "UNPAID");

    private static async Task<ProductPage> SearchAsync(
        MerchantRuntimeDbContext db, string? paymentStatus, params SpDocumentItem[] upstream) =>
        await new ListProductsHandler(
                new FixedGateway(upstream), new ProductRepository(db),
                NullLogger<ListProductsHandler>.Instance)
            .Handle(
                new ListProductsQuery
                {
                    ProductFilters = new ProductFilterDto
                    {
                        SaleCode = SaleCode,
                        ProductGroup = ProductGroup.VMI,
                        PaymentStatus = paymentStatus,
                    },
                },
                CancellationToken.None);

    // REQ-5.1/5.3/5.4 — the whole point of the task: a document sold here disappears from the UNPAID page
    // the merchant sells from, while the row itself stays put, PAID, tied to the order that bought it.
    [Fact]
    public async Task A_document_sold_here_drops_off_the_UNPAID_page_the_upstream_still_offers()
    {
        var sold = DocumentNo("sold");
        var forSale = DocumentNo("for-sale");
        var orderId = Guid.NewGuid();
        Guid soldProductId;

        // The merchant's first search: both documents are on sale.
        await using (var db = NewContext())
        {
            var page = await SearchAsync(db, null, UnpaidRow(sold), UnpaidRow(forSale));
            Assert.Equal([sold, forSale], page.Items.Select(i => i.DocumentNo));
            soldProductId = page.Items.Single(i => i.DocumentNo == sold).Id;
        }

        // A paid order retires one of them, through the real consumer and the real write path.
        await using (var db = NewContext())
            await new DocumentPaidOnOrderPaidConsumer(
                    new ProductRepository(db), new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance),
                    NullLogger<DocumentPaidOnOrderPaidConsumer>.Instance)
                .Handle(
                    new Contracts.OrderPaid(AnyMerchant, [soldProductId], DateTime.UtcNow, orderId),
                    CancellationToken.None);

        // The next search: the upstream still reports both UNPAID, and the sold one is gone anyway.
        await using (var db = NewContext())
        {
            var page = await SearchAsync(db, null, UnpaidRow(sold), UnpaidRow(forSale));

            Assert.Equal([forSale], page.Items.Select(i => i.DocumentNo));
            // REQ-5.2 — the procedure counted two rows and keeps saying two; nothing is recounted here.
            Assert.Equal(2, page.TotalRows);
        }

        // Filtered, not lost: asking for ALL still finds it, PAID, with the order that bought it stored in
        // the new column (REQ-5.3/5.4).
        await using (var db = NewContext())
        {
            var item = Assert.Single((await SearchAsync(db, "ALL", UnpaidRow(sold))).Items);
            Assert.Equal(sold, item.DocumentNo);
            Assert.Equal(PaymentStatus.PAID, item.PaymentStatus);
        }

        await using var verify = NewContext();
        var row = await verify.Set<Product>().SingleAsync(p => p.DocumentNo == sold);
        Assert.Equal(PaymentStatus.PAID, row.PaymentStatus);
        Assert.Equal(orderId, row.SoldOrderId);
    }

    private sealed class FixedGateway(SpDocumentItem[] items) : ISpDocumentGateway
    {
        public Task<SpDocumentSearchResult> SearchAsync(SpDocumentSearchRequest request, CancellationToken ct) =>
            Task.FromResult(new SpDocumentSearchResult(
                new SpPaginationMetadata(items.Length, 1, 1, 25, HasNextPage: false, HasPreviousPage: false, "EXACT", 6),
                items));
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
