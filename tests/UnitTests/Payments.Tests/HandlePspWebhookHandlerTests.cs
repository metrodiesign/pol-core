using BuildingBlocks.Application;
using Contracts;
using Payments.Application.Confirmation;
using Payments.Application.HandlePspWebhook;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Tests;

/// <summary>
/// The pinned-secret webhook ingest path (merchant-psp-settings task 8) plus the amount check it runs
/// between fetch-to-confirm and MarkPaid (captive-payment-alignment REQ-8). The session is resolved from a
/// bounded, untrusted reference BEFORE verify; the signature is checked with the version the SESSION pinned
/// (a rotation retires but keeps it readable); Omise is confirmed only by a server-side fetch that never
/// trusts the body; and an unresolved / undecidable webhook defers or parks a pending match rather than
/// false-acking.
/// </summary>
public sealed class HandlePspWebhookHandlerTests
{
    private static readonly Guid MerchantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Money SessionAmount = Money.Of(250.09m, "THB");
    private static readonly DateTime Now = new(2026, 7, 26, 9, 0, 0, DateTimeKind.Utc);

    private const string EventId = "evt-1";
    private const string RawPayload = """{"whatever":"the adapter parses"}""";

    private sealed record Harness(
        HandlePspWebhookHandler Handler,
        Guid ConnectionId,
        Session Session,
        string ChargeId,
        FakeOutbox Outbox,
        FakeUnitOfWork UnitOfWork,
        FakeIdempotencyStore Idempotency,
        FakeInboundWebhookRecorder InboundEvents,
        FakeVaultSecretStore Vault)
    {
        public async ValueTask<WebhookOutcome> Deliver(string payload = RawPayload) =>
            (await Handler.Handle(new HandlePspWebhookCommand(ConnectionId, payload, "sig"), default)).Outcome;
    }

    /// <summary>In-memory recorder that dedups by (connection, event id) exactly as the real store's unique
    /// index does — so a redelivered event leaves ONE row (adversarial #6).</summary>
    private sealed class FakeInboundWebhookRecorder : IInboundWebhookRecorder
    {
        private readonly Dictionary<(Guid ConnectionId, string ExternalEventId), InboundWebhookEvent> _events = [];
        public int RejectedCount { get; private set; }

        public int PendingCount => _events.Values.Count(x => x.Status == InboundWebhookStatus.PendingMatch);

        public Task RecordRejectedAsync(Guid connectionId, Guid merchantId, string pspCode,
            string payloadFingerprint, bool signatureValid, string failureCode, CancellationToken cancellationToken)
        {
            RejectedCount++;
            var entity = InboundWebhookEvent.Reject(
                connectionId, merchantId, pspCode, payloadFingerprint, signatureValid, failureCode, Now);
            _events.TryAdd((connectionId, entity.ExternalEventId), entity);
            return Task.CompletedTask;
        }

        public Task<InboundWebhookClaim> ClaimAsync(Guid connectionId, Guid merchantId, string pspCode,
            string externalEventId, string payloadFingerprint, WebhookVerificationMode mode, CancellationToken cancellationToken)
        {
            if (!_events.TryGetValue((connectionId, externalEventId), out var entity))
            {
                entity = InboundWebhookEvent.Receive(
                    connectionId, merchantId, pspCode, externalEventId, payloadFingerprint, mode, Now);
                _events.Add((connectionId, externalEventId), entity);
            }

            return Task.FromResult(new InboundWebhookClaim(entity.Id, entity.Status, entity.PayloadFingerprint));
        }

        public Task RecordPendingMatchAsync(Guid connectionId, Guid merchantId, string pspCode,
            string externalEventId, string externalChargeId, string payloadFingerprint, CancellationToken cancellationToken)
        {
            var key = (connectionId, externalEventId);
            if (!_events.ContainsKey(key))
                _events[key] = InboundWebhookEvent.PendingMatch(
                    connectionId, merchantId, pspCode, externalEventId, externalChargeId, payloadFingerprint, Now);
            return Task.CompletedTask;
        }

        public Task<InboundWebhookEvent> LoadAsync(Guid eventId, CancellationToken cancellationToken) =>
            Task.FromResult(_events.Values.Single(x => x.Id == eventId));

        public Task<IReadOnlyList<InboundWebhookEvent>> FindPendingMatchesAsync(
            Guid connectionId, string externalChargeId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InboundWebhookEvent>>(_events.Values
                .Where(x => x.PspConnectionId == connectionId
                    && x.ExternalChargeId == externalChargeId
                    && x.Status == InboundWebhookStatus.PendingMatch)
                .ToList());
    }

    /// <summary>Builds a webhook world. <paramref name="mode"/> picks 2C2P (signed, resolved by
    /// <c>Session.Id</c>) or Omise (fetch-confirm-only, resolved by charge id). <paramref name="bindCharge"/>
    /// controls whether the session already carries its PSP charge. The pinned secret version is registered
    /// in the vault so a test can prove it — not the connection's active version — drove the read.</summary>
    private static Harness NewHarness(
        Money? confirmedAmount = null,
        PspChargeStatus fetchedStatus = PspChargeStatus.Paid,
        Func<string, PspChargeConfirmation>? onFetchCharge = null,
        bool webhookVerifies = true,
        WebhookVerificationMode mode = WebhookVerificationMode.SignedDeterministicReference,
        bool bindCharge = true,
        Guid? referenceChargeOverride = null,
        Func<string, string, string, bool>? onVerify = null)
    {
        confirmedAmount ??= SessionAmount;
        var psp = mode == WebhookVerificationMode.SignedDeterministicReference ? Code.TwoCTwoP : Code.Omise;
        var connection = Connection.Create(MerchantId, psp, PaymentMethods.Card, "psp/secret-ref", Now);
        var pinnedVersion = Guid.NewGuid();

        var session = Session.Create(MerchantId, OrderId, SessionAmount, PaymentMethods.Card, psp,
            connection.Id, pinnedVersion, PspEnvironment.Sandbox, Now);

        // 2C2P correlates on invoiceNo = Session.Id; Omise on the charge id (chrg_...).
        var chargeId = psp == Code.TwoCTwoP ? session.Id.ToString("N") : "chrg_test_123";
        if (bindCharge)
        {
            session.BeginRedirect(Now);
            session.SetPspCharge(chargeId, "https://psp.test/hosted/pay", Now);
        }

        // For 2C2P the reference resolves the session by id; for Omise by the (possibly-not-yet-bound) charge.
        var referenceCharge = referenceChargeOverride is { } o
            ? o.ToString("N")
            : psp == Code.TwoCTwoP ? session.Id.ToString("N") : chargeId;

        var outbox = new FakeOutbox();
        var unitOfWork = new FakeUnitOfWork();
        var idempotency = new FakeIdempotencyStore();
        var connections = new FakeConnectionRepository(connection);
        var adapter = new FakePspAdapter(psp, PaymentMethods.Card)
        {
            Mode = mode,
            WebhookVerifies = webhookVerifies,
            OnVerifyWebhook = onVerify,
            Reference = new PspWebhookReference(EventId, referenceCharge),
            ParsedWebhook = new WebhookEvent(EventId, chargeId, PspChargeStatus.Paid),
            OnFetchCharge = onFetchCharge ?? (_ => new PspChargeConfirmation(fetchedStatus, confirmedAmount)),
        };
        var adapters = new FakePspAdapterFactory(adapter);
        var vault = new FakeVaultSecretStore();
        var clock = new FixedClock { UtcNow = Now };
        var inboundEvents = new FakeInboundWebhookRecorder();
        var sessions = new FakeSessionRepository(session);

        var handler = new HandlePspWebhookHandler(
            connections,
            sessions,
            adapters,
            vault,
            unitOfWork,
            new PaymentConfirmationService(
                connections, adapters, vault, idempotency, outbox, unitOfWork, sessions, clock,
                new RecordingLogger<PaymentConfirmationService>()),
            inboundEvents,
            clock);

        return new Harness(handler, connection.Id, session, chargeId, outbox, unitOfWork, idempotency, inboundEvents, vault);
    }

    private static void AssertNotPaid(Harness harness)
    {
        Assert.NotEqual(SessionStatus.Paid, harness.Session.Status);
        Assert.Empty(harness.Outbox.Enqueued);
    }

    // ---- amount check (kept from captive-payment-alignment, adapted to the pinned flow) ----

    [Fact]
    public async Task An_amount_matching_the_session_is_processed_and_publishes_PaymentPaid()
    {
        var harness = NewHarness(SessionAmount);

        Assert.Equal(WebhookOutcome.Processed, await harness.Deliver());

        Assert.Equal(SessionStatus.Paid, harness.Session.Status);
        var paid = Assert.IsType<PaymentPaid>(Assert.Single(harness.Outbox.Enqueued));
        Assert.Equal(harness.Session.Id, paid.PaymentSessionId);
        Assert.Equal(OrderId, paid.OrderId);
        Assert.Equal(SessionAmount, paid.Amount);
        Assert.Equal(harness.ChargeId, paid.ExternalChargeId);
        Assert.Equal(EventId, paid.PspEventId);
    }

    [Fact]
    public async Task An_amount_that_differs_from_the_session_is_ignored_and_publishes_nothing()
    {
        var harness = NewHarness(Money.Of(100.00m, "THB"));

        Assert.Equal(WebhookOutcome.Ignored, await harness.Deliver());

        AssertNotPaid(harness);
    }

    [Fact]
    public async Task An_amount_collected_in_a_different_currency_is_ignored()
    {
        var harness = NewHarness(Money.Of(250.09m, "USD"));

        Assert.Equal(WebhookOutcome.Ignored, await harness.Deliver());

        AssertNotPaid(harness);
    }

    [Fact]
    public async Task An_amount_differing_only_in_decimal_scale_still_matches()
    {
        var harness = NewHarness(Money.Of(250.0900m, "THB"));

        Assert.Equal(WebhookOutcome.Processed, await harness.Deliver());

        Assert.Equal(SessionStatus.Paid, harness.Session.Status);
        Assert.Single(harness.Outbox.Enqueued);
    }

    [Fact]
    public async Task A_confirmation_without_an_amount_is_processed_on_status_alone()
    {
        // A PSP whose fetch response carries no amount confirms on status alone (REQ-8.3).
        var harness = NewHarness(onFetchCharge: _ => new PspChargeConfirmation(PspChargeStatus.Paid, null));

        Assert.Equal(WebhookOutcome.Processed, await harness.Deliver());
        Assert.Equal(SessionStatus.Paid, harness.Session.Status);
        Assert.Single(harness.Outbox.Enqueued);
    }

    // ---- AC-8.1 / AC-8.2 / adversarial #1: verify with the SESSION-pinned (retired) version ----

    [Fact]
    public async Task The_signature_is_verified_with_the_version_the_session_pinned_not_the_current_one()
    {
        // The session pins a retired version; the vault hands back that version's secret. Verification must
        // run against THAT secret — a handler reading the connection's active/current version would verify
        // against the wrong one and reject a genuine webhook (adversarial #1).
        var harness = NewHarness(onVerify: (_, _, secret) => secret == "retired-secret");
        var pinned = harness.Session.SecretVersionId!.Value;
        harness.Vault.VersionSecrets[pinned] = "retired-secret";

        Assert.Equal(WebhookOutcome.Processed, await harness.Deliver());

        Assert.Equal(SessionStatus.Paid, harness.Session.Status);
        // The pinned version — and only it — drove the read.
        Assert.All(harness.Vault.VersionReads, v => Assert.Equal(pinned, v));
    }

    // ---- AC-8.2 / adversarial #2: bad signature -> 401, never confirmed ----

    [Fact]
    public async Task A_signature_that_does_not_verify_is_rejected_without_confirming()
    {
        var harness = NewHarness(webhookVerifies: false);

        Assert.Equal(WebhookOutcome.Rejected, await harness.Deliver());

        AssertNotPaid(harness);
        Assert.Equal(1, harness.InboundEvents.RejectedCount);
        Assert.Empty(harness.Idempotency.Claims);
    }

    // ---- AC-8.2 / adversarial #3: reference to another merchant / unknown session -> deferred, no ack ----

    [Fact]
    public async Task A_reference_that_resolves_to_no_session_of_ours_is_deferred_without_acking()
    {
        // A cross-merchant id is hidden by the merchant query filter (resolves to null here), the same as an
        // unknown id: a signed webhook that cannot be resolved defers for provider retry, never a false ack.
        var harness = NewHarness(referenceChargeOverride: Guid.NewGuid());

        Assert.Equal(WebhookOutcome.Deferred, await harness.Deliver());

        AssertNotPaid(harness);
        Assert.Equal(0, harness.InboundEvents.RejectedCount);
        Assert.Equal(0, harness.Vault.Reveals);
    }

    // ---- AC-8.5: unreadable pinned secret / unbound signed charge -> 503 deferred ----

    [Fact]
    public async Task An_unreadable_pinned_secret_defers_rather_than_false_acking()
    {
        var harness = NewHarness();
        harness.Vault.UnreadableVersions.Add(harness.Session.SecretVersionId!.Value);

        Assert.Equal(WebhookOutcome.Deferred, await harness.Deliver());

        AssertNotPaid(harness);
        Assert.Equal(0, harness.InboundEvents.RejectedCount);
    }

    [Fact]
    public async Task A_signed_webhook_whose_charge_is_not_bound_yet_is_deferred_after_verifying()
    {
        var harness = NewHarness(bindCharge: false);

        Assert.Equal(WebhookOutcome.Deferred, await harness.Deliver());

        AssertNotPaid(harness);
    }

    [Fact]
    public async Task A_deferral_leaves_the_event_re_claimable_so_the_retry_after_it_clears_processes()
    {
        // Class sweep for the defer paths: a 503 must write nothing that poisons the redelivery. The pinned
        // secret is unreadable, then becomes readable — the retry that follows must confirm, not answer
        // Duplicate off a claim the defer had spent.
        var harness = NewHarness();
        var pinned = harness.Session.SecretVersionId!.Value;
        harness.Vault.UnreadableVersions.Add(pinned);

        Assert.Equal(WebhookOutcome.Deferred, await harness.Deliver());

        harness.Vault.UnreadableVersions.Remove(pinned);
        Assert.Equal(WebhookOutcome.Processed, await harness.Deliver());
        Assert.Equal(SessionStatus.Paid, harness.Session.Status);
    }

    // ---- AC-8.3 / adversarial #5: Omise fetch is the authority, not the body ----

    [Fact]
    public async Task An_omise_body_claiming_paid_is_not_marked_paid_when_the_fetch_says_otherwise()
    {
        // The parsed body claims Paid, but the fetch-to-confirm returns Pending — the fetch wins and nothing
        // is marked paid (adversarial #5).
        var harness = NewHarness(
            mode: WebhookVerificationMode.FetchConfirmOnly,
            onFetchCharge: _ => new PspChargeConfirmation(PspChargeStatus.Pending, SessionAmount));

        Assert.Equal(WebhookOutcome.Ignored, await harness.Deliver());

        AssertNotPaid(harness);
    }

    // ---- AC-8.4 / adversarial #6: Omise webhook before bind -> one pending match, no ack ----

    [Fact]
    public async Task An_omise_webhook_before_its_charge_binds_is_parked_as_one_pending_match()
    {
        var harness = NewHarness(mode: WebhookVerificationMode.FetchConfirmOnly, bindCharge: false);

        Assert.Equal(WebhookOutcome.PendingMatch, await harness.Deliver());
        // Redelivered twice more before the bind — still ONE pending match (dedup by event id).
        Assert.Equal(WebhookOutcome.PendingMatch, await harness.Deliver());
        Assert.Equal(WebhookOutcome.PendingMatch, await harness.Deliver());

        Assert.Equal(1, harness.InboundEvents.PendingCount);
        AssertNotPaid(harness);
    }

    // ---- adversarial #8: an ambiguous fetch -> 503, no ack, no claim spent, no failover to the body ----

    [Fact]
    public async Task An_ambiguous_fetch_defers_without_acking_or_spending_a_claim()
    {
        var harness = NewHarness(onFetchCharge: _ => throw new PspAmbiguousException("2c2p paymentInquiry timed out."));

        Assert.Equal(WebhookOutcome.Deferred, await harness.Deliver());

        AssertNotPaid(harness);
        Assert.Empty(harness.Idempotency.Claims);
    }

    // ---- adversarial #7 / duplicate: a redelivery after a confirmed event does not transition twice ----

    [Fact]
    public async Task A_redelivery_of_an_already_processed_event_is_a_duplicate()
    {
        var harness = NewHarness(SessionAmount);

        Assert.Equal(WebhookOutcome.Processed, await harness.Deliver());
        Assert.Equal(WebhookOutcome.Duplicate, await harness.Deliver());

        Assert.Equal(SessionStatus.Paid, harness.Session.Status);
        Assert.Single(harness.Outbox.Enqueued);
    }

    [Fact]
    public async Task A_pending_fetch_is_ignored_and_leaves_the_claim_for_the_settling_delivery()
    {
        var fetched = PspChargeStatus.Pending;
        var harness = NewHarness(onFetchCharge: _ => new PspChargeConfirmation(fetched, SessionAmount));

        Assert.Equal(WebhookOutcome.Ignored, await harness.Deliver());
        Assert.Empty(harness.Idempotency.Claims);

        fetched = PspChargeStatus.Paid;
        Assert.Equal(WebhookOutcome.Processed, await harness.Deliver());
        Assert.Equal(SessionStatus.Paid, harness.Session.Status);
        Assert.Single(harness.Outbox.Enqueued);
    }
}
