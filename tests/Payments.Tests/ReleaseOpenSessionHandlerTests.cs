using BuildingBlocks.Application;
using Contracts;
using Payments.Application.Confirmation;
using Payments.Application.Ports;
using Payments.Application.ReleaseOpenSession;
using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Tests;

/// <summary>
/// The release an order-cancel has to win first (purchase-flow-completion REQ-4.1-4.3). The handler owns one
/// decision — which confirmation outcomes are safe to cancel on — so every test asserts the session's own
/// state alongside the answer: releasing "successfully" while leaving a chargeable session behind, or
/// refusing after having expired one, would both pass an exception-only assertion.
/// </summary>
public sealed class ReleaseOpenSessionHandlerTests
{
    private static readonly Guid MerchantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Money Amount = Money.Of(1200m, "THB");
    private static readonly DateTime Created = new(2026, 7, 26, 9, 0, 0, DateTimeKind.Utc);

    private const string ChargeId = "INV-REL";

    private sealed record Harness(
        ReleaseOpenSessionHandler Handler, FakeOutbox Outbox, FakeUnitOfWork UnitOfWork, FakeVaultSecretStore Vault);

    private static Session NewSession(bool withCharge)
    {
        var session = Session.Create(MerchantId, OrderId, Amount, PaymentMethods.Card, Code.TwoCTwoP, Created);
        session.BeginRedirect(Created);
        if (withCharge)
            session.SetPspCharge(ChargeId, "https://2c2p.test/hosted/pay", Created);

        return session;
    }

    /// <summary>The clock defaults to creation time (nothing stale) and the PSP, when asked at all, reports
    /// <paramref name="fetchedStatus"/>. <paramref name="onFetchCharge"/> replaces the whole fetch so a test
    /// can make the inquiry itself fail.</summary>
    private static Harness NewHarness(
        Session? session,
        DateTime? now = null,
        PspChargeStatus fetchedStatus = PspChargeStatus.Pending,
        Func<string, PspChargeConfirmation>? onFetchCharge = null)
    {
        var outbox = new FakeOutbox();
        var unitOfWork = new FakeUnitOfWork();
        var vault = new FakeVaultSecretStore();

        var confirmation = new PaymentConfirmationService(
            new FakeConnectionRepository(
                Connection.Create(MerchantId, Code.TwoCTwoP, PaymentMethods.Card, "psp/secret-ref", Created)),
            new FakePspAdapterFactory(new FakePspAdapter(Code.TwoCTwoP, PaymentMethods.Card)
            {
                OnFetchCharge = onFetchCharge ?? (_ => new PspChargeConfirmation(fetchedStatus, Amount)),
            }),
            vault,
            new FakeIdempotencyStore(),
            outbox,
            unitOfWork,
            new FixedClock { UtcNow = now ?? Created },
            new RecordingLogger<PaymentConfirmationService>());

        var sessions = session is null ? new FakeSessionRepository() : new FakeSessionRepository(session);

        return new Harness(new ReleaseOpenSessionHandler(sessions, confirmation), outbox, unitOfWork, vault);
    }

    // REQ-4.1 — no chargeable session: nothing is holding the order, so the release is a silent success and
    // the PSP is never asked (a vault reveal here would mean an inquiry we had no reason to make).
    [Fact]
    public async Task An_order_with_no_open_session_is_released()
    {
        var harness = NewHarness(session: null);

        await harness.Handler.Handle(new ReleaseOpenSessionCommand(OrderId), default);

        Assert.Equal(0, harness.UnitOfWork.SaveCount);
        Assert.Equal(0, harness.Vault.Reveals);
    }

    // REQ-4.2 — a session that never got a charge id cannot be holding money, so once it is past its TTL it
    // is expired offline and the cancel may proceed.
    [Fact]
    public async Task A_stale_session_that_never_got_a_charge_is_expired_without_asking_the_PSP()
    {
        var session = NewSession(withCharge: false);
        var harness = NewHarness(session, now: Created + Session.OpenTtl);

        await harness.Handler.Handle(new ReleaseOpenSessionCommand(OrderId), default);

        Assert.Equal(SessionStatus.Expired, session.Status);
        Assert.Equal(1, harness.UnitOfWork.SaveCount);
        Assert.Equal(0, harness.Vault.Reveals);
    }

    // REQ-4.3 — the customer may be on the hosted page right now. A session still inside its TTL is refused,
    // and must be left exactly as it was.
    [Fact]
    public async Task A_live_session_refuses_the_release()
    {
        var session = NewSession(withCharge: true);
        var harness = NewHarness(session, now: Created.AddHours(1));

        await Assert.ThrowsAsync<ConflictException>(
            () => harness.Handler.Handle(new ReleaseOpenSessionCommand(OrderId), default).AsTask());

        Assert.Equal(SessionStatus.Redirected, session.Status);
        Assert.Equal(0, harness.UnitOfWork.SaveCount);
    }

    // REQ-4.2 — with a charge id the PSP is asked FIRST; only its "nothing was collected" plus the TTL earns
    // the expiry.
    [Fact]
    public async Task A_stale_session_the_PSP_has_not_settled_is_expired_after_the_inquiry()
    {
        var session = NewSession(withCharge: true);
        var harness = NewHarness(session, now: Created + Session.OpenTtl, fetchedStatus: PspChargeStatus.Pending);

        await harness.Handler.Handle(new ReleaseOpenSessionCommand(OrderId), default);

        Assert.Equal(SessionStatus.Expired, session.Status);
        Assert.Equal(1, harness.Vault.Reveals);
    }

    // A charge the PSP refused frees the order immediately — Failed is terminal without money.
    [Fact]
    public async Task A_session_the_PSP_refused_is_released()
    {
        var session = NewSession(withCharge: true);
        var harness = NewHarness(session, fetchedStatus: PspChargeStatus.Failed);

        await harness.Handler.Handle(new ReleaseOpenSessionCommand(OrderId), default);

        Assert.Equal(SessionStatus.Failed, session.Status);
    }

    // REQ-4.4 at the money layer — the customer paid while the merchant was cancelling. The confirmation
    // service settles the session (the order is fulfilled through PaymentPaid), and the release REFUSES:
    // cancelling a paid order is exactly the outcome this whole verify-first ordering exists to prevent.
    [Fact]
    public async Task A_session_the_PSP_has_settled_refuses_the_release_and_is_marked_paid()
    {
        var session = NewSession(withCharge: true);
        var harness = NewHarness(session, now: Created + Session.OpenTtl, fetchedStatus: PspChargeStatus.Paid);

        await Assert.ThrowsAsync<ConflictException>(
            () => harness.Handler.Handle(new ReleaseOpenSessionCommand(OrderId), default).AsTask());

        Assert.Equal(SessionStatus.Paid, session.Status);
        Assert.IsType<PaymentPaid>(Assert.Single(harness.Outbox.Enqueued));
    }

    // The PSP could not be reached: ambiguous, never "not paid". The session stays chargeable and the caller
    // gets a 409 to retry later, instead of a cancel granted on an unanswered question.
    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("garbage")]
    [InlineData("ambiguous")] // the adapter's own classification: unverifiable/unreadable inquiry, persistent 5xx
    public async Task An_inquiry_that_fails_refuses_the_release(string failure)
    {
        var session = NewSession(withCharge: true);
        var harness = NewHarness(session, now: Created + Session.OpenTtl, onFetchCharge: _ => throw (failure switch
        {
            "http" => new HttpRequestException("2c2p unreachable"),
            "timeout" => new TaskCanceledException("inquiry timed out"),
            "ambiguous" => new PspAmbiguousException("2c2p paymentInquiry response failed signature verification."),
            _ => (Exception)new System.Text.Json.JsonException("unreadable inquiry response"),
        }));

        var conflict = await Assert.ThrowsAsync<ConflictException>(
            () => harness.Handler.Handle(new ReleaseOpenSessionCommand(OrderId), default).AsTask());

        Assert.NotNull(conflict.InnerException);
        Assert.Equal(SessionStatus.Redirected, session.Status);
        Assert.Equal(0, harness.UnitOfWork.SaveCount);
    }
}
