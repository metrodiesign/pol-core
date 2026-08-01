using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Products;
using Products.Application;
using Products.Application.Ports;

namespace Hosts.Tests;

/// <summary>
/// products-sp-gateway task 6 (REQ-7.1/7.2/7.9): an upstream §5.2 row survives the whole cutover path —
/// gateway -> <see cref="SpDocumentItemMapper"/> -> <c>Product.Create</c> -> <see cref="ProductRepository"/>'s
/// upsert -> the answered <see cref="ProductListItem"/> — and is still there field-for-field when read back
/// by id, the read the cart uses. Every step is the REAL Application handler over the REAL
/// <see cref="ProductRepository"/>/<see cref="MerchantRuntimeDbContext"/> (reached through the
/// InternalsVisibleTo grant insurance-pivot task 0 added) on SQLite in-memory; only the two upstream
/// procedures are stubbed, and those are exercised for real in <c>Integration.Tests</c>.
/// <para>
/// This replaces the create -> list round trip this file used to prove: the list no longer reads
/// <c>shop.Products</c>, so a document reaching the list is now a document the upstream returned.
/// </para>
/// </summary>
public sealed class ProductInsuranceFieldsRoundTripTests : IDisposable
{
    private static readonly Guid MerchantA = Guid.NewGuid();

    private const string DocumentNo = "77001-69900/กธ/950001-10";

    private readonly SqliteConnection _connection;

    public ProductInsuranceFieldsRoundTripTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = NewContext();
        setup.Database.EnsureCreated();
    }

    private MerchantRuntimeDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
            FakeActor.For(MerchantA), AllowAllWriteAuthorizer.Instance, NoOpSecurityTelemetry.Instance);

    [Fact]
    public async Task Upstream_document_fields_survive_the_list_upsert_and_get_by_id()
    {
        var start = new DateTime(2026, 7, 1, 8, 30, 0);
        var end = new DateTime(2027, 6, 30, 23, 59, 59);

        // A full §5.2 row, in the column order the procedures return (SpDocumentContractTests pins it).
        var gateway = new FakeSpDocumentGateway(new SpDocumentItem(
            "Motor", "VMI", "POLICY", DocumentNo,
            "69", "900", "pre", "seq-1", "68", "ref-1", "branch-1", "type-1",
            "77001", "สมชาย ขาย", "BK", "โบรกเกอร์",
            "00098-68100/037674", "APP-1", "POL-0", "E950001",
            start, end, "สมชาย ใจดี",
            14800m, 10m, 1090m, 15900m, 12m, 500m, null,
            "1กก 1234", "UNPAID"));

        Guid productId;
        using (var db = NewContext())
        {
            var page = await new ListProductsHandler(
                    gateway, new ProductRepository(db), NullLogger<ListProductsHandler>.Instance)
                .Handle(
                    new ListProductsQuery
                    {
                        ProductFilters = new ProductFilterDto { SaleCode = "77001", InsuranceType = Products.Domain.InsuranceType.Motor },
                        Page = 1,
                        Limit = 25,
                    },
                    CancellationToken.None);

            // The §5.1 envelope is the procedure's, copied through untouched (REQ-8.1).
            Assert.Equal(1, page.TotalRows);
            Assert.Equal(1, page.TotalPages);
            Assert.Equal("EXACT", page.CountMode);
            Assert.Equal(6, page.SearchWindowMonths);
            Assert.False(page.HasNextPage);

            var item = Assert.Single(page.Items);
            productId = item.Id;
            Assert.NotEqual(Guid.Empty, item.Id);
            Assert.Equal(DocumentNo, item.DocumentNo);
            Assert.Equal(Products.Domain.DocumentType.POLICY, item.DocumentType);
            Assert.Equal(Products.Domain.ProductGroup.VMI, item.ProductGroup);
            Assert.Equal("สมชาย ใจดี", item.ShowName);
            Assert.Equal(15900m, item.TotalPremium);
            Assert.Equal(Products.Domain.PaymentStatus.UNPAID, item.PaymentStatus);
            Assert.Equal(start, item.StartDate);
            Assert.Equal(end, item.EndDate);
        }

        using (var db = NewContext())
        {
            var view = await new GetProductByIdHandler(new ProductRepository(db))
                .Handle(new GetProductByIdQuery(productId), CancellationToken.None);

            Assert.NotNull(view);
            Assert.Equal(DocumentNo, view!.DocumentNo);
            Assert.Equal("69", view.PolicyYear);
            Assert.Equal("00098-68100/037674", view.PolicyNumber);
            Assert.Equal("1กก 1234", view.LicensePlateNumber);
            Assert.Equal(15900m, view.TotalPremium);
            Assert.Equal(14800m, view.NetPremium);
            Assert.Equal(12m, view.CommissionPercent);
            Assert.Equal(Products.Domain.InsuranceType.Motor, view.InsuranceType);
        }
    }

    // REQ-7.2: the second search of the same document updates the row it already created — one document, one
    // Guid, so a cart line opened from the first page still points at it after the next refresh.
    [Fact]
    public async Task A_second_search_refreshes_the_same_document_instead_of_duplicating_it()
    {
        var gateway = new FakeSpDocumentGateway(Row(15900m));
        Guid firstId;

        using (var db = NewContext())
            firstId = Assert.Single((await ListAsync(gateway, db)).Items).Id;

        var refreshed = new FakeSpDocumentGateway(Row(16900m));
        using (var db = NewContext())
        {
            var item = Assert.Single((await ListAsync(refreshed, db)).Items);
            Assert.Equal(firstId, item.Id);
            Assert.Equal(16900m, item.TotalPremium);
        }

        using var verify = NewContext();
        Assert.Equal(1, await verify.Set<Products.Domain.Product>().CountAsync(p => p.DocumentNo == DocumentNo));
    }

    private static SpDocumentItem Row(decimal totalPremium) => new(
        "Motor", "VMI", "POLICY", DocumentNo,
        null, null, null, null, null, null, null, null,
        "77001", null, null, null, null, null, null, null,
        null, null, null,
        null, null, null, totalPremium, null, null, null, null, "UNPAID");

    private static Task<ProductPage> ListAsync(ISpDocumentGateway gateway, MerchantRuntimeDbContext db) =>
        new ListProductsHandler(gateway, new ProductRepository(db), NullLogger<ListProductsHandler>.Instance)
            .Handle(
                new ListProductsQuery
                {
                    ProductFilters = new ProductFilterDto { SaleCode = "77001", InsuranceType = Products.Domain.InsuranceType.Motor },
                },
                CancellationToken.None)
            .AsTask();

    private sealed class FakeActor(bool hasActor, Guid merchantId = default) : IActorContext
    {
        public static FakeActor For(Guid merchantId) => new(true, merchantId);

        public Guid MerchantId => hasActor ? merchantId : throw new InvalidOperationException("No actor bound.");
        public Guid? UserId => null;
        public bool HasActor => hasActor;
    }

    private sealed class AllowAllWriteAuthorizer : IWriteAuthorizer
    {
        public static readonly AllowAllWriteAuthorizer Instance = new();
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }

    public void Dispose() => _connection.Dispose();
}
