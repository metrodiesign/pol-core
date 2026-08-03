using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Persistence.MerchantRuntime;
using CheckoutSession = Checkouts.Domain.Session;

namespace Architecture.Tests;

/// <summary>
/// Offline proof of the one-live-checkout-per-cart DB floor (purchase-flow-completion REQ-2.4), the exact
/// sibling of <see cref="OpenSessionIndexTests"/> and for the same reason: the index's BEHAVIOUR is proven
/// against real SQL Server in Integration.Tests, but CI skips that whole job when the <c>MSSQL_SA_PASSWORD</c>
/// secret is absent, so declaring the index in only ONE of the two <c>SessionConfiguration</c> files — the
/// migration owner's (Checkouts.Infrastructure) or the runtime context's (Persistence.MerchantRuntime) —
/// would sail through every check that always runs.
/// </summary>
public sealed class OpenCheckoutIndexTests : IDisposable
{
    private const string IndexName = "IX_CheckoutSessions_CartId_Open";
    private const string Filter = "[Status] IN (0, 1)";

    private readonly SqliteConnection _ownerConnection = OpenSqlite();
    private readonly SqliteConnection _runtimeConnection = OpenSqlite();
    private readonly PolDbContext _owner;
    private readonly MerchantRuntimeDbContext _runtime;

    public OpenCheckoutIndexTests()
    {
        _owner = new PolDbContext(
            new DbContextOptionsBuilder<PolDbContext>().UseSqlite(_ownerConnection)
                .EnableServiceProviderCaching(false).Options,
            new ModuleAssemblies([typeof(Checkouts.Infrastructure.CheckoutModuleRegistration).Assembly]));

        _runtime = new MerchantRuntimeDbContext(
            new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_runtimeConnection)
                .EnableServiceProviderCaching(false).Options,
            FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
    }

    [Fact]
    public void The_migration_owner_declares_the_one_live_checkout_per_cart_index() => AssertOpenIndex(_owner);

    [Fact]
    public void The_runtime_context_declares_the_identical_index() => AssertOpenIndex(_runtime);

    private static void AssertOpenIndex(DbContext db)
    {
        var index = Indexes(db).Single(i => i.Name == IndexName);

        Assert.Equal("CartId", index.Properties.Single().Name);
        Assert.True(index.IsUnique);
        // SessionStatus 0/1 = Started/Confirmed. Abandoned (2) sits outside the filter on purpose (REQ-2.5):
        // abandoning a checkout is exactly what lets the same cart start a fresh one.
        Assert.Equal(Filter, index.GetFilter());
    }

    private static IEnumerable<IIndex> Indexes(DbContext db) =>
        (db.Model.FindEntityType(typeof(CheckoutSession))
            ?? throw new InvalidOperationException("Checkouts.Domain.Session is not in the model."))
        .GetIndexes();

    private static SqliteConnection OpenSqlite()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        _owner.Dispose();
        _runtime.Dispose();
        _ownerConnection.Dispose();
        _runtimeConnection.Dispose();
    }
}
