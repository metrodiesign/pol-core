using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Orders.Domain;
using Orders.Domain.Items;
using Payments.Application.Capabilities;
using Payments.Application.Confirmation;
using Payments.Application.CreateSession;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Domain;
using Payments.Domain.Psp;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Outbox;
using Persistence.MerchantRuntime.Payments;
using SharedKernel;

namespace Hosts.Tests;

public sealed class PaymentAttemptAtomicityTests : IDisposable
{
    private static readonly Guid MerchantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTime Now = new(2026, 8, 7, 4, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public PaymentAttemptAtomicityTests()
    {
        _connection.Open();
        using var setup = NewContext();
        setup.Database.EnsureCreated();
    }

    [Fact]
    public async Task Session_insert_failure_rolls_back_Order_attempt_attachment()
    {
        var amount = Money.Of(100m, "THB");
        var order = Order.Create(
            MerchantId,
            amount,
            Now,
            [new OrderItemInput(1, amount, "DOC-1", "VMI", "ประกันรถยนต์")],
            orderNo: "ORD6900000001",
            paymentChannel: PaymentMethods.Card,
            initiatingAudience: OrderInitiatingAudience.User,
            initiatingMerchantUserId: Guid.NewGuid());
        await using (var seed = NewContext())
        {
            seed.Add(order);
            await seed.SaveChangesAsync();
        }

        await using (var db = NewContext())
        {
            var unitOfWork = new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance);
            var connections = new FixedConnections();
            var adapters = new FixedAdapters();
            var clock = new FixedClock();
            var sessions = new SessionRepository(db);
            var confirmation = new PaymentConfirmationService(
                connections,
                adapters,
                new NoOpVault(),
                new NeverClaimedIdempotency(),
                new EfOutbox(db, clock, new Actor()),
                unitOfWork,
                clock,
                NullLogger<PaymentConfirmationService>.Instance);
            var handler = new CreateSessionHandler(
                new PayableOrderReader(db),
                connections,
                adapters,
                sessions,
                confirmation,
                new AvailableDocuments(),
                unitOfWork,
                clock,
                new NoOpAuthorizationLocks(),
                new AllowCapabilities());

            // SQLite cannot generate SQL Server rowversion for PaymentSession. Its NOT NULL insert failure
            // occurs after Order.AttachPaymentAttempt, inside the shared transaction.
            await Assert.ThrowsAsync<DbUpdateException>(async () =>
                await handler.Handle(new CreateSessionCommand(
                    order.Id, MerchantId, PaymentMethods.Card, Code.TwoCTwoP), default));
        }

        await using var verify = NewContext();
        var persisted = await verify.Orders.SingleAsync(x => x.Id == order.Id);
        Assert.Equal(OrderStatus.Pending, persisted.Status);
        Assert.Null(persisted.PaymentSessionId);
        Assert.Equal(PaymentMethods.Card, persisted.PaymentChannel);
        Assert.Empty(await verify.PaymentSessions.ToListAsync());
    }

    private MerchantRuntimeDbContext NewContext() => new(
        new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
        new Actor(), new AllowAllWrites(), NoOpSecurityTelemetry.Instance);

    public void Dispose() => _connection.Dispose();

    private sealed class Actor : IActorContext
    {
        public Guid MerchantId => PaymentAttemptAtomicityTests.MerchantId;
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

    private sealed class FixedConnections : IConnectionRepository
    {
        private readonly Connection _connection = Connection.Create(
            MerchantId, Code.TwoCTwoP, PaymentMethods.Card, "psp/test", Now);
        public Task<Connection?> GetAsync(Guid merchantId, Code psp, CancellationToken cancellationToken) =>
            Task.FromResult<Connection?>(merchantId == MerchantId && psp == Code.TwoCTwoP ? _connection : null);
        public Task<Connection?> GetByIdAsync(Guid pspConnectionId, CancellationToken cancellationToken) =>
            Task.FromResult<Connection?>(_connection.Id == pspConnectionId ? _connection : null);
        public void Add(Connection connection) => throw new NotSupportedException();
        public Task<IReadOnlyList<Connection>> ListByTenantAsync(Guid merchantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Connection>>([_connection]);
    }

    private sealed class FixedAdapters : IPspAdapterFactory
    {
        private sealed class Adapter : IPspAdapter
        {
            public Code Psp => Code.TwoCTwoP;
            public IReadOnlySet<string> SupportedMethods { get; } = new HashSet<string> { PaymentMethods.Card };
            public Task<PspCharge> CreateRedirectChargeAsync(
                Payments.Domain.Session session, Guid pspConnectionId, string secret, CancellationToken cancellationToken) =>
                throw new NotSupportedException();
            public Task<PspChargeConfirmation> FetchChargeAsync(
                string externalChargeId, string secret, CancellationToken cancellationToken) =>
                throw new NotSupportedException();
            public bool VerifyWebhook(string rawPayload, string signature, string secret) => false;
            public WebhookEvent ParseWebhook(string rawPayload) => throw new NotSupportedException();
        }

        private static readonly IPspAdapter Instance = new Adapter();
        public IPspAdapter For(Code psp) => psp == Code.TwoCTwoP ? Instance : throw new ArgumentOutOfRangeException(nameof(psp));
    }

    private sealed class AvailableDocuments : IDocumentSaleProbe
    {
        public Task<IReadOnlyList<DocumentSaleStatus>> ProbeAsync(
            IReadOnlyCollection<DocumentKey> keys, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DocumentSaleStatus>>([]);
    }

    private sealed class NoOpAuthorizationLocks : IPaymentAuthorizationLockManager
    {
        public Task AcquireGlobalExclusiveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AcquireMerchantSharedAsync(Guid merchantId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AcquireMerchantExclusiveAsync(Guid merchantId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class AllowCapabilities : IEffectivePaymentCapabilityResolver
    {
        public Task<PaymentMethodDecision> ResolveMethodAsync(
            ResolvePaymentMethod request, CancellationToken cancellationToken) => Task.FromResult(
            new PaymentMethodDecision(true, request.Method, PaymentCapabilityDenial.None, Guid.NewGuid()));
        public Task<IReadOnlyList<EffectivePaymentMethod>> ListMethodsAsync(
            PaymentCapabilitySubject subject, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EffectivePaymentMethod>>([]);
        public Task<IReadOnlyList<EffectivePaymentOption>> ResolveOptionsAsync(
            ResolvePaymentMethod request, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EffectivePaymentOption>>([]);
    }

    private sealed class NeverClaimedIdempotency : IIdempotencyStore
    {
        public Task<bool> TryBeginAsync(
            IReadOnlyCollection<string> keys, string context, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class NoOpVault : IVaultSecretStore
    {
        public Task StoreAsync(Guid merchantId, string name, string plaintextSecret, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task InsertAsync(Guid merchantId, string name, string plaintextSecret, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<string> RevealAsync(Guid merchantId, string name, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<string?> MaskedAsync(Guid merchantId, string name, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> ExistsAsync(Guid merchantId, string name, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
