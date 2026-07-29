using BuildingBlocks.Application;
using Payments.Application.CreateSession;
using Payments.Application.Ports;
using Payments.Application.StartRedirect;
using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Tests;

/// <summary>
/// The start-redirect decision sequence (captive-payment-alignment REQ-3.5, REQ-7). The properties asserted
/// here are liveness ones: a refused request must leave the session exactly as it found it (nothing claimed,
/// no secret revealed), and a charge the PSP refuses must leave the session Failed rather than claimed-but-
/// unusable — a session stuck at Redirected with no URL can neither redirect again nor be replaced, which
/// makes its order permanently unpayable.
/// </summary>
public sealed class StartRedirectHandlerTests
{
    private static readonly Guid MerchantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Money OrderAmount = Money.Of(15000m, "THB");
    private static readonly DateTime Now = new(2026, 7, 26, 9, 0, 0, DateTimeKind.Utc);

    private const string SecretRef = "psp/secret-ref/merchant-1";

    private static Connection NewConnection(string enabledMethods = "card,promptpay") =>
        Connection.Create(MerchantId, Code.TwoCTwoP, enabledMethods, SecretRef, Now);

    /// <summary>Reproduces a connection an admin turned off the only way that state exists in production (EF
    /// materialising such a row) — see ConnectionEligibilityTests; Connection has no Disable().</summary>
    private static Connection Disabled(Connection connection)
    {
        typeof(Connection).GetProperty(nameof(Connection.IsEnabled))!.SetValue(connection, false);
        return connection;
    }

    private static Session CreatedSession(string method = PaymentMethods.Card) =>
        Session.Create(MerchantId, OrderId, OrderAmount, method, Code.TwoCTwoP, Now);

    private sealed record Harness(
        StartRedirectHandler Handler,
        Session Session,
        FakeVaultSecretStore Vault,
        FakeUnitOfWork UnitOfWork)
    {
        public ValueTask<StartRedirectResult> Start() =>
            Handler.Handle(new StartRedirectCommand(Session.Id), default);
    }

    /// <summary>Default world: a Created session on card, a 2C2P connection enabling card+promptpay, and an
    /// adapter that refuses to charge unless the test arranged it (so an unexpected PSP call fails loudly).</summary>
    private static Harness NewHarness(
        Session? session = null,
        Connection[]? connections = null,
        Func<Session, PspCharge>? onCharge = null,
        Func<int, Exception?>? saveFails = null,
        Exception? vaultFails = null)
    {
        var target = session ?? CreatedSession();
        var vault = new FakeVaultSecretStore { RevealFails = vaultFails };
        var unitOfWork = new FakeUnitOfWork { SaveFails = saveFails };

        var handler = new StartRedirectHandler(
            new FakeSessionRepository(target),
            new FakeConnectionRepository(connections ?? [NewConnection()]),
            new FakePspAdapterFactory(
                new FakePspAdapter(Code.TwoCTwoP, PaymentMethods.Card) { OnCreateCharge = onCharge }),
            vault,
            unitOfWork,
            new FixedClock { UtcNow = Now });

        return new Harness(handler, target, vault, unitOfWork);
    }

    /// <summary>A refusal has to be free of side effects: no claim, no vault read, nothing to commit.</summary>
    private static void AssertNothingWasClaimed(Harness harness)
    {
        Assert.Equal(SessionStatus.Created, harness.Session.Status);
        Assert.Null(harness.Session.RedirectUrl);
        Assert.Equal(0, harness.Vault.Reveals);
        Assert.Equal(0, harness.UnitOfWork.SaveCount);
    }

    // --- step 1: the session itself ---

    [Fact]
    public async Task An_unknown_session_is_reported_as_missing()
    {
        var harness = NewHarness();

        await Assert.ThrowsAsync<NotFoundException>(async () =>
            await harness.Handler.Handle(new StartRedirectCommand(Guid.NewGuid()), default));

        AssertNothingWasClaimed(harness);
    }

    // --- step 2: idempotent re-entry, ahead of every recheck ---

    [Fact]
    public async Task An_already_redirected_session_returns_its_url_without_charging_again()
    {
        var session = CreatedSession();
        session.BeginRedirect(Now);
        session.SetPspCharge("chg_1", "https://psp.example/hosted/1", Now);
        var harness = NewHarness(session: session);

        var result = await harness.Start();

        Assert.Equal("https://psp.example/hosted/1", result.RedirectUrl);
        Assert.Equal(0, harness.Vault.Reveals);
        Assert.Equal(0, harness.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task A_connection_disabled_after_the_charge_exists_does_not_revoke_the_live_redirect()
    {
        // Pins that re-entry is answered BEFORE the eligibility recheck: the customer is already on the PSP's
        // page, so the URL they were given must keep working — the recheck guards new claims, not old charges.
        var session = CreatedSession();
        session.BeginRedirect(Now);
        session.SetPspCharge("chg_2", "https://psp.example/hosted/2", Now);
        var harness = NewHarness(session: session, connections: [Disabled(NewConnection())]);

        var result = await harness.Start();

        Assert.Equal("https://psp.example/hosted/2", result.RedirectUrl);
    }

    // --- step 3: only a Created session may claim ---

    [Fact]
    public async Task A_failed_session_cannot_start_another_redirect()
    {
        var session = CreatedSession();
        session.MarkFailed("declined", Now);
        var harness = NewHarness(session: session);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await harness.Start());

        Assert.Equal(SessionStatus.Failed, harness.Session.Status);
        Assert.Equal(0, harness.Vault.Reveals);
        Assert.Equal(0, harness.UnitOfWork.SaveCount);
    }

    // --- step 4: eligibility recheck, BEFORE the claim and before the secret (REQ-3.5 / REQ-7.3) ---

    [Fact]
    public async Task A_missing_connection_is_refused_before_anything_is_claimed()
    {
        var harness = NewHarness(connections: []);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await harness.Start());

        AssertNothingWasClaimed(harness);
    }

    [Fact]
    public async Task A_connection_disabled_between_create_and_redirect_is_refused_before_anything_is_claimed()
    {
        var harness = NewHarness(connections: [Disabled(NewConnection())]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await harness.Start());

        Assert.Contains("disabled", ex.Message, StringComparison.Ordinal);
        AssertNothingWasClaimed(harness);
    }

    [Fact]
    public async Task A_method_the_connection_stopped_enabling_is_refused_before_anything_is_claimed()
    {
        // The method was enabled when the session was created; the admin has since narrowed the list. Without
        // the recheck this session would still reach the PSP on a channel the company no longer enables.
        var harness = NewHarness(connections: [NewConnection(enabledMethods: PaymentMethods.PromptPay)]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await harness.Start());

        AssertNothingWasClaimed(harness);
    }

    // --- step 5: the claim, and the concurrent loser ---

    [Fact]
    public async Task A_concurrent_loser_returns_the_winners_url_instead_of_minting_a_second_charge()
    {
        var session = CreatedSession();
        var harness = NewHarness(
            session: session,
            saveFails: save =>
            {
                if (save != 1)
                    return null;

                // The winner claimed first and has already bound its hosted charge by the time this save is
                // rejected by the rowversion check.
                session.SetPspCharge("chg_winner", "https://psp.example/hosted/winner", Now);
                return new ConcurrencyConflictException("PaymentSession was modified concurrently.");
            });

        var result = await harness.Start();

        Assert.Equal("https://psp.example/hosted/winner", result.RedirectUrl);
        Assert.Equal(0, harness.Vault.Reveals);
    }

    [Fact]
    public async Task A_concurrent_loser_with_no_url_yet_is_told_to_retry()
    {
        var harness = NewHarness(
            saveFails: save => save == 1
                ? new ConcurrencyConflictException("PaymentSession was modified concurrently.")
                : null);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await harness.Start());

        Assert.Equal(0, harness.Vault.Reveals);
    }

    // --- steps 6-8: charge, and what happens when it fails (REQ-7.1 / REQ-7.2) ---

    [Fact]
    public async Task The_claim_winner_binds_the_hosted_charge_once()
    {
        var harness = NewHarness(onCharge: _ => new PspCharge("chg_9", "https://psp.example/hosted/9"));

        var result = await harness.Start();

        Assert.Equal("https://psp.example/hosted/9", result.RedirectUrl);
        Assert.Equal(SessionStatus.Redirected, harness.Session.Status);
        Assert.Equal("chg_9", harness.Session.PspExternalChargeId);
        Assert.Equal("https://psp.example/hosted/9", harness.Session.RedirectUrl);
        Assert.Equal(1, harness.Vault.Reveals);
        Assert.Equal(2, harness.UnitOfWork.SaveCount); // the claim, then the binding
    }

    [Fact]
    public async Task A_charge_the_psp_refuses_fails_the_session_and_rethrows()
    {
        var refusal = new PspRejectedException("2c2p paymentToken declined (respCode 4009).");
        var harness = NewHarness(onCharge: _ => throw refusal);

        var thrown = await Assert.ThrowsAsync<PspRejectedException>(async () => await harness.Start());

        Assert.Same(refusal, thrown); // rethrown as-is: never swallowed, never re-wrapped into another status
        Assert.Equal(SessionStatus.Failed, harness.Session.Status);
        Assert.Null(harness.Session.RedirectUrl); // never Redirected-with-no-URL after the request (REQ-7.2)
        Assert.Equal(2, harness.UnitOfWork.SaveCount); // the claim, then the Failed transition
    }

    [Fact]
    public async Task Failing_to_record_the_failure_does_not_hide_the_psp_refusal()
    {
        var refusal = new PspRejectedException("2c2p returned HTTP 402.");
        var harness = NewHarness(
            onCharge: _ => throw refusal,
            saveFails: save => save == 2 ? new InvalidOperationException("the store is unavailable.") : null);

        var thrown = await Assert.ThrowsAsync<PspRejectedException>(async () => await harness.Start());

        // The caller must be told the PSP declined, not that the database broke while noting it down.
        Assert.Same(refusal, thrown);
        Assert.Equal(2, harness.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task A_failed_charge_lets_the_same_order_open_a_fresh_session()
    {
        // The whole line, not a hand-built Failed session: create -> redirect (the PSP refuses) -> create again.
        // Before this task the first session stayed Redirected — still "open" to GetOpenForOrderAsync — so
        // create-session kept handing back a session that could never redirect: the order was unpayable.
        var sessions = new FakeSessionRepository();
        var connections = new FakeConnectionRepository(NewConnection());
        var adapters = new FakePspAdapterFactory(
            new FakePspAdapter(Code.TwoCTwoP, PaymentMethods.Card)
            {
                OnCreateCharge = _ => throw new PspRejectedException("2c2p returned HTTP 402."),
            });
        var unitOfWork = new FakeUnitOfWork();
        var clock = new FixedClock { UtcNow = Now };

        var create = new CreateSessionHandler(
            new FakePayableOrderReader(new PayableOrder(OrderId, OrderAmount, true)),
            connections,
            adapters,
            sessions,
            unitOfWork,
            clock);
        var redirect = new StartRedirectHandler(
            sessions, connections, adapters, new FakeVaultSecretStore(), unitOfWork, clock);

        var command = new CreateSessionCommand(OrderId, MerchantId, PaymentMethods.Card, Code.TwoCTwoP);
        var first = await create.Handle(command, default);

        await Assert.ThrowsAsync<PspRejectedException>(async () =>
            await redirect.Handle(new StartRedirectCommand(first.PaymentSessionId), default));

        var second = await create.Handle(command, default);

        Assert.NotEqual(first.PaymentSessionId, second.PaymentSessionId);
        Assert.Equal(2, sessions.Added.Count);
        Assert.Equal(SessionStatus.Failed, sessions.Added[0].Status);
        Assert.Equal(SessionStatus.Created, sessions.Added[1].Status);
        Assert.Equal(OrderAmount, sessions.Added[1].Amount); // the retry is still priced from the order
    }

    // --- definitive vs ambiguous, and settling a claim (REQ-7.5 / REQ-7.6) ---

    [Fact]
    public async Task An_ambiguous_charge_failure_keeps_the_claim_instead_of_failing_the_session()
    {
        // Every one of these can happen AFTER the PSP accepted the charge: we simply never learned the answer.
        // Failing the session would let the order open a replacement whose id is a NEW idempotency key at the
        // PSP — a second charge against the same customer.
        Exception[] ambiguous =
        [
            new TaskCanceledException("The request was canceled due to a timeout."),
            new HttpRequestException("Connection reset by peer."),
            new InvalidOperationException("2c2p returned HTTP 503."),
            new InvalidOperationException("2c2p paymentToken response failed signature verification."),
        ];

        foreach (var failure in ambiguous)
        {
            var harness = NewHarness(onCharge: _ => throw failure);

            var thrown = await Assert.ThrowsAnyAsync<Exception>(async () => await harness.Start());

            Assert.Same(failure, thrown);
            Assert.Equal(SessionStatus.Redirected, harness.Session.Status); // claim intact, NOT Failed
            Assert.Null(harness.Session.RedirectUrl);
            Assert.Equal(1, harness.UnitOfWork.SaveCount); // the claim only — no Failed transition was written
        }
    }

    [Fact]
    public async Task A_vault_that_cannot_reveal_the_secret_fails_the_session()
    {
        // Definitive by position: nothing has been sent to the PSP, so the claim must not survive — with the
        // one-open-session index in place a surviving claim with no URL makes the order unpayable.
        var outage = new InvalidOperationException("Vault secret psp/secret-ref/merchant-1 is unavailable.");
        var harness = NewHarness(vaultFails: outage);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await harness.Start());

        Assert.Same(outage, thrown);
        Assert.Equal(SessionStatus.Failed, harness.Session.Status);
        Assert.Equal(2, harness.UnitOfWork.SaveCount); // the claim, then the Failed transition
    }

    [Fact]
    public async Task A_claim_with_no_url_settles_by_calling_the_psp_again_under_the_same_key()
    {
        // The state an ambiguous attempt leaves behind. Re-calling with the session's own idempotency key
        // returns the ORIGINAL charge (both adapters derive the key from Session.Id), so binding it here is
        // what lets its webhook correlate — instead of 409 forever with an untracked charge at the PSP.
        var session = CreatedSession();
        session.BeginRedirect(Now);
        var charges = 0;
        var harness = NewHarness(
            session: session,
            onCharge: _ =>
            {
                charges++;
                return new PspCharge("chg_settled", "https://psp.example/hosted/settled");
            });

        var result = await harness.Start();

        Assert.Equal("https://psp.example/hosted/settled", result.RedirectUrl);
        Assert.Equal(1, charges);
        Assert.Equal("chg_settled", harness.Session.PspExternalChargeId);
        Assert.Equal(SessionStatus.Redirected, harness.Session.Status);
        Assert.Equal(1, harness.Vault.Reveals);
        // One save: the binding. A second BeginRedirect would have thrown outright, and a re-claim would have
        // saved twice.
        Assert.Equal(1, harness.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task A_claim_with_no_url_still_settles_when_the_connection_was_disabled_meanwhile()
    {
        // The eligibility recheck gates NEW claims (REQ-3.5). Applying it here would strand a claim whose
        // charge may already exist at the PSP — exactly what REQ-7.6 exists to prevent.
        var session = CreatedSession();
        session.BeginRedirect(Now);
        var harness = NewHarness(
            session: session,
            connections: [Disabled(NewConnection())],
            onCharge: _ => new PspCharge("chg_settled", "https://psp.example/hosted/settled"));

        var result = await harness.Start();

        Assert.Equal("https://psp.example/hosted/settled", result.RedirectUrl);
    }

    [Fact]
    public async Task A_settling_call_that_is_refused_still_never_fails_the_session()
    {
        // Even an outright refusal of the RE-call proves nothing about the first attempt, which may have left a
        // charge at the PSP. The claim stays; a human (or a later successful settle) resolves it.
        var session = CreatedSession();
        session.BeginRedirect(Now);
        var harness = NewHarness(
            session: session,
            onCharge: _ => throw new PspRejectedException("2c2p returned HTTP 400."));

        await Assert.ThrowsAsync<PspRejectedException>(async () => await harness.Start());

        Assert.Equal(SessionStatus.Redirected, harness.Session.Status);
        Assert.Equal(0, harness.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task A_settling_call_whose_vault_read_fails_still_never_fails_the_session()
    {
        var session = CreatedSession();
        session.BeginRedirect(Now);
        var harness = NewHarness(
            session: session,
            vaultFails: new InvalidOperationException("Vault secret psp/secret-ref/merchant-1 is unavailable."));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await harness.Start());

        Assert.Equal(SessionStatus.Redirected, harness.Session.Status);
        Assert.Equal(0, harness.UnitOfWork.SaveCount);
    }
}
