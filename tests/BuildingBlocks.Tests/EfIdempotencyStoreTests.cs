using BuildingBlocks.Infrastructure.Idempotency;

namespace BuildingBlocks.Tests;

/// <summary>
/// Observable contract of <see cref="EfIdempotencyStore.TryBeginAsync"/>: the first delivery of a
/// key-set wins (true), and any later delivery that shares even one key is rejected as a replay
/// (false). This is the guard that makes webhook handling exactly-once.
/// </summary>
public sealed class EfIdempotencyStoreTests
{
    private static readonly DateTime Now = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task TryBeginAsync_first_delivery_returns_true()
    {
        using var harness = ProducerDbContextTestHarness.Create();
        await using var db = harness.NewContext();
        var store = new EfIdempotencyStore(db, new FixedClock(Now));

        var first = await store.TryBeginAsync(["evt-1"], "webhook", CancellationToken.None);

        Assert.True(first);
    }

    [Fact]
    public async Task TryBeginAsync_exact_replay_returns_false()
    {
        using var harness = ProducerDbContextTestHarness.Create();

        await using (var db1 = harness.NewContext())
        {
            var store1 = new EfIdempotencyStore(db1, new FixedClock(Now));
            Assert.True(await store1.TryBeginAsync(["evt-1"], "webhook", CancellationToken.None));
        }

        // Replay arrives on a fresh per-scope context, exactly as the host would hand a new handler.
        await using var db2 = harness.NewContext();
        var store2 = new EfIdempotencyStore(db2, new FixedClock(Now));

        var replay = await store2.TryBeginAsync(["evt-1"], "webhook", CancellationToken.None);

        Assert.False(replay);
    }

    [Fact]
    public async Task TryBeginAsync_returns_false_when_any_shared_key_already_claimed()
    {
        using var harness = ProducerDbContextTestHarness.Create();

        await using (var db1 = harness.NewContext())
        {
            var store1 = new EfIdempotencyStore(db1, new FixedClock(Now));
            Assert.True(await store1.TryBeginAsync(["shared-key", "first-only"], "webhook", CancellationToken.None));
        }

        // A different multi-key delivery that overlaps on just ONE key must still be a replay.
        await using var db2 = harness.NewContext();
        var store2 = new EfIdempotencyStore(db2, new FixedClock(Now));

        var overlapping = await store2.TryBeginAsync(["second-only", "shared-key"], "webhook", CancellationToken.None);

        Assert.False(overlapping);
    }

    [Fact]
    public async Task TryBeginAsync_distinct_keys_both_succeed()
    {
        using var harness = ProducerDbContextTestHarness.Create();

        await using (var db1 = harness.NewContext())
        {
            var store1 = new EfIdempotencyStore(db1, new FixedClock(Now));
            Assert.True(await store1.TryBeginAsync(["evt-A"], "webhook", CancellationToken.None));
        }

        await using var db2 = harness.NewContext();
        var store2 = new EfIdempotencyStore(db2, new FixedClock(Now));

        var second = await store2.TryBeginAsync(["evt-B"], "webhook", CancellationToken.None);

        Assert.True(second);
    }

    [Fact]
    public async Task TryBeginAsync_empty_key_set_returns_true_without_persisting()
    {
        using var harness = ProducerDbContextTestHarness.Create();
        await using var db = harness.NewContext();
        var store = new EfIdempotencyStore(db, new FixedClock(Now));

        var result = await store.TryBeginAsync([], "webhook", CancellationToken.None);

        Assert.True(result);
    }
}
