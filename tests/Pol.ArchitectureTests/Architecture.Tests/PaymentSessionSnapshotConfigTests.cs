using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Persistence.MerchantRuntime;
using PaymentSession = Payments.Domain.Session;

namespace Architecture.Tests;

/// <summary>
/// Offline proof of the immutable-routing-snapshot contract (merchant-psp-settings REQ-2.9, AC-4.2). The
/// enforcement runs against real SQL Server in the integration suite, but the two <c>SessionConfiguration</c>
/// files — the migration owner's (reaches the DDL) and the runtime context's (validates runtime saves) —
/// must declare it identically or the divergence is invisible to the always-run suite. This asserts both
/// models carry the four snapshot columns and the version-1 CHECK constraint, verbatim.
/// </summary>
public sealed class PaymentSessionSnapshotConfigTests : IDisposable
{
    private const string CheckName = "CK_PaymentSessions_RoutingSnapshotV1";
    private const string CheckSql =
        "[RoutingSnapshotVersion] <> 1 OR ([PspConnectionId] IS NOT NULL AND [SecretVersionId] IS NOT NULL AND [PspEnvironment] IS NOT NULL)";

    private static readonly string[] SnapshotColumns =
        ["PspConnectionId", "SecretVersionId", "PspEnvironment", "RoutingSnapshotVersion"];

    private readonly SqliteConnection _ownerConnection = OpenSqlite();
    private readonly SqliteConnection _runtimeConnection = OpenSqlite();
    private readonly PolDbContext _owner;
    private readonly MerchantRuntimeDbContext _runtime;

    public PaymentSessionSnapshotConfigTests()
    {
        _owner = new PolDbContext(
            new DbContextOptionsBuilder<PolDbContext>().UseSqlite(_ownerConnection)
                .EnableServiceProviderCaching(false).Options,
                new ModuleAssemblies([
                    typeof(Payments.Infrastructure.PaymentsModuleRegistration),
                    typeof(Orders.Infrastructure.OrdersModuleRegistration),
                ]));

        _runtime = new MerchantRuntimeDbContext(
            new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_runtimeConnection)
                .EnableServiceProviderCaching(false).Options,
            FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
    }

    [Fact]
    public void The_migration_owner_declares_the_snapshot_columns_and_check() => AssertSnapshot(_owner);

    [Fact]
    public void The_runtime_context_declares_the_identical_snapshot_columns_and_check() => AssertSnapshot(_runtime);

    private static void AssertSnapshot(DbContext db)
    {
        // Check constraints live in the design-time model, not the read-optimized runtime model.
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(PaymentSession))
            ?? throw new InvalidOperationException("Payments.Domain.Session is not in the model.");

        foreach (var column in SnapshotColumns)
            Assert.NotNull(entity.FindProperty(column));

        var check = Assert.Single(entity.GetCheckConstraints(), c => c.Name == CheckName);
        Assert.Equal(CheckSql, check.Sql);
    }

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
