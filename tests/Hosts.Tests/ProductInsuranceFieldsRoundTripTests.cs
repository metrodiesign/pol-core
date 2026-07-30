using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Products;
using Products.Application;
using SharedKernel;

namespace Hosts.Tests;

/// <summary>
/// insurance-pivot task 1 (REQ-1/2): proves the 3 new insurance fields survive create -&gt; list -&gt;
/// get-by-id through the REAL Application handlers (<see cref="CreateProductHandler"/>,
/// <see cref="ListProductsHandler"/>, <see cref="GetProductByIdHandler"/>) backed by the REAL
/// <see cref="ProductRepository"/>/<see cref="MerchantRuntimeUnitOfWork"/> (Persistence.MerchantRuntime,
/// reached here via the same InternalsVisibleTo grant task 0 added) on SQLite in-memory. These handlers are
/// exactly what <c>POST /api/v1/products</c>/<c>GET /api/v1/products</c>/<c>GetProductByIdQuery</c> call —
/// only the HTTP/JSON transport (generic ASP.NET model binding, already proven for <c>Money</c> elsewhere per
/// design.md) is not re-exercised here.
/// </summary>
public sealed class ProductInsuranceFieldsRoundTripTests : IDisposable
{
    private static readonly Guid MerchantA = Guid.NewGuid();

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
    public async Task Created_document_fields_survive_list_and_get_by_id()
    {
        var totalPremium = Money.Of(15900m, "THB");
        var netPremium = Money.Of(14800m, "THB");
        var start = new DateTime(2026, 7, 1);
        var end = new DateTime(2027, 7, 1);

        Guid productId;
        using (var db = NewContext())
        {
            var handler = new CreateProductHandler(
                new ProductRepository(db, NullLogger<ProductRepository>.Instance),
                new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance), new SystemClock());

            productId = await handler.Handle(
                new CreateProductCommand(new Products.Domain.ProductInput(
                    MerchantA, Products.Domain.ProductGroup.VMI, Products.Domain.DocumentType.POLICY,
                    "00098-69100/กธ/037674-10", "100", "00098", totalPremium,
                    PolicyYear: "69", PolicyNumber: "00098-68100/037674",
                    StartDate: start, EndDate: end, ShowName: "สมชาย ใจดี",
                    LicensePlateNumber: "1กก 1234", NetPremium: netPremium, CommissionPercent: 12m)),
                CancellationToken.None);
        }

        using (var db = NewContext())
        {
            var listed = await new ListProductsHandler(new ProductRepository(db, NullLogger<ProductRepository>.Instance))
                .Handle(new ListProductsQuery { MerchantId = MerchantA, Page = 1, Limit = 10 }, CancellationToken.None);

            var item = Assert.Single(listed.Items);
            Assert.Equal("00098-69100/กธ/037674-10", item.DocumentNo);
            Assert.Equal(Products.Domain.DocumentType.POLICY, item.DocumentType);
            Assert.Equal(Products.Domain.ProductGroup.VMI, item.ProductGroup);
            Assert.Equal("สมชาย ใจดี", item.ShowName);
            Assert.Equal(totalPremium, item.TotalPremium);
            Assert.Equal(Products.Domain.PaymentStatus.UNPAID, item.PaymentStatus);
            Assert.Equal(start, item.StartDate);
            Assert.Equal(end, item.EndDate);
        }

        using (var db = NewContext())
        {
            var view = await new GetProductByIdHandler(new ProductRepository(db, NullLogger<ProductRepository>.Instance))
                .Handle(new GetProductByIdQuery(MerchantA, productId), CancellationToken.None);

            Assert.NotNull(view);
            Assert.Equal("00098-69100/กธ/037674-10", view!.DocumentNo);
            Assert.Equal("69", view.PolicyYear);
            Assert.Equal("00098-68100/037674", view.PolicyNumber);
            Assert.Equal("1กก 1234", view.LicensePlateNumber);
            Assert.Equal(totalPremium, view.TotalPremium);
            Assert.Equal(netPremium, view.NetPremium);
            Assert.Equal(12m, view.CommissionPercent);
            Assert.Equal(Products.Domain.InsuranceType.Motor, view.InsuranceType);
        }
    }

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
