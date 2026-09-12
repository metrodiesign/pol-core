using Contracts;
using Payments.Application.Confirmation;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Tests;

/// <summary>
/// The shared confirm line (purchase-flow-completion REQ-3). Every branch is money-critical, so each test
/// asserts the WHOLE effect — outcome, session status, what was published, whether anything was saved, and
/// whether the PSP was called at all — never the outcome alone: a service that returned the right enum after
/// marking the wrong thing would pass an outcome-only assertion.
///
/// The rule that shapes most of these: a session may only be expired when it is PROVABLY chargeless. A
/// Created session without a charge is decidable offline; a Redirected session without one may still be an
/// in-flight PSP claim, and a session with one is asked first so its answer wins over the clock.
/// </summary>
[Trait("Capability", "MerchantConfiguration")]
[Trait("Requirement", "REQ-5.7")]
[Trait("Requirement", "REQ-5.8")]
public sealed class PaymentConfirmationServiceTests
{
    private static readonly Guid MerchantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Money SessionAmount = Money.Of(250.09m, "THB");
    private static readonly DateTime Created = new(2026, 7, 26, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Connection DefaultConnection =
        Connection.Create(MerchantId, Code.TwoCTwoP, PaymentMethods.Card, "psp/secret-ref", Created);

    private const string ChargeId = "INV-ABC";

    private sealed record Harness(
        PaymentConfirmationService Service,
        Session Session,
        FakeOutbox Outbox,
        FakeUnitOfWork UnitOfWork,
        FakeIdempotencyStore Idempotency,
        FakeVaultSecretStore Vault,
        RecordingLogger<PaymentConfirmationService> Logger);

    /// <summary>A session that has redirected and carries the PSP's charge id, unless
    /// <paramref name="withCharge"/> says the redirect never produced one.</summary>
    private static Session NewSession(bool withCharge = true, Guid? connectionId = null)
    {
        var session = Session.Create(MerchantId, OrderId, SessionAmount, PaymentMethods.Card, Code.TwoCTwoP,
            connectionId ?? DefaultConnection.Id, Guid.NewGuid(), PspEnvironment.Sandbox, Created);
        session.BeginRedirect(Created);
        if (withCharge)
            session.SetPspCharge(ChargeId, "https://2c2p.test/hosted/pay", Created);

        return session;
    }

    /// <summary>Default world: the clock sits at creation time (nothing is stale) and the PSP confirms Paid
    /// for the session's own amount. <paramref name="onFetchCharge"/> replaces the whole fetch, including
    /// throwing to stand in for an ambiguous PSP.</summary>
    private static Harness NewHarness(
        Session? session = null,
        DateTime? now = null,
        PspChargeStatus fetchedStatus = PspChargeStatus.Paid,
        Money? confirmedAmount = null,
        Func<string, PspChargeConfirmation>? onFetchCharge = null,
        Connection? connection = null,
        Func<string, string, PspEnvironment, PspChargeConfirmation>? onFetchChargeWithContext = null,
        Action<bool>? onFetchTransaction = null)
    {
        connection ??= DefaultConnection;
        session ??= NewSession(connectionId: connection.Id);

        var outbox = new FakeOutbox();
        var unitOfWork = new FakeUnitOfWork();
        var idempotency = new FakeIdempotencyStore();
        var sessions = new FakeSessionRepository(session);
        var vault = new FakeVaultSecretStore();
        var logger = new RecordingLogger<PaymentConfirmationService>();

        var service = new PaymentConfirmationService(
            new FakeConnectionRepository(connection),
            new FakePspAdapterFactory(new FakePspAdapter(Code.TwoCTwoP, PaymentMethods.Card)
            {
                OnFetchCharge = onFetchCharge
                    ?? (_ => new PspChargeConfirmation(fetchedStatus, confirmedAmount ?? SessionAmount)),
                OnFetchChargeWithContext = onFetchChargeWithContext is null
                    ? null
                    : (charge, secret, environment) =>
                    {
                        onFetchTransaction?.Invoke(unitOfWork.IsInTransaction);
                        return onFetchChargeWithContext(charge, secret, environment);
                    },
            }),
            vault,
            idempotency,
            outbox,
            unitOfWork,
            sessions,
            new FixedClock { UtcNow = now ?? Created },
            logger);

        return new Harness(service, session, outbox, unitOfWork, idempotency, vault, logger);
    }

    [Fact]
    public async Task Inquiry_uses_the_session_pinned_credential_and_environment_after_rotation_and_emergency_disable()
    {
        var connection = Connection.Create(MerchantId, Code.TwoCTwoP, PaymentMethods.Card, "psp/secret-ref", Created);
        var activeVersion = Guid.NewGuid();
        var candidateVersion = Guid.NewGuid();
        connection.SetInitialSecretVersion(activeVersion, PspEnvironment.Sandbox);
        connection.StageSecretVersion(candidateVersion, Guid.NewGuid(), PspEnvironment.Live);
        connection.ActivatePendingSecretVersion();
        connection.EmergencyDisable();

        var pinnedVersion = Guid.NewGuid();
        var session = Session.Create(
            MerchantId, OrderId, SessionAmount, PaymentMethods.Card, Code.TwoCTwoP,
            connection.Id, pinnedVersion, PspEnvironment.Sandbox, Created);
        session.BeginRedirect(Created);
        session.SetPspCharge(ChargeId, "https://2c2p.test/hosted/pay", Created);

        var harness = NewHarness(
            session: session,
            connection: connection,
            onFetchChargeWithContext: (_, secret, environment) =>
            {
                Assert.Equal("pinned-secret", secret);
                Assert.Equal(PspEnvironment.Sandbox, environment);
                return new PspChargeConfirmation(PspChargeStatus.Paid, SessionAmount);
            });
        harness.Vault.VersionSecrets[pinnedVersion] = "pinned-secret";

        Assert.Equal(ConfirmationOutcome.Paid, await harness.Service.ConfirmAsync(session, default));
        Assert.Contains(pinnedVersion, harness.Vault.VersionReads);
        Assert.Equal(SessionStatus.Paid, session.Status);
    }

    /// <summary>Nothing moved: no transition, nothing published, nothing committed.</summary>
    private static void AssertUntouched(Harness harness, SessionStatus expected)
    {
        Assert.Equal(expected, harness.Session.Status);
        Assert.Empty(harness.Outbox.Enqueued);
        Assert.Equal(0, harness.UnitOfWork.SaveCount);
    }

    // --- the paid path ---

    [Fact]
    public async Task Prepared_evidence_that_no_longer_matches_the_session_fails_closed_before_claiming()
    {
        var session = NewSession(withCharge: false);
        var harness = NewHarness(session);
        var prepared = await harness.Service.PrepareAsync(session, access: null, pspEventId: null, default);

        session.SetPspCharge("charge-bound-after-prepare", "https://2c2p.test/hosted/pay", Created);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.ApplyPreparedAsync(prepared, default));

        Assert.Equal(SessionStatus.Redirected, session.Status);
        Assert.Empty(harness.Idempotency.Claims);
        Assert.Empty(harness.Outbox.Enqueued);
    }

    [Fact]
    public async Task Offline_evidence_prepared_in_Created_fails_closed_if_redirect_begins_before_apply()
    {
        var session = Session.Create(
            MerchantId, OrderId, SessionAmount, PaymentMethods.Card, Code.TwoCTwoP,
            DefaultConnection.Id, Guid.NewGuid(), PspEnvironment.Sandbox, Created);
        var harness = NewHarness(session: session, now: Created + Session.OpenTtl);
        var prepared = await harness.Service.PrepareAsync(session, access: null, pspEventId: null, default);

        session.BeginRedirect(Created);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.ApplyPreparedAsync(prepared, default));

        AssertUntouched(harness, SessionStatus.Redirected);
        Assert.Empty(harness.Idempotency.Claims);
    }

    [Fact]
    public async Task Offline_evidence_for_an_unchanged_Redirected_session_stays_pending_after_TTL()
    {
        var harness = NewHarness(
            session: NewSession(withCharge: false),
            now: Created + Session.OpenTtl);
        var prepared = await harness.Service.PrepareAsync(harness.Session, access: null, pspEventId: null, default);

        var outcome = await harness.Service.ApplyPreparedAsync(prepared, default);

        Assert.Equal(ConfirmationOutcome.Pending, outcome);
        AssertUntouched(harness, SessionStatus.Redirected);
        Assert.Empty(harness.Idempotency.Claims);
    }

    [Fact]
    public async Task Provider_fetch_runs_before_the_apply_transaction_opens()
    {
        var fetchInsideTransaction = true;
        var harness = NewHarness(
            onFetchChargeWithContext: (_, _, _) => new PspChargeConfirmation(PspChargeStatus.Paid, SessionAmount),
            onFetchTransaction: inside => fetchInsideTransaction = inside);

        Assert.Equal(ConfirmationOutcome.Paid, await harness.Service.ConfirmAsync(harness.Session, default));

        Assert.False(fetchInsideTransaction);
        Assert.Equal(1, harness.UnitOfWork.TransactionCount);
    }

    [Fact]
    public async Task A_confirmed_charge_for_the_session_amount_is_paid_and_publishes_PaymentPaid()
    {
        var harness = NewHarness();

        var outcome = await harness.Service.ConfirmAsync(harness.Session, default);

        Assert.Equal(ConfirmationOutcome.Paid, outcome);
        Assert.Equal(SessionStatus.Paid, harness.Session.Status);
        Assert.Equal(1, harness.UnitOfWork.SaveCount);

        var paid = Assert.IsType<PaymentPaid>(Assert.Single(harness.Outbox.Enqueued));
        Assert.Equal(harness.Session.Id, paid.PaymentSessionId);
        Assert.Equal(OrderId, paid.OrderId);
        Assert.Equal(MerchantId, paid.MerchantId);
        Assert.Equal(SessionAmount, paid.Amount);
        Assert.Equal(ChargeId, paid.ExternalChargeId);
    }

    [Fact]
    public async Task A_confirmation_without_an_amount_is_paid_on_status_alone()
    {
        // REQ-8.3: a null amount means the PSP reported none, never "collected zero" — failing closed on a
        // response shape we have not verified would stop confirming real payments.
        var harness = NewHarness(onFetchCharge: _ => new PspChargeConfirmation(PspChargeStatus.Paid, null));

        Assert.Equal(ConfirmationOutcome.Paid, await harness.Service.ConfirmAsync(harness.Session, default));

        Assert.Equal(SessionStatus.Paid, harness.Session.Status);
        Assert.Single(harness.Outbox.Enqueued);
    }

    [Theory]
    [InlineData(100.00, "THB")] // wrong amount
    [InlineData(250.09, "USD")] // right number, wrong currency
    public async Task An_amount_the_session_does_not_back_is_logged_Critical_and_never_marked(decimal amount, string currency)
    {
        var harness = NewHarness(confirmedAmount: Money.Of(amount, currency));

        var outcome = await harness.Service.ConfirmAsync(harness.Session, default);

        Assert.Equal(ConfirmationOutcome.AmountMismatch, outcome);
        AssertUntouched(harness, SessionStatus.Redirected);
        // The claim must survive: the delivery that DOES back the order still has to be able to confirm it.
        Assert.Empty(harness.Idempotency.Claims);
        Assert.Contains(harness.Logger.Critical, m => m.Contains("mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_amount_differing_only_in_decimal_scale_still_matches()
    {
        var harness = NewHarness(confirmedAmount: Money.Of(250.0900m, "THB"));

        Assert.Equal(ConfirmationOutcome.Paid, await harness.Service.ConfirmAsync(harness.Session, default));
    }

    // --- idempotency: the claim is shared, the EVENT rides the transition ---

    [Fact]
    public async Task A_second_confirmation_of_the_same_charge_is_a_duplicate_and_publishes_nothing_more()
    {
        var harness = NewHarness();

        Assert.Equal(ConfirmationOutcome.Paid, await harness.Service.ConfirmAsync(harness.Session, default));
        Assert.Equal(ConfirmationOutcome.Duplicate, await harness.Service.ConfirmAsync(harness.Session, default));

        Assert.Single(harness.Outbox.Enqueued);
        Assert.Equal(1, harness.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task Two_callers_holding_different_keys_still_publish_PaymentPaid_only_once()
    {
        // The webhook carries its delivery's event id as an EXTRA key; a status check carries none. Their key
        // sets therefore differ, so the claim alone cannot be the guard — the transition is. This is the race
        // that would otherwise fulfil an order twice.
        var harness = NewHarness();
        var session = harness.Session;
        session.MarkPaid(ChargeId, Created); // whoever won, won: the session is already Paid

        var outcome = await harness.Service.ConfirmAsync(session, access: null, pspEventId: "evt-99", default);

        Assert.Equal(ConfirmationOutcome.AlreadyPaid, outcome);
        Assert.Empty(harness.Outbox.Enqueued);
        Assert.Equal(0, harness.UnitOfWork.SaveCount);
    }

    // --- the PSP refused ---

    [Fact]
    public async Task A_charge_the_PSP_reports_as_failed_fails_the_session()
    {
        // Failed sits outside the one-open-session filtered index, so the order can be paid again at once
        // instead of waiting out the 24h TTL (REQ-8.5).
        var harness = NewHarness(fetchedStatus: PspChargeStatus.Failed);

        Assert.Equal(ConfirmationOutcome.Failed, await harness.Service.ConfirmAsync(harness.Session, default));

        Assert.Equal(SessionStatus.Failed, harness.Session.Status);
        Assert.Equal(1, harness.UnitOfWork.SaveCount);
        var failed = Assert.IsType<PaymentFailed>(Assert.Single(harness.Outbox.Enqueued));
        Assert.Equal(harness.Session.Id, failed.PaymentSessionId);
        Assert.Equal("psp_failed", failed.ReasonCode);
    }

    [Theory]
    [InlineData("psp failed: raw decline")]
    [InlineData("secret=value")]
    public async Task Failure_event_rejects_unbounded_or_secret_shaped_reason_text(string reason)
    {
        var harness = NewHarness();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.MarkFailedAsync(
            harness.Session, reason, Created, default));

        Assert.Equal(SessionStatus.Redirected, harness.Session.Status);
        Assert.Empty(harness.Outbox.Enqueued);
        Assert.Equal(0, harness.UnitOfWork.SaveCount);
    }

    // --- expiry: only ever on proof that no money exists ---

    [Fact]
    public async Task A_session_that_never_got_a_charge_expires_past_its_TTL_without_touching_the_PSP()
    {
        var harness = NewHarness(
            session: Session.Create(
                MerchantId, OrderId, SessionAmount, PaymentMethods.Card, Code.TwoCTwoP,
                DefaultConnection.Id, Guid.NewGuid(), PspEnvironment.Sandbox, Created),
            now: Created + Session.OpenTtl,
            // Reaching the PSP at all here is the failure: there is nothing to ask about.
            onFetchCharge: _ => throw new InvalidOperationException("must not fetch"));

        Assert.Equal(ConfirmationOutcome.Expired, await harness.Service.ConfirmAsync(harness.Session, default));

        Assert.Equal(SessionStatus.Expired, harness.Session.Status);
        Assert.Equal(1, harness.UnitOfWork.SaveCount);
        Assert.IsType<PaymentExpired>(Assert.Single(harness.Outbox.Enqueued));
        // Not even the connection/secret: a session with no charge must stay releasable after its connection
        // is gone, or the order is blocked forever.
        Assert.Equal(0, harness.Vault.Reveals);
    }

    [Fact]
    public async Task A_chargeless_session_inside_its_TTL_is_pending_and_changes_nothing()
    {
        var harness = NewHarness(
            session: NewSession(withCharge: false),
            now: Created + Session.OpenTtl - TimeSpan.FromMinutes(1),
            onFetchCharge: _ => throw new InvalidOperationException("must not fetch"));

        Assert.Equal(ConfirmationOutcome.Pending, await harness.Service.ConfirmAsync(harness.Session, default));

        AssertUntouched(harness, SessionStatus.Redirected);
    }

    [Fact]
    public async Task A_stale_session_whose_charge_never_settled_is_expired_only_after_the_PSP_says_so()
    {
        var fetches = 0;
        var harness = NewHarness(
            now: Created + Session.OpenTtl,
            onFetchCharge: _ =>
            {
                fetches++;
                return new PspChargeConfirmation(PspChargeStatus.Pending, null);
            });

        Assert.Equal(ConfirmationOutcome.Expired, await harness.Service.ConfirmAsync(harness.Session, default));

        Assert.Equal(1, fetches); // verify-first, not clock-first
        Assert.Equal(SessionStatus.Expired, harness.Session.Status);
        Assert.Equal(1, harness.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task A_stale_session_the_customer_actually_paid_is_marked_paid_not_expired()
    {
        // The whole reason expiry verifies first. Expiring this session would strand a real payment against
        // an order that can never be fulfilled.
        var harness = NewHarness(now: Created + Session.OpenTtl * 3);

        Assert.Equal(ConfirmationOutcome.Paid, await harness.Service.ConfirmAsync(harness.Session, default));

        Assert.Equal(SessionStatus.Paid, harness.Session.Status);
        Assert.Single(harness.Outbox.Enqueued);
    }

    [Fact]
    public async Task An_ambiguous_fetch_decides_nothing_and_surfaces_to_the_caller()
    {
        // Timeout/5xx/unreadable response: the PSP may be holding money we have not heard about. Reading that
        // as "not paid" is precisely what would expire a paid session, so the failure is deliberately not
        // caught — the caller answers per its context (webhook 500 -> redelivery; a request -> 409/pending).
        var harness = NewHarness(
            now: Created + Session.OpenTtl,
            onFetchCharge: _ => throw new HttpRequestException("2c2p timed out"));

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await harness.Service.ConfirmAsync(harness.Session, default));

        AssertUntouched(harness, SessionStatus.Redirected);
    }

    // --- verified late money after the session gave up (REQ-9.27) ---

    [Theory]
    [InlineData(SessionStatus.Expired)]
    [InlineData(SessionStatus.Failed)]
    public async Task A_charge_confirmed_for_a_failed_or_expired_session_becomes_Paid(SessionStatus terminal)
    {
        var session = NewSession();
        if (terminal == SessionStatus.Expired)
            session.MarkExpired(Created);
        else
            session.MarkFailed("declined", Created);

        var harness = NewHarness(session);

        var outcome = await harness.Service.ConfirmAsync(session, default);

        Assert.Equal(ConfirmationOutcome.Paid, outcome);
        Assert.Equal(SessionStatus.Paid, session.Status);
        var paid = Assert.IsType<PaymentPaid>(Assert.Single(harness.Outbox.Enqueued));
        Assert.Equal(session.Id, paid.PaymentSessionId);
        Assert.Equal(session.Method, paid.Method);
        Assert.Equal(1, harness.UnitOfWork.SaveCount);
        Assert.Empty(harness.Logger.Critical);
    }

    [Fact]
    public async Task A_terminal_session_the_PSP_did_not_settle_just_reports_what_it_is()
    {
        var session = NewSession();
        session.MarkFailed("declined", Created);
        var harness = NewHarness(session, fetchedStatus: PspChargeStatus.Pending);

        Assert.Equal(ConfirmationOutcome.Failed, await harness.Service.ConfirmAsync(session, default));

        AssertUntouched(harness, SessionStatus.Failed);
        Assert.Empty(harness.Logger.Critical);
    }
}
