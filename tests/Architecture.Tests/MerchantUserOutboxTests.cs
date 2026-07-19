using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Outbox;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.MerchantUser;
using Persistence.MerchantUser.Outbox;

namespace Architecture.Tests;

/// <summary>
/// Proves the rls-to-query-filter task 5 per-owner outbox split for the MerchantUser cluster:
/// <see cref="MerchantUserOutbox"/> is filtered per-merchant like every other entity in this context
/// (REQ-1.6), and <see cref="MerchantUserOutboxDrain"/> is the ONE sanctioned way to see across owners for
/// the dispatcher's lease-claim scan — proven by leasing rows for TWO different merchants in one call,
/// which the ordinary per-merchant filter alone could never do.
/// </summary>
public sealed class MerchantUserOutboxTests : IDisposable
{
    private static readonly Guid MerchantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MerchantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly SqliteConnection _connection;

    public MerchantUserOutboxTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = NewContext(FakeActorContext.Unbound);
        setup.Database.EnsureCreated();
    }

    private MerchantUserDbContext NewContext(BuildingBlocks.Application.IActorContext actor) =>
        new(new DbContextOptionsBuilder<MerchantUserDbContext>().UseSqlite(_connection).Options,
            actor, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    [Fact]
    public async Task Merchant_A_never_reads_merchant_B_outbox_rows()
    {
        await SeedAsync(MerchantA, "a-event");
        await SeedAsync(MerchantB, "b-event");

        using var asA = NewContext(FakeActorContext.For(MerchantA));
        var visible = await asA.UserOutbox.ToListAsync();

        Assert.Single(visible);
        Assert.Equal("a-event", visible[0].Type);
    }

    [Fact]
    public async Task Drain_leases_rows_across_every_merchant_in_one_call()
    {
        await SeedAsync(MerchantA, "a-event");
        await SeedAsync(MerchantB, "b-event");

        using var draining = NewContext(FakeActorContext.Unbound);
        var drain = new MerchantUserOutboxDrain(draining);
        var leased = await drain.LeaseNextBatchAsync(
            batchSize: 10, owner: "worker-1", now: DateTime.UtcNow, leaseDuration: TimeSpan.FromMinutes(1),
            maxAttempts: 8, CancellationToken.None);

        Assert.Equal(2, leased.Count);
        Assert.Contains(leased, m => m.MerchantId == MerchantA);
        Assert.Contains(leased, m => m.MerchantId == MerchantB);
        Assert.All(leased, m => Assert.Equal("worker-1", m.LeaseOwner));
        Assert.All(leased, m => Assert.Equal(1, m.Attempts));
    }

    [Fact]
    public async Task Drain_skips_an_already_leased_unexpired_row()
    {
        await SeedAsync(MerchantA, "a-event");
        using (var firstDrain = NewContext(FakeActorContext.Unbound))
        {
            var firstBatch = await new MerchantUserOutboxDrain(firstDrain).LeaseNextBatchAsync(
                10, "worker-1", DateTime.UtcNow, TimeSpan.FromMinutes(1), 8, CancellationToken.None);
            Assert.Single(firstBatch);
        }

        using var secondDrain = NewContext(FakeActorContext.Unbound);
        var secondBatch = await new MerchantUserOutboxDrain(secondDrain).LeaseNextBatchAsync(
            10, "worker-2", DateTime.UtcNow, TimeSpan.FromMinutes(1), 8, CancellationToken.None);

        Assert.Empty(secondBatch);
    }

    [Fact]
    public async Task Drain_excludes_a_row_that_already_exhausted_max_attempts()
    {
        await SeedAsync(MerchantA, "a-event");
        using (var exhaust = NewContext(FakeActorContext.Unbound))
        {
            var row = await exhaust.UserOutbox.IgnoreQueryFilters().SingleAsync();
            row.Lease("worker-x", DateTime.UtcNow.AddMinutes(-10)); // Attempts = 1, already-expired lease
            await exhaust.SaveChangesAsync();
        }

        using var draining = NewContext(FakeActorContext.Unbound);
        var leased = await new MerchantUserOutboxDrain(draining).LeaseNextBatchAsync(
            10, "worker-2", DateTime.UtcNow, TimeSpan.FromMinutes(1), maxAttempts: 1, CancellationToken.None);

        Assert.Empty(leased); // Attempts (1) is not < maxAttempts (1)
    }

    [Fact]
    public async Task FindLeased_returns_the_row_to_the_unbound_dispatcher_that_holds_the_lease()
    {
        await SeedAsync(MerchantA, "a-event");
        Guid leasedId;
        using (var leasing = NewContext(FakeActorContext.Unbound))
        {
            var batch = await new MerchantUserOutboxDrain(leasing).LeaseNextBatchAsync(
                10, "worker-1", DateTime.UtcNow, TimeSpan.FromMinutes(1), 8, CancellationToken.None);
            leasedId = Assert.Single(batch).Id;
        }

        // Codex P1 regression: a plain db.UserOutbox lookup under the unbound dispatcher is filtered to
        // nothing (CurrentMerchant == Guid.Empty), so the message would lease forever and never publish.
        using var unbound = NewContext(FakeActorContext.Unbound);
        Assert.Null(await unbound.UserOutbox.FirstOrDefaultAsync(m => m.Id == leasedId));

        var drain = new MerchantUserOutboxDrain(unbound);
        var found = await drain.FindLeasedAsync(leasedId, "worker-1", CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(MerchantA, found.MerchantId);

        // Lease-owner scoped: a dispatcher that does not hold the lease sees nothing.
        Assert.Null(await drain.FindLeasedAsync(leasedId, "worker-2", CancellationToken.None));
    }

    private async Task SeedAsync(Guid merchantId, string type)
    {
        using var writer = NewContext(FakeActorContext.For(merchantId));
        writer.UserOutbox.Add(MerchantUserOutbox.Create(Guid.NewGuid(), merchantId, type, "{}", DateTime.UtcNow));
        await writer.SaveChangesAsync();
    }

    public void Dispose() => _connection.Dispose();
}
