using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Idempotency;

namespace Architecture.Tests;

/// <summary>
/// Observable contract of <see cref="EfIdempotencyStore.TryBeginAsync"/> (ported from
/// <c>BuildingBlocks.Tests.EfIdempotencyStoreTests</c> onto <see cref="MerchantRuntimeDbContext"/>, task
/// 8.5.8 — the class moved into this assembly and is internal, so only a project with InternalsVisibleTo can
/// construct it directly): the first delivery of a key-set wins (true), and any later delivery that shares
/// even one key is rejected as a replay (false). This is the guard that makes webhook handling exactly-once.
/// </summary>
public sealed class EfIdempotencyStoreTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Merchant = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly SqliteConnection _connection;

    public EfIdempotencyStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = NewContext();
        setup.Database.EnsureCreated();
    }

    private MerchantRuntimeDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
            FakeActorContext.For(Merchant), FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    private sealed class FixedClock(DateTime utcNow) : BuildingBlocks.Application.IClock
    {
        public DateTime UtcNow => utcNow;
    }

    [Fact]
    public async Task TryBeginAsync_first_delivery_returns_true()
    {
        using var db = NewContext();
        var store = new EfIdempotencyStore(db, new FixedClock(Now), FakeActorContext.For(Merchant));

        var first = await store.TryBeginAsync(["evt-1"], "webhook", CancellationToken.None);

        Assert.True(first);
    }

    [Fact]
    public async Task TryBeginAsync_exact_replay_returns_false()
    {
        using (var db1 = NewContext())
        {
            var store1 = new EfIdempotencyStore(db1, new FixedClock(Now), FakeActorContext.For(Merchant));
            Assert.True(await store1.TryBeginAsync(["evt-1"], "webhook", CancellationToken.None));
        }

        // Replay arrives on a fresh per-scope context, exactly as the host would hand a new handler.
        using var db2 = NewContext();
        var store2 = new EfIdempotencyStore(db2, new FixedClock(Now), FakeActorContext.For(Merchant));

        var replay = await store2.TryBeginAsync(["evt-1"], "webhook", CancellationToken.None);

        Assert.False(replay);
    }

    [Fact]
    public async Task TryBeginAsync_returns_false_when_any_shared_key_already_claimed()
    {
        using (var db1 = NewContext())
        {
            var store1 = new EfIdempotencyStore(db1, new FixedClock(Now), FakeActorContext.For(Merchant));
            Assert.True(await store1.TryBeginAsync(["shared-key", "first-only"], "webhook", CancellationToken.None));
        }

        // A different multi-key delivery that overlaps on just ONE key must still be a replay.
        using var db2 = NewContext();
        var store2 = new EfIdempotencyStore(db2, new FixedClock(Now), FakeActorContext.For(Merchant));

        var overlapping = await store2.TryBeginAsync(["second-only", "shared-key"], "webhook", CancellationToken.None);

        Assert.False(overlapping);
    }

    [Fact]
    public async Task TryBeginAsync_distinct_keys_both_succeed()
    {
        using (var db1 = NewContext())
        {
            var store1 = new EfIdempotencyStore(db1, new FixedClock(Now), FakeActorContext.For(Merchant));
            Assert.True(await store1.TryBeginAsync(["evt-A"], "webhook", CancellationToken.None));
        }

        using var db2 = NewContext();
        var store2 = new EfIdempotencyStore(db2, new FixedClock(Now), FakeActorContext.For(Merchant));

        var second = await store2.TryBeginAsync(["evt-B"], "webhook", CancellationToken.None);

        Assert.True(second);
    }

    [Fact]
    public async Task TryBeginAsync_empty_key_set_returns_true_without_persisting()
    {
        using var db = NewContext();
        var store = new EfIdempotencyStore(db, new FixedClock(Now), FakeActorContext.For(Merchant));

        var result = await store.TryBeginAsync([], "webhook", CancellationToken.None);

        Assert.True(result);
    }

    public void Dispose() => _connection.Dispose();
}
