using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Persistence.MerchantRuntime;
using PaymentSession = Payments.Domain.Session;

namespace Architecture.Tests;

/// <summary>
/// Offline proof of the one-open-session-per-order DB floor (captive-payment-alignment REQ-2.6). The floor's
/// BEHAVIOUR is proven against real SQL Server in Integration.Tests, but CI skips the whole integration job
/// whenever the <c>MSSQL_SA_PASSWORD</c> secret is absent (ci.yml "integration gate"), so declaring the index
/// in only one of the two <c>SessionConfiguration</c> files — the migration owner's or the runtime context's —
/// would pass every check that always runs: the DDL would lack the index while the unit suite stayed green
/// (or vice versa). This asserts both models carry it, identically.
///
/// Model built the Sqlite-in-memory way <see cref="ModelDisjointnessTests"/> uses (index filters are
/// provider-independent relational metadata, so the string reads back verbatim). Sessions cannot be INSERTed
/// through this harness — <c>RowVersion</c> is <c>IsRowVersion()</c>, which SQLite cannot generate — which is
/// exactly why the enforcement half of REQ-2.4/2.5 lives in the live-SQL suite.
/// </summary>
public sealed class OpenSessionIndexTests : IDisposable
{
    private const string IndexName = "IX_PaymentSessions_OrderId_Open";
    private const string Filter = "[Status] IN (1, 2)";

    private readonly SqliteConnection _ownerConnection = OpenSqlite();
    private readonly SqliteConnection _runtimeConnection = OpenSqlite();
    private readonly PolDbContext _owner;
    private readonly MerchantRuntimeDbContext _runtime;

    public OpenSessionIndexTests()
    {
        _owner = new PolDbContext(
            new DbContextOptionsBuilder<PolDbContext>().UseSqlite(_ownerConnection)
                .EnableServiceProviderCaching(false).Options,
            new ModuleAssemblies([typeof(Payments.Infrastructure.PaymentsModuleRegistration).Assembly]));

        _runtime = new MerchantRuntimeDbContext(
            new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_runtimeConnection)
                .EnableServiceProviderCaching(false).Options,
            FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
    }

    [Fact]
    public void The_migration_owner_declares_the_one_open_session_per_order_index() => AssertOpenIndex(_owner);

    [Fact]
    public void The_runtime_context_declares_the_identical_index() => AssertOpenIndex(_runtime);

    [Fact]
    public void The_plain_OrderId_lookup_index_survives_in_both_contexts()
    {
        // The named overload is load-bearing: EF keys UNNAMED indexes by property set, so a second
        // HasIndex(x => x.OrderId) would have mutated this lookup index into the unique filtered one rather
        // than adding a second index — silently turning every ordinary by-order read unique.
        foreach (var db in new DbContext[] { _owner, _runtime })
        {
            var plain = Indexes(db).Single(i => i.Name is null && i.Properties is [{ Name: "OrderId" }]);

            Assert.False(plain.IsUnique);
            Assert.Null(plain.GetFilter());
        }
    }

    private static void AssertOpenIndex(DbContext db)
    {
        var index = Indexes(db).Single(i => i.Name == IndexName);

        Assert.Equal("OrderId", index.Properties.Single().Name);
        Assert.True(index.IsUnique);
        // Status 1/2 = Created/Redirected. Paid/Failed/Expired sit outside the filter on purpose (REQ-7.4):
        // a declined attempt must still let the same order open a fresh session.
        Assert.Equal(Filter, index.GetFilter());
    }

    private static IEnumerable<IIndex> Indexes(DbContext db) =>
        (db.Model.FindEntityType(typeof(PaymentSession))
            ?? throw new InvalidOperationException("Payments.Domain.Session is not in the model."))
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
