using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Idempotency;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Idempotency;

namespace Hosts.Tests;

public sealed class AdminCommerceOperationTests : IDisposable
{
    private static readonly Guid MerchantId = Guid.Parse("b1000000-0000-4000-8000-000000000001");
    private static readonly Guid ActorId = Guid.Parse("b2000000-0000-4000-8000-000000000001");
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public AdminCommerceOperationTests()
    {
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Same_key_and_intent_replays_bounded_result_without_running_target_twice()
    {
        await using var db = NewContext();
        var uow = new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance);
        var sut = new AdminOperationExecutor(db, new Clock(), uow);
        var calls = 0;
        var request = Request("cart.create", "same-key", "{\"merchantId\":1}");

        var first = await sut.ExecuteAsync(request, async ct =>
        {
            calls++;
            return await uow.ExecuteInTransactionAsync(
                _ => Task.FromResult(new TestResult("cart-1")), ct);
        }, value => value.Id, default);
        var replay = await sut.ExecuteAsync(request, _ =>
        {
            calls++;
            return Task.FromResult(new TestResult("cart-2"));
        }, value => value.Id, default);

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal("cart-1", replay.Value.Id);
        Assert.Equal(1, calls);
        Assert.Equal(AdminOperationState.Succeeded,
            (await db.AdminOperationRecords.IgnoreQueryFilters().SingleAsync()).State);
    }

    [Fact]
    public async Task Recoverable_operation_resumes_in_progress_target_but_rejects_changed_intent()
    {
        await using var db = NewContext();
        var uow = new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance);
        var sut = new AdminOperationExecutor(db, new Clock(), uow);
        var calls = 0;
        var request = Request("payment-session.redirect", "recover-key", "{\"session\":1}");

        await Assert.ThrowsAsync<TimeoutException>(() => sut.ExecuteRecoverableAsync<TestResult>(
            request, _ =>
            {
                calls++;
                throw new TimeoutException("ambiguous PSP result");
            }, value => value.Id, default));
        var resumed = await sut.ExecuteRecoverableAsync(request, _ =>
        {
            calls++;
            return Task.FromResult(new TestResult("session-1"));
        }, value => value.Id, default);

        Assert.False(resumed.Replayed);
        Assert.Equal(2, calls);
        await Assert.ThrowsAsync<ConflictException>(() => sut.ExecuteRecoverableAsync(
            request with { Intent = "{\"session\":2}" },
            _ => Task.FromResult(new TestResult("session-2")), value => value.Id, default));
    }

    private static AdminOperationRequest Request(string operation, string key, string intent) =>
        new(MerchantId, ActorId, operation, key, intent, 200);

    private MerchantRuntimeDbContext NewContext() => new(
        new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
        new Actor(), new AllowAllWrites(), NoOpSecurityTelemetry.Instance);

    public void Dispose() => _connection.Dispose();

    private sealed record TestResult(string Id);
    private sealed class Clock : IClock { public DateTime UtcNow => new(2026, 8, 10, 15, 0, 0, DateTimeKind.Utc); }
    private sealed class Actor : IActorContext
    {
        public Guid MerchantId => AdminCommerceOperationTests.MerchantId;
        public Guid? UserId => ActorId;
        public bool HasActor => true;
    }
    private sealed class AllowAllWrites : IWriteAuthorizer
    {
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }
}
