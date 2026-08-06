using BuildingBlocks.Application;
using Carts.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Persistence.MerchantRuntime;

namespace Architecture.Tests;

/// <summary>
/// Write failures must keep their EXISTING behavior while reads gain the 503 classification
/// (probe-dependency-failure-mapping REQ-1.5, design M4): a unique-key violation at commit still surfaces
/// as <see cref="ConflictException"/> (the 409 arm), and a transport failure during a write escapes RAW —
/// never as <see cref="DependencyUnavailableException"/>, whose 503 would invite a retry of a write whose
/// outcome is unknown.
/// </summary>
public sealed class MerchantRuntimeWriteRegressionTests : IDisposable
{
    private static readonly Guid Merchant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private readonly SqliteConnection _connection;

    public MerchantRuntimeWriteRegressionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = NewContext();
        setup.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task A_unique_violation_at_save_still_surfaces_as_the_conflict_409()
    {
        using var db = NewContext(new ThrowOnSave(
            new DbUpdateException("duplicate key", SqlExceptionFactory.Create(number: 2627))));
        var uow = new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance);
        db.Carts.Add(new Cart(Guid.NewGuid(), Merchant, DateTime.UtcNow));

        await Assert.ThrowsAsync<ConflictException>(() => uow.SaveChangesAsync(default));
    }

    [Fact]
    public async Task A_write_transport_failure_escapes_raw_never_as_dependency_unavailable()
    {
        // 10061 = TCP connection refused. Raw SqlException from a write has no handler arm -> opaque 500.
        using var db = NewContext(new ThrowOnSave(SqlExceptionFactory.Create(number: 10061)));
        var uow = new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance);
        db.Carts.Add(new Cart(Guid.NewGuid(), Merchant, DateTime.UtcNow));

        await Assert.ThrowsAsync<SqlException>(() => uow.SaveChangesAsync(default));
    }

    // spec-architect S5: a guarded read that fails INSIDE ExecuteInTransactionAsync must surface as the
    // SAME DependencyUnavailableException — not be replaced by a secondary error from rollback/dispose.
    // The read is a REAL DbException (SQLite: querying a table that does not exist) through the real unit
    // of work's strategy + transaction machinery; the 503 wire for this exception is proven by
    // ProblemDetailsExceptionHandlerTests.
    [Fact]
    public async Task A_guarded_read_failing_inside_the_transaction_still_surfaces_classified()
    {
        using var db = NewContext();
        var uow = new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance);

        var thrown = await Assert.ThrowsAsync<DependencyUnavailableException>(() =>
            uow.ExecuteInTransactionAsync(
                ct => PlatformReadGuard.ReadAsync(
                    c => db.Database.SqlQueryRaw<int>("SELECT V FROM table_that_does_not_exist").ToListAsync(c),
                    ct),
                default));

        Assert.IsAssignableFrom<System.Data.Common.DbException>(thrown.InnerException);
    }

    private MerchantRuntimeDbContext NewContext(IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection);
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);
        return new MerchantRuntimeDbContext(
            builder.Options, FakeActorContext.For(Merchant), FakeWriteAuthorizer.AllowAll,
            NoOpSecurityTelemetry.Instance);
    }

    /// <summary>Throws the configured exception at save time — the only way to make SQLite produce the
    /// SQL-Server-shaped failures the unit of work classifies (SQLite raises different types natively).</summary>
    private sealed class ThrowOnSave : SaveChangesInterceptor
    {
        private readonly Exception _exception;
        public ThrowOnSave(Exception exception) => _exception = exception;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
            => throw _exception;
    }
}
