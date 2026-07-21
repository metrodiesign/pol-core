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
    public async Task Created_insurance_fields_survive_list_and_get_by_id()
    {
        var price = Money.Of(2500m, "THB");
        var sumInsured = Money.Of(1_000_000m, "THB");

        Guid productId;
        using (var db = NewContext())
        {
            var handler = new CreateProductHandler(
                new ProductRepository(db, NullLogger<ProductRepository>.Instance),
                new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance), new SystemClock());

            productId = await handler.Handle(
                new CreateProductCommand(MerchantA, "Travel Plan A", price, sumInsured, 30, "Muang Thai Insurance"),
                CancellationToken.None);
        }

        using (var db = NewContext())
        {
            var listed = await new ListProductsHandler(new ProductRepository(db, NullLogger<ProductRepository>.Instance))
                .Handle(new ListProductsQuery { MerchantId = MerchantA, Page = 1, Limit = 10 }, CancellationToken.None);

            var item = Assert.Single(listed.Items);
            Assert.Equal(sumInsured, item.SumInsured);
            Assert.Equal(30, item.CoverageDurationDays);
            Assert.Equal("Muang Thai Insurance", item.Insurer);
        }

        using (var db = NewContext())
        {
            var view = await new GetProductByIdHandler(new ProductRepository(db, NullLogger<ProductRepository>.Instance))
                .Handle(new GetProductByIdQuery(MerchantA, productId), CancellationToken.None);

            Assert.NotNull(view);
            Assert.Equal(sumInsured, view!.SumInsured);
            Assert.Equal(30, view.CoverageDurationDays);
            Assert.Equal("Muang Thai Insurance", view.Insurer);
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
