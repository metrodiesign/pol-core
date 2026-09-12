using Contracts;
using Payments.Application.Confirmation;
using Payments.Application.HandlePspWebhook;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Tests;

/// <summary>
/// The two-direction, idempotent rematch that closes the fetch-confirm-only (Omise) webhook/charge-bind
/// race (merchant-psp-settings AC-8.4, adversarial #6/#7). Whichever of <see cref="InboundWebhookMatchRequested"/>
/// (webhook first) or <see cref="PspChargeBound"/> (bind first) commits first drives ONE confirm; the other
/// finds no pending row and is a no-op, so there is exactly one transition and one <c>PaymentPaid</c>.
/// </summary>
public sealed class InboundWebhookRematcherTests
{
    private static readonly Guid MerchantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Money Amount = Money.Of(250.09m, "THB");
    private static readonly DateTime Now = new(2026, 7, 26, 9, 0, 0, DateTimeKind.Utc);
    private const string ChargeId = "chrg_test_123";

    /// <summary>Recorder that holds pending rows in memory. Complete mutates the same objects it returns, so
    /// a second lookup finds them terminal — the idempotency the rematch relies on.</summary>
    private sealed class FakeRecorder : IInboundWebhookRecorder
    {
        private readonly List<InboundWebhookEvent> _pending = [];

        public FakeRecorder(int pendingCount)
        {
            for (var i = 0; i < pendingCount; i++)
                _pending.Add(InboundWebhookEvent.PendingMatch(
                    default, MerchantId, "omise", $"evnt_{i}", ChargeId, new string('a', 64), Now));
        }

        public int TerminalCount => _pending.Count(x => x.Status != InboundWebhookStatus.PendingMatch);

        public Task<IReadOnlyList<InboundWebhookEvent>> FindPendingMatchesAsync(
            Guid connectionId, string externalChargeId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InboundWebhookEvent>>(_pending
                .Where(x => x.ExternalChargeId == externalChargeId && x.Status == InboundWebhookStatus.PendingMatch)
                .ToList());

        public Task RecordRejectedAsync(Guid c, Guid m, string p, string f, bool s, string fc, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<InboundWebhookClaim> ClaimAsync(Guid c, Guid m, string p, string e, string f, WebhookVerificationMode mode, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task RecordPendingMatchAsync(Guid c, Guid m, string p, string e, string ch, string f, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<InboundWebhookEvent> LoadAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed record World(InboundWebhookRematcher Rematcher, Guid ConnectionId, FakeRecorder Recorder, FakeOutbox Outbox, Session Session);

    private static World Build(int pendingCount, PspChargeStatus fetched = PspChargeStatus.Paid)
    {
        var connection = Connection.Create(MerchantId, Code.Omise, PaymentMethods.Card, "psp/secret-ref", Now);
        var session = Session.Create(MerchantId, OrderId, Amount, PaymentMethods.Card, Code.Omise,
            connection.Id, Guid.NewGuid(), PspEnvironment.Sandbox, Now);
        session.BeginRedirect(Now);
        session.SetPspCharge(ChargeId, "https://omise.test/pay", Now);

        var outbox = new FakeOutbox();
        var unitOfWork = new FakeUnitOfWork();
        var connections = new FakeConnectionRepository(connection);
        var adapters = new FakePspAdapterFactory(new FakePspAdapter(Code.Omise, PaymentMethods.Card)
        {
            Mode = WebhookVerificationMode.FetchConfirmOnly,
            OnFetchCharge = _ => new PspChargeConfirmation(fetched, Amount),
        });
        var vault = new FakeVaultSecretStore();
        var recorder = new FakeRecorder(pendingCount);
        var confirmation = new PaymentConfirmationService(
            connections, adapters, vault, new FakeIdempotencyStore(), outbox, unitOfWork,
            new FixedClock { UtcNow = Now }, new RecordingLogger<PaymentConfirmationService>());

        var rematcher = new InboundWebhookRematcher(
            connections, new FakeSessionRepository(session), confirmation, recorder, unitOfWork,
            new FixedClock { UtcNow = Now });

        return new World(rematcher, connection.Id, recorder, outbox, session);
    }

    [Fact]
    public async Task Both_directions_together_confirm_once_and_resolve_every_pending_row()
    {
        // Two distinct pre-bind webhooks parked; the bind lands.
        var world = Build(pendingCount: 2);

        await world.Rematcher.Handle(
            new InboundWebhookMatchRequested(Guid.NewGuid(), MerchantId, world.ConnectionId, ChargeId, Now), default);
        // The other direction fires after — must be a no-op.
        await world.Rematcher.Handle(
            new PspChargeBound(Guid.NewGuid(), MerchantId, world.ConnectionId, world.Session.Id, ChargeId, Now), default);

        Assert.Equal(SessionStatus.Paid, world.Session.Status);
        Assert.Single(world.Outbox.Enqueued.OfType<PaymentPaid>()); // exactly one transition
        Assert.Equal(2, world.Recorder.TerminalCount); // every pending row resolved
    }

    [Fact]
    public async Task A_charge_bound_event_with_no_pending_match_is_a_no_op()
    {
        var world = Build(pendingCount: 0);

        await world.Rematcher.Handle(
            new PspChargeBound(Guid.NewGuid(), MerchantId, world.ConnectionId, world.Session.Id, ChargeId, Now), default);

        Assert.Empty(world.Outbox.Enqueued);
    }

    [Fact]
    public async Task A_pending_fetch_resolves_the_row_without_marking_paid()
    {
        // The fetch does not confirm paid: the pending row is resolved (Ignored), nothing is marked paid and
        // no PaymentPaid is published. A later settlement webhook takes the normal bound path.
        var world = Build(pendingCount: 1, fetched: PspChargeStatus.Pending);

        await world.Rematcher.Handle(
            new InboundWebhookMatchRequested(Guid.NewGuid(), MerchantId, world.ConnectionId, ChargeId, Now), default);

        Assert.NotEqual(SessionStatus.Paid, world.Session.Status);
        Assert.Empty(world.Outbox.Enqueued.OfType<PaymentPaid>());
        Assert.Equal(1, world.Recorder.TerminalCount);
    }
}
