using Contracts;
using Payments.Application.HandlePspWebhook;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Tests;

/// <summary>
/// The amount check the webhook ingest path runs between fetch-to-confirm and MarkPaid
/// (captive-payment-alignment REQ-8). The session is priced from its order row, so the Orders-side
/// amount check compares the session's amount to itself — this is the only place the amount the PSP
/// ACTUALLY collected is compared against anything. A mismatch must leave the session untouched and
/// publish nothing; a PSP that reports no amount at all must still be confirmed on status alone, because
/// failing closed on a response contract we have not verified against a sandbox would stop confirming
/// real payments (REQ-8.3).
/// </summary>
public sealed class HandlePspWebhookHandlerTests
{
    private static readonly Guid MerchantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Money SessionAmount = Money.Of(250.09m, "THB");
    private static readonly DateTime Now = new(2026, 7, 26, 9, 0, 0, DateTimeKind.Utc);

    private const string ChargeId = "INV-ABC";
    private const string EventId = "evt-1";
    private const string RawPayload = """{"whatever":"the adapter parses"}""";

    private sealed record Harness(
        HandlePspWebhookHandler Handler,
        Guid ConnectionId,
        Session Session,
        FakeOutbox Outbox,
        FakeUnitOfWork UnitOfWork,
        FakeIdempotencyStore Idempotency)
    {
        public async ValueTask<WebhookOutcome> Deliver() =>
            (await Handler.Handle(new HandlePspWebhookCommand(ConnectionId, RawPayload, "sig"), default)).Outcome;
    }

    /// <summary>Default world: a session that has already redirected and carries the PSP's charge id, a
    /// verified webhook claiming Paid, and a fetch that confirms Paid plus whatever
    /// <paramref name="confirmedAmount"/> says the PSP collected.</summary>
    private static Harness NewHarness(
        Money? confirmedAmount,
        PspChargeStatus fetchedStatus = PspChargeStatus.Paid,
        Func<string, PspChargeConfirmation>? onFetchCharge = null)
    {
        var connection = Connection.Create(MerchantId, Code.TwoCTwoP, PaymentMethods.Card, "psp/secret-ref", Now);

        var session = Session.Create(MerchantId, OrderId, SessionAmount, PaymentMethods.Card, Code.TwoCTwoP, Now);
        session.BeginRedirect(Now);
        session.SetPspCharge(ChargeId, "https://2c2p.test/hosted/pay", Now);

        var outbox = new FakeOutbox();
        var unitOfWork = new FakeUnitOfWork();
        var idempotency = new FakeIdempotencyStore();

        var handler = new HandlePspWebhookHandler(
            new FakeConnectionRepository(connection),
            new FakeSessionRepository(session),
            new FakePspAdapterFactory(new FakePspAdapter(Code.TwoCTwoP, PaymentMethods.Card)
            {
                WebhookVerifies = true,
                ParsedWebhook = new WebhookEvent(EventId, ChargeId, PspChargeStatus.Paid),
                OnFetchCharge = onFetchCharge ?? (_ => new PspChargeConfirmation(fetchedStatus, confirmedAmount)),
            }),
            new FakeVaultSecretStore(),
            idempotency,
            outbox,
            unitOfWork,
            new FixedClock { UtcNow = Now });

        return new Harness(handler, connection.Id, session, outbox, unitOfWork, idempotency);
    }

    /// <summary>An unconfirmed collection must not move the session, must not publish, and must not commit —
    /// asserting the outcome alone would pass on a handler that returned Ignored AFTER marking it paid.</summary>
    private static void AssertNotPaid(Harness harness)
    {
        Assert.Equal(SessionStatus.Redirected, harness.Session.Status);
        Assert.Empty(harness.Outbox.Enqueued);
        Assert.Equal(0, harness.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task An_amount_matching_the_session_is_processed_and_publishes_PaymentPaid()
    {
        var harness = NewHarness(SessionAmount);

        Assert.Equal(WebhookOutcome.Processed, await harness.Deliver());

        Assert.Equal(SessionStatus.Paid, harness.Session.Status);
        Assert.Equal(1, harness.UnitOfWork.SaveCount);
        // REQ-8.4: the event's own contract is untouched — same fields, same amount, published in the
        // same transaction as the transition.
        var paid = Assert.IsType<PaymentPaid>(Assert.Single(harness.Outbox.Enqueued));
        Assert.Equal(harness.Session.Id, paid.PaymentSessionId);
        Assert.Equal(OrderId, paid.OrderId);
        Assert.Equal(SessionAmount, paid.Amount);
        Assert.Equal(ChargeId, paid.ExternalChargeId);
        Assert.Equal(EventId, paid.EventId);
        Assert.Equal(Now, paid.OccurredAt);
    }

    [Fact]
    public async Task An_amount_that_differs_from_the_session_is_ignored_and_publishes_nothing()
    {
        // REQ-8.2: the customer was charged 100.00 for an order worth 250.09. Marking it paid would fulfil
        // an order the money does not back; the transition and the publish are both withheld.
        var harness = NewHarness(Money.Of(100.00m, "THB"));

        Assert.Equal(WebhookOutcome.Ignored, await harness.Deliver());

        AssertNotPaid(harness);
    }

    [Fact]
    public async Task An_amount_collected_in_a_different_currency_is_ignored()
    {
        // Same number, wrong currency — 250.09 USD is not 250.09 THB. The comparison covers both halves.
        var harness = NewHarness(Money.Of(250.09m, "USD"));

        Assert.Equal(WebhookOutcome.Ignored, await harness.Deliver());

        AssertNotPaid(harness);
    }

    [Fact]
    public async Task An_amount_differing_only_in_decimal_scale_still_matches()
    {
        // 250.0900 and 250.09 are the same money. A scale-sensitive comparison (string- or byte-wise) would
        // refuse a correct payment, so the value-based comparison is pinned here at the seam that decides.
        var harness = NewHarness(Money.Of(250.0900m, "THB"));

        Assert.Equal(WebhookOutcome.Processed, await harness.Deliver());

        Assert.Equal(SessionStatus.Paid, harness.Session.Status);
        Assert.Single(harness.Outbox.Enqueued);
    }

    [Fact]
    public async Task A_confirmation_without_an_amount_is_processed_on_status_alone()
    {
        // REQ-8.3: exactly the behavior that existed before the amount check — the PSP response carries no
        // amount, so status is the confirmation. Fail-closed here would halt every Omise/2C2P path whose
        // response shape is not sandbox-verified.
        var harness = NewHarness(confirmedAmount: null);

        Assert.Equal(WebhookOutcome.Processed, await harness.Deliver());

        Assert.Equal(SessionStatus.Paid, harness.Session.Status);
        Assert.Single(harness.Outbox.Enqueued);
    }

    [Fact]
    public async Task A_fetch_that_does_not_confirm_a_paid_charge_is_ignored_before_the_amount_is_read()
    {
        // Unchanged by this task, kept as its regression net: the status gate still runs first, so a Pending
        // charge never reaches the amount comparison or the session lookup.
        var harness = NewHarness(SessionAmount, PspChargeStatus.Pending);

        Assert.Equal(WebhookOutcome.Ignored, await harness.Deliver());

        AssertNotPaid(harness);
    }

    [Fact]
    public async Task A_redelivery_after_a_mismatch_is_still_Ignored_because_no_claim_was_spent()
    {
        // REQ-8.5 (reverses the behavior this test used to pin): an Ignored outcome must not consume the
        // idempotency claim. The claim is taken only in the same transaction as the transition, so a
        // notification that confirmed nothing leaves the door open for the one that will.
        var harness = NewHarness(Money.Of(100.00m, "THB"));

        Assert.Equal(WebhookOutcome.Ignored, await harness.Deliver());
        Assert.Equal(WebhookOutcome.Ignored, await harness.Deliver());

        AssertNotPaid(harness);
        Assert.Empty(harness.Idempotency.Claims);
    }

    [Fact]
    public async Task A_notification_arriving_before_the_charge_settles_does_not_block_the_real_one()
    {
        // Live-sandbox repro (2026-07-28): a "paid" notification landed while paymentInquiry still said
        // Pending. The old claim-before-fetch order burned charge:{invoice}:Paid on that Ignored, so the
        // genuine post-payment notification was refused as Duplicate forever and the session stayed
        // Redirected after the customer had paid. The claim must only be spent with the transition (REQ-8.5).
        var fetched = PspChargeStatus.Pending;
        var harness = NewHarness(SessionAmount, onFetchCharge: _ => new PspChargeConfirmation(fetched, SessionAmount));

        Assert.Equal(WebhookOutcome.Ignored, await harness.Deliver());

        fetched = PspChargeStatus.Paid;
        Assert.Equal(WebhookOutcome.Processed, await harness.Deliver());

        Assert.Equal(SessionStatus.Paid, harness.Session.Status);
        Assert.Single(harness.Outbox.Enqueued);

        // And a further redelivery of the settled event is the duplicate now.
        Assert.Equal(WebhookOutcome.Duplicate, await harness.Deliver());
    }
}
