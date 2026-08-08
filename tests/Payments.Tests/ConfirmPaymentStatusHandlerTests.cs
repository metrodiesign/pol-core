using System.Text.Json;
using BuildingBlocks.Application;
using Contracts;
using Payments.Application.Confirmation;
using Payments.Application.ConfirmPaymentStatus;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Tests;

/// <summary>
/// The customer's status check (purchase-flow-completion REQ-8.5-8.7, 8.12, 8.13). Two properties carry the
/// whole surface, so nearly every test asserts BOTH: the answer the customer is given, and whether the PSP
/// was asked at all — a terminal order that still pays for a vault reveal and an inquiry on every poll is
/// the failure REQ-8.12's short-circuit exists to prevent, and it is invisible in the returned status.
///
/// The confirmation service is the REAL one throughout: the whole point of this command is that a status
/// check and a webhook decide identically, which a faked confirm line would stop proving.
/// </summary>
public sealed class ConfirmPaymentStatusHandlerTests
{
    private static readonly Guid MerchantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Money OrderAmount = Money.Of(15000m, "THB");
    private static readonly DateTime Created = new(2026, 7, 26, 9, 0, 0, DateTimeKind.Utc);

    private const string ChargeId = "INV-STATUS-1";

    private sealed record Harness(
        ConfirmPaymentStatusHandler Handler,
        FakeOutbox Outbox,
        FakeVaultSecretStore Vault,
        FakeUnitOfWork UnitOfWork,
        RecordingLogger<PaymentConfirmationService> Logger);

    private static Session NewSession(bool withCharge = true, DateTime? createdAt = null)
    {
        var session = Session.Create(
            MerchantId, OrderId, OrderAmount, PaymentMethods.Card, Code.TwoCTwoP, createdAt ?? Created);
        session.BeginRedirect(createdAt ?? Created);
        if (withCharge)
            session.SetPspCharge(ChargeId, "https://2c2p.test/hosted/pay", createdAt ?? Created);

        return session;
    }

    private static Harness NewHarness(
        PayableOrderStatus orderStatus = PayableOrderStatus.Pending,
        Session? session = null,
        DateTime? now = null,
        PspChargeStatus fetchedStatus = PspChargeStatus.Paid,
        Money? confirmedAmount = null,
        Func<string, PspChargeConfirmation>? onFetchCharge = null,
        bool orderExists = true)
    {
        var outbox = new FakeOutbox();
        var unitOfWork = new FakeUnitOfWork();
        var vault = new FakeVaultSecretStore();
        var logger = new RecordingLogger<PaymentConfirmationService>();

        var confirmation = new PaymentConfirmationService(
            new FakeConnectionRepository(
                Connection.Create(MerchantId, Code.TwoCTwoP, PaymentMethods.Card, "psp/secret-ref", Created)),
            new FakePspAdapterFactory(new FakePspAdapter(Code.TwoCTwoP, PaymentMethods.Card)
            {
                OnFetchCharge = onFetchCharge
                    ?? (_ => new PspChargeConfirmation(fetchedStatus, confirmedAmount ?? OrderAmount)),
            }),
            vault,
            new FakeIdempotencyStore(),
            outbox,
            unitOfWork,
            new FixedClock { UtcNow = now ?? Created },
            logger);

        var handler = new ConfirmPaymentStatusHandler(
            new FakePayableOrderReader(orderExists ? new PayableOrder(OrderId, OrderAmount, orderStatus) : null),
            session is null ? new FakeSessionRepository() : new FakeSessionRepository(session),
            confirmation);

        return new Harness(handler, outbox, vault, unitOfWork, logger);
    }

    private static ValueTask<PaymentStatusResult> Ask(Harness harness) =>
        harness.Handler.Handle(new ConfirmPaymentStatusCommand(OrderId), default);

    // --- REQ-8.12: answered from the order, with no session read and no PSP call ---

    [Fact]
    public async Task A_paid_order_answers_paid_without_asking_the_PSP()
    {
        // The session is live and would fetch as Paid anyway — proving the answer came from the ORDER means
        // proving the vault was never touched.
        var harness = NewHarness(PayableOrderStatus.Paid, NewSession());

        Assert.Equal(PaymentStatusResult.Paid, await Ask(harness));
        Assert.Equal(0, harness.Vault.Reveals);
        Assert.Empty(harness.Outbox.Enqueued);
    }

    [Fact]
    public async Task A_cancelled_order_answers_cancelled_without_asking_the_PSP()
    {
        var harness = NewHarness(PayableOrderStatus.Cancelled, NewSession());

        Assert.Equal(PaymentStatusResult.Cancelled, await Ask(harness));
        Assert.Equal(0, harness.Vault.Reveals);
    }

    [Fact]
    public async Task An_order_with_no_payment_session_at_all_is_pending()
    {
        var harness = NewHarness();

        Assert.Equal(PaymentStatusResult.Pending, await Ask(harness));
        Assert.Equal(0, harness.Vault.Reveals);
    }

    [Fact]
    public async Task An_order_the_customer_cannot_reach_is_reported_as_missing()
    {
        var harness = NewHarness(orderExists: false);

        await Assert.ThrowsAsync<NotFoundException>(async () => await Ask(harness));
    }

    // --- REQ-8.5/8.6: the open session is verified with the PSP, on the webhook's own confirm line ---

    [Fact]
    public async Task A_settled_charge_marks_the_session_paid_and_publishes_PaymentPaid()
    {
        var session = NewSession();
        var harness = NewHarness(session: session);

        Assert.Equal(PaymentStatusResult.Paid, await Ask(harness));
        Assert.Equal(SessionStatus.Paid, session.Status);
        Assert.Single(harness.Outbox.Enqueued.OfType<PaymentPaid>());
    }

    // REQ-8.7 — the amount is the one thing that must never be waved through. Not paid, not failed: pending,
    // plus a Critical for ops. Telling the customer "paid" here would confirm money we cannot account for.
    [Fact]
    public async Task A_charge_collected_for_the_wrong_amount_is_pending_and_logged_critical()
    {
        var session = NewSession();
        var harness = NewHarness(session: session, confirmedAmount: Money.Of(1m, "THB"));

        Assert.Equal(PaymentStatusResult.Pending, await Ask(harness));
        Assert.Equal(SessionStatus.Redirected, session.Status);
        Assert.Empty(harness.Outbox.Enqueued);
        Assert.Single(harness.Logger.Critical);
    }

    [Fact]
    public async Task A_charge_the_PSP_refused_fails_the_session_and_answers_failed()
    {
        var session = NewSession();
        var harness = NewHarness(session: session, fetchedStatus: PspChargeStatus.Failed);

        Assert.Equal(PaymentStatusResult.Failed, await Ask(harness));
        Assert.Equal(SessionStatus.Failed, session.Status);
    }

    [Fact]
    public async Task A_charge_the_PSP_has_not_settled_yet_is_pending_and_changes_nothing()
    {
        var session = NewSession();
        var harness = NewHarness(session: session, fetchedStatus: PspChargeStatus.Pending);

        Assert.Equal(PaymentStatusResult.Pending, await Ask(harness));
        Assert.Equal(SessionStatus.Redirected, session.Status);
        Assert.Equal(0, harness.UnitOfWork.SaveCount);
    }

    // --- REQ-8.13: age alone never decides a session that carries a charge ---

    [Fact]
    public async Task A_session_past_its_TTL_with_no_charge_expires_without_asking_the_PSP()
    {
        var session = NewSession(withCharge: false);
        var harness = NewHarness(session: session, now: Created + Session.OpenTtl + TimeSpan.FromMinutes(1));

        Assert.Equal(PaymentStatusResult.Failed, await Ask(harness));
        Assert.Equal(SessionStatus.Expired, session.Status);
        Assert.Equal(0, harness.Vault.Reveals);
    }

    [Fact]
    public async Task A_session_past_its_TTL_that_the_customer_actually_paid_is_confirmed_not_expired()
    {
        var session = NewSession();
        var harness = NewHarness(session: session, now: Created + Session.OpenTtl + TimeSpan.FromMinutes(1));

        Assert.Equal(PaymentStatusResult.Paid, await Ask(harness));
        Assert.Equal(SessionStatus.Paid, session.Status);
        Assert.Single(harness.Outbox.Enqueued.OfType<PaymentPaid>());
    }

    [Fact]
    public async Task A_session_inside_its_TTL_with_no_charge_is_pending_without_asking_the_PSP()
    {
        var session = NewSession(withCharge: false);
        var harness = NewHarness(session: session);

        Assert.Equal(PaymentStatusResult.Pending, await Ask(harness));
        Assert.Equal(SessionStatus.Redirected, session.Status);
        Assert.Equal(0, harness.Vault.Reveals);
    }

    // --- an inquiry we could not complete is not an answer ---

    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("garbage")]
    [InlineData("ambiguous")] // the adapter's own classification: unverifiable/unreadable inquiry, persistent 5xx
    public async Task An_unreachable_PSP_is_pending_and_leaves_the_session_alone(string failure)
    {
        var session = NewSession();
        var harness = NewHarness(
            session: session,
            now: Created + Session.OpenTtl + TimeSpan.FromMinutes(1),
            onFetchCharge: _ => throw (failure switch
            {
                "http" => new HttpRequestException("2c2p unreachable"),
                "timeout" => new TaskCanceledException("inquiry timed out"),
                "ambiguous" => new PspAmbiguousException("2c2p paymentInquiry response failed signature verification."),
                _ => (Exception)new JsonException("unreadable inquiry response"),
            }));

        // Past its TTL, so the tempting reading of a failed inquiry is "nobody paid, expire it" — which is
        // exactly how a paid customer's session gets retired and their order left unpayable.
        Assert.Equal(PaymentStatusResult.Pending, await Ask(harness));
        Assert.Equal(SessionStatus.Redirected, session.Status);
        Assert.Equal(0, harness.UnitOfWork.SaveCount);
    }
}
