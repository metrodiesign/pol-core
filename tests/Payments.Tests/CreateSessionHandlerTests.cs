using BuildingBlocks.Application;
using Payments.Application.Confirmation;
using Payments.Application.CreateSession;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Tests;

/// <summary>
/// The create-session decision sequence (captive-payment-alignment REQ-1/2/3/6). Two properties matter most
/// and both are asserted directly: the session's amount can only ever be the ORDER's amount (the platform is
/// a channel, it never mints a charge), and every refusal happens BEFORE a session row exists — a rejected
/// request must leave nothing behind for a later attempt to trip over.
/// </summary>
public sealed class CreateSessionHandlerTests
{
    private static readonly Guid MerchantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Money OrderAmount = Money.Of(15000m, "THB");
    private static readonly DateTime Now = new(2026, 7, 26, 9, 0, 0, DateTimeKind.Utc);

    private static PayableOrder AwaitingOrder() => new(OrderId, OrderAmount, true);

    private static Connection NewConnection(string enabledMethods = "card,promptpay", Code psp = Code.TwoCTwoP) =>
        Connection.Create(MerchantId, psp, enabledMethods, "psp/secret-ref/merchant-1", Now);

    /// <summary>Reproduces a connection an admin turned off the only way that state exists in production (EF
    /// materialising such a row) — see ConnectionEligibilityTests; Connection has no Disable().</summary>
    private static Connection Disabled(Connection connection)
    {
        typeof(Connection).GetProperty(nameof(Connection.IsEnabled))!.SetValue(connection, false);
        return connection;
    }

    private sealed record Harness(
        CreateSessionHandler Handler,
        FakePayableOrderReader Orders,
        FakeSessionRepository Sessions,
        FakeUnitOfWork UnitOfWork,
        FakeOutbox Outbox)
    {
        /// <summary>The save count observed AT the moment the new row was added — the expire must already
        /// have been committed by then, which the end state alone cannot show.</summary>
        public int? SaveCountWhenMinted { get; set; }
    }

    /// <summary>Default world: the order is awaiting payment, the merchant has a 2C2P connection enabling
    /// card+promptpay, and both adapters honour card only (today's real capability).</summary>
    private static Harness NewHarness(
        PayableOrder? order = null,
        Connection[]? connections = null,
        string[]? adapterMethods = null,
        Session[]? existingSessions = null,
        DateTime? now = null,
        Func<string, PspChargeConfirmation>? onFetchCharge = null)
    {
        var orders = new FakePayableOrderReader(order ?? AwaitingOrder());
        var methods = adapterMethods ?? [PaymentMethods.Card];
        var adapters = new FakePspAdapterFactory(
            new FakePspAdapter(Code.TwoCTwoP, methods) { OnFetchCharge = onFetchCharge },
            new FakePspAdapter(Code.Omise, methods) { OnFetchCharge = onFetchCharge });
        var unitOfWork = new FakeUnitOfWork();
        var outbox = new FakeOutbox();
        var connectionRepository = new FakeConnectionRepository(connections ?? [NewConnection()]);
        var clock = new FixedClock { UtcNow = now ?? Now };

        Harness? harness = null;
        var sessions = new FakeSessionRepository(existingSessions ?? [])
        {
            OnAdd = _ => harness!.SaveCountWhenMinted ??= unitOfWork.SaveCount,
        };

        var handler = new CreateSessionHandler(
            orders,
            connectionRepository,
            adapters,
            sessions,
            new PaymentConfirmationService(
                connectionRepository,
                adapters,
                new FakeVaultSecretStore(),
                new FakeIdempotencyStore(),
                outbox,
                unitOfWork,
                clock,
                new RecordingLogger<PaymentConfirmationService>()),
            unitOfWork,
            clock);

        return harness = new Harness(handler, orders, sessions, unitOfWork, outbox);
    }

    private static CreateSessionCommand Command(string method = PaymentMethods.Card, Code psp = Code.TwoCTwoP) =>
        new(OrderId, MerchantId, method, psp);

    private static void AssertNothingWasPersisted(Harness harness)
    {
        Assert.Empty(harness.Sessions.Added);
        Assert.Equal(0, harness.UnitOfWork.SaveCount);
    }

    // --- step 1: method vocabulary (400) ---

    [Theory]
    [InlineData("paypal")]
    [InlineData("CC")]
    [InlineData("")]
    public async Task A_method_outside_the_vocabulary_is_bad_input_and_the_order_is_never_read(string method)
    {
        var harness = NewHarness();

        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await harness.Handler.Handle(Command(method), default));

        // Pins the ORDER of the checks: a malformed method is refused before any read happens.
        Assert.Equal(0, harness.Orders.Calls);
        AssertNothingWasPersisted(harness);
    }

    // --- step 2: unknown order (404, no existence leak) ---

    [Fact]
    public async Task An_order_the_merchant_cannot_see_is_reported_as_missing()
    {
        // The reader returns null both for a missing order and for another company's order (its query filter
        // makes them indistinguishable) — so this is the 404 path for both, with no "exists elsewhere" hint.
        var harness = NewHarness(order: new PayableOrder(Guid.NewGuid(), OrderAmount, true));

        await Assert.ThrowsAsync<NotFoundException>(async () =>
            await harness.Handler.Handle(Command(), default));

        AssertNothingWasPersisted(harness);
    }

    // --- step 3: order state (409) ---

    [Fact]
    public async Task An_order_that_is_not_awaiting_payment_cannot_open_a_session()
    {
        // Covers the already-paid order too: it is no longer AwaitingPayment, so this is the path that stops
        // a second charge against a settled order.
        var harness = NewHarness(order: new PayableOrder(OrderId, OrderAmount, false));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await harness.Handler.Handle(Command(), default));

        AssertNothingWasPersisted(harness);
    }

    // --- steps 4-5: connection existence + eligibility (409) ---

    [Fact]
    public async Task A_missing_connection_is_refused_here_not_at_redirect_time()
    {
        var harness = NewHarness(connections: []);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await harness.Handler.Handle(Command(), default));

        AssertNothingWasPersisted(harness);
    }

    [Fact]
    public async Task A_disabled_connection_is_refused()
    {
        var harness = NewHarness(connections: [Disabled(NewConnection())]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await harness.Handler.Handle(Command(), default));

        Assert.Contains("disabled", ex.Message, StringComparison.Ordinal);
        AssertNothingWasPersisted(harness);
    }

    [Fact]
    public async Task A_method_the_connection_does_not_enable_is_refused()
    {
        var harness = NewHarness(connections: [NewConnection(enabledMethods: PaymentMethods.PromptPay)]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await harness.Handler.Handle(Command(PaymentMethods.Card), default));

        AssertNothingWasPersisted(harness);
    }

    // --- step 6: adapter capability (409) ---

    [Fact]
    public async Task A_method_the_adapter_cannot_honour_is_refused_even_when_the_connection_enables_it()
    {
        // The real seed enables promptpay on 2C2P while the 2C2P adapter can only drive card. Without this
        // step the customer would be redirected to a CARD page after choosing PromptPay.
        var harness = NewHarness(
            connections: [NewConnection(enabledMethods: "card,promptpay")],
            adapterMethods: [PaymentMethods.Card]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await harness.Handler.Handle(Command(PaymentMethods.PromptPay), default));

        AssertNothingWasPersisted(harness);
    }

    // --- step 7: one chargeable session per order ---

    [Fact]
    public async Task An_open_session_on_the_same_channel_is_returned_instead_of_a_second_one()
    {
        var open = Session.Create(MerchantId, OrderId, OrderAmount, PaymentMethods.Card, Code.TwoCTwoP, Now);
        var harness = NewHarness(existingSessions: [open]);

        var result = await harness.Handler.Handle(Command(), default);

        Assert.Equal(open.Id, result.PaymentSessionId);
        AssertNothingWasPersisted(harness); // no new row, and nothing to commit
    }

    [Fact]
    public async Task A_session_already_redirected_still_counts_as_open()
    {
        // The customer who abandoned the PSP page must be able to resume on the SAME hosted charge, which is
        // what keeps the one-open-session rule from bricking the order.
        var open = Session.Create(MerchantId, OrderId, OrderAmount, PaymentMethods.Card, Code.TwoCTwoP, Now);
        open.BeginRedirect(Now);
        var harness = NewHarness(existingSessions: [open]);

        var result = await harness.Handler.Handle(Command(), default);

        Assert.Equal(open.Id, result.PaymentSessionId);
        AssertNothingWasPersisted(harness);
    }

    [Fact]
    public async Task An_open_session_on_a_different_method_blocks_a_new_one()
    {
        var open = Session.Create(MerchantId, OrderId, OrderAmount, PaymentMethods.PromptPay, Code.TwoCTwoP, Now);
        var harness = NewHarness(
            connections: [NewConnection(enabledMethods: "card,promptpay")],
            adapterMethods: [PaymentMethods.Card, PaymentMethods.PromptPay],
            existingSessions: [open]);

        await Assert.ThrowsAsync<ConflictException>(async () =>
            await harness.Handler.Handle(Command(PaymentMethods.Card), default));

        AssertNothingWasPersisted(harness);
    }

    [Fact]
    public async Task An_open_session_on_a_different_psp_blocks_a_new_one()
    {
        var open = Session.Create(MerchantId, OrderId, OrderAmount, PaymentMethods.Card, Code.TwoCTwoP, Now);
        var harness = NewHarness(
            connections: [NewConnection(psp: Code.Omise)],
            existingSessions: [open]);

        await Assert.ThrowsAsync<ConflictException>(async () =>
            await harness.Handler.Handle(Command(psp: Code.Omise), default));

        AssertNothingWasPersisted(harness);
    }

    [Fact]
    public async Task A_terminal_session_does_not_block_a_fresh_attempt()
    {
        var failed = Session.Create(MerchantId, OrderId, OrderAmount, PaymentMethods.Card, Code.TwoCTwoP, Now);
        failed.MarkFailed("declined", Now);
        var harness = NewHarness(existingSessions: [failed]);

        var result = await harness.Handler.Handle(Command(), default);

        var created = Assert.Single(harness.Sessions.Added);
        Assert.Equal(created.Id, result.PaymentSessionId);
        Assert.NotEqual(failed.Id, result.PaymentSessionId);
    }

    // --- step 7b: an aged-out session is released here, and only on proof it holds no money (REQ-3.1-3.3) ---

    /// <summary>An open session created far enough in the past to be stale at <see cref="Now"/>.</summary>
    private static Session StaleSession(bool withCharge)
    {
        var createdAt = Now - Session.OpenTtl;
        var session = Session.Create(MerchantId, OrderId, OrderAmount, PaymentMethods.Card, Code.TwoCTwoP, createdAt);
        if (!withCharge)
            return session;

        session.BeginRedirect(createdAt);
        session.SetPspCharge("INV-STALE", "https://2c2p.test/hosted/pay", createdAt);
        return session;
    }

    [Fact]
    public async Task An_expired_session_that_never_got_a_charge_is_retired_and_replaced()
    {
        var stale = StaleSession(withCharge: false);
        var harness = NewHarness(existingSessions: [stale]);

        var result = await harness.Handler.Handle(Command(), default);

        Assert.Equal(SessionStatus.Expired, stale.Status);
        var created = Assert.Single(harness.Sessions.Added);
        Assert.Equal(created.Id, result.PaymentSessionId);
        Assert.NotEqual(stale.Id, result.PaymentSessionId);

        // TWO saves, in this order: the UPDATE that frees the filtered unique index commits before the INSERT
        // that needs it free. One batched save would leave the ordering to EF's ModificationCommandComparer.
        Assert.Equal(2, harness.UnitOfWork.SaveCount);
        Assert.Equal(1, harness.SaveCountWhenMinted);
    }

    [Fact]
    public async Task An_expired_session_whose_charge_never_settled_is_verified_first_then_replaced()
    {
        var stale = StaleSession(withCharge: true);
        var harness = NewHarness(
            existingSessions: [stale],
            onFetchCharge: _ => new PspChargeConfirmation(PspChargeStatus.Pending, null));

        var result = await harness.Handler.Handle(Command(), default);

        Assert.Equal(SessionStatus.Expired, stale.Status);
        Assert.Equal(result.PaymentSessionId, Assert.Single(harness.Sessions.Added).Id);
        Assert.Equal(1, harness.SaveCountWhenMinted);
    }

    [Fact]
    public async Task An_expired_session_the_customer_paid_blocks_a_replacement_instead_of_being_expired()
    {
        // The money-path case: the hosted page outlived nothing, the customer paid on the last minute, and
        // this request would otherwise mint a SECOND chargeable session for an order already settled.
        var stale = StaleSession(withCharge: true);
        var harness = NewHarness(
            existingSessions: [stale],
            onFetchCharge: _ => new PspChargeConfirmation(PspChargeStatus.Paid, OrderAmount));

        await Assert.ThrowsAsync<ConflictException>(async () =>
            await harness.Handler.Handle(Command(), default));

        Assert.Equal(SessionStatus.Paid, stale.Status);
        Assert.Empty(harness.Sessions.Added);
        Assert.Single(harness.Outbox.Enqueued); // the payment still gets published — it really happened
    }

    [Fact]
    public async Task An_expired_session_the_PSP_cannot_be_asked_about_blocks_a_replacement()
    {
        // Ambiguous fetch: 2C2P may be holding a charge for this session. Minting a replacement would open a
        // second chargeable attempt against the same order.
        var stale = StaleSession(withCharge: true);
        var harness = NewHarness(
            existingSessions: [stale],
            onFetchCharge: _ => throw new HttpRequestException("2c2p timed out"));

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await harness.Handler.Handle(Command(), default));

        Assert.Equal(SessionStatus.Redirected, stale.Status);
        AssertNothingWasPersisted(harness);
    }

    [Fact]
    public async Task An_expired_session_on_the_same_channel_is_replaced_rather_than_resumed()
    {
        // Resume (the same-channel idempotent return) must not win over expiry: handing back a session whose
        // hosted page died 24h ago sends the customer to a dead link with no way to get a live one.
        var stale = StaleSession(withCharge: false);
        var harness = NewHarness(existingSessions: [stale]);

        var result = await harness.Handler.Handle(Command(), default);

        Assert.NotEqual(stale.Id, result.PaymentSessionId);
    }

    [Fact]
    public async Task A_session_one_minute_short_of_the_TTL_is_still_open_and_is_resumed()
    {
        // REQ-3.3: the one-open-session rule is unchanged for everything inside the TTL — the boundary is the
        // only thing this task moved, so it is pinned from both sides.
        var open = Session.Create(MerchantId, OrderId, OrderAmount, PaymentMethods.Card, Code.TwoCTwoP, Now - Session.OpenTtl + TimeSpan.FromMinutes(1));
        var harness = NewHarness(existingSessions: [open]);

        var result = await harness.Handler.Handle(Command(), default);

        Assert.Equal(open.Id, result.PaymentSessionId);
        AssertNothingWasPersisted(harness);
    }

    // --- step 8: the amount comes from the order, and only from the order ---

    [Fact]
    public async Task The_created_session_is_priced_from_the_order_amount_and_currency()
    {
        var harness = NewHarness();

        var result = await harness.Handler.Handle(Command(), default);

        var session = Assert.Single(harness.Sessions.Added);
        Assert.Equal(result.PaymentSessionId, session.Id);
        Assert.Equal(OrderAmount, session.Amount);
        Assert.Equal(OrderAmount.Amount, session.Amount.Amount);
        Assert.Equal(OrderAmount.Currency, session.Amount.Currency);
        Assert.Equal(OrderId, session.OrderId);
        Assert.Equal(MerchantId, session.MerchantId);
        Assert.Equal(SessionStatus.Created, session.Status);
        Assert.Equal(1, harness.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task The_session_stores_the_canonical_method_code_not_the_caller_spelling()
    {
        var harness = NewHarness();

        await harness.Handler.Handle(Command(" CARD "), default);

        Assert.Equal(PaymentMethods.Card, Assert.Single(harness.Sessions.Added).Method);
    }
}
