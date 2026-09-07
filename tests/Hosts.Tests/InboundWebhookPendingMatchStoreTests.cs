using System.Security.Cryptography;
using System.Text;
using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Payments.Application.HandlePspWebhook;
using Payments.Domain;
using Payments.Domain.Psp;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Outbox;
using Persistence.MerchantRuntime.Payments;
using SharedKernel;

namespace Hosts.Tests;

/// <summary>
/// Store-level proof of the fetch-confirm-only pending-match record (merchant-psp-settings AC-8.4/8.6):
/// a pre-bind Omise webhook redelivered three times leaves ONE pending row (the unique index dedups by
/// event id, adversarial #6) and enqueues ONE rematch outbox event, and the row keeps only a bounded
/// reference — never a raw payload or secret.
/// </summary>
public sealed class InboundWebhookPendingMatchStoreTests : IDisposable
{
    private static readonly Guid MerchantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTime Now = new(2026, 9, 7, 4, 0, 0, DateTimeKind.Utc);
    private const string ChargeId = "chrg_test_123";
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public InboundWebhookPendingMatchStoreTests()
    {
        _connection.Open();
        using var setup = NewContext();
        setup.Database.EnsureCreated();
    }

    [Fact]
    public async Task Three_pre_bind_deliveries_of_one_event_leave_one_pending_row_and_one_rematch_event()
    {
        var connection = Connection.Create(MerchantId, Code.Omise, PaymentMethods.Card, "psp/ref", Now);
        await using (var seed = NewContext())
        {
            seed.Set<Connection>().Add(connection);
            await seed.SaveChangesAsync();
        }

        await using (var db = NewContext())
        {
            var store = new InboundWebhookStore(
                db, new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance),
                new EfOutbox(db, new FixedClock(), new Actor()), new FixedClock());

            for (var i = 0; i < 3; i++)
                await store.RecordPendingMatchAsync(
                    connection.Id, MerchantId, "omise", "evnt_1", ChargeId, Fingerprint("body"), default);
        }

        await using var verify = NewContext();
        var rows = await verify.Set<InboundWebhookEvent>().IgnoreQueryFilters().ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(InboundWebhookStatus.PendingMatch, row.Status);
        Assert.Equal(ChargeId, row.ExternalChargeId);
        Assert.Null(row.SignatureValid);
        Assert.Equal(WebhookVerificationMode.FetchConfirmOnly, row.VerificationMode);

        var outbox = await verify.OutboxMessages.IgnoreQueryFilters().ToListAsync();
        var message = Assert.Single(outbox);
        Assert.Equal("payments.inbound-webhook-match-requested.v1", message.Type);
        Assert.DoesNotContain("body", message.Payload); // bounded reference only, no raw payload (AC-8.6)
    }

    private MerchantRuntimeDbContext NewContext() => new(
        new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
        new Actor(), new AllowAllWrites(), NoOpSecurityTelemetry.Instance);

    private static string Fingerprint(string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    public void Dispose() => _connection.Dispose();

    private sealed class Actor : IActorContext
    {
        public Guid MerchantId => InboundWebhookPendingMatchStoreTests.MerchantId;
        public Guid? UserId => Guid.NewGuid();
        public bool HasActor => true;
    }

    private sealed class AllowAllWrites : IWriteAuthorizer
    {
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Now;
    }
}
