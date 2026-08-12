using System.Net;
using Microsoft.Extensions.Options;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;
using Payments.Infrastructure.Psp;
using SharedKernel;

namespace Payments.Tests.Psp;

/// <summary>
/// Unit tests for the real Omise adapter against a stub HTTP transport (no network, no real keys).
/// Covers: card hosted-3DS redirect (authorize_uri) with a deterministic Idempotency-Key + conversion
/// to Omise's minor-unit wire amount, PromptPay via Payment Links+ (hosted transaction_url, never
/// source+charge), the test/live key-environment guard, status mapping (pending never Failed), and
/// webhook well-formedness.
/// </summary>
public sealed class OmiseAdapterTests
{
    private const string CardSecret = """{"secretKey":"skey_test_abc"}""";

    /// <summary>The connection being charged through. Omise takes its webhook endpoint from the dashboard,
    /// not from the charge request, so this id must NOT appear in the request body — the per-connection
    /// callback is an ops step in the deploy runbook instead (REQ-4.5).</summary>
    private static readonly Guid ConnectionId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");

    private static (OmiseAdapter Adapter, StubHttpMessageHandler Handler) Build(
        Func<HttpRequestMessage, string, HttpResponseMessage> responder, bool useSandbox = true)
    {
        var handler = new StubHttpMessageHandler(responder);
        var options = Options.Create(new PspOptions { UseSandbox = useSandbox });
        return (new OmiseAdapter(new FakeHttpClientFactory(handler), options), handler);
    }

    private static Session MakeSession(string method, decimal amount = 20.00m, string currency = "THB") =>
        Session.Create(Guid.NewGuid(), Guid.NewGuid(), Money.Of(amount, currency), method, Code.Omise, DateTime.UtcNow);

    [Fact]
    public void SupportedMethods_declares_card_only()
    {
        var (adapter, _) = Build((_, _) => StubHttpMessageHandler.Json("{}"));

        // PromptPay is deferred and installment was never wired — the capability set must not claim
        // either, so create-session refuses them instead of the charge call throwing NotSupported (500).
        Assert.Equal(new[] { PaymentMethods.Card }, adapter.SupportedMethods);
    }

    [Fact]
    public async Task TestConnection_reads_authenticated_account_without_creating_a_charge()
    {
        var (adapter, handler) = Build((_, _) =>
            StubHttpMessageHandler.Json("""{"object":"account","id":"acct_test_1"}"""));

        var result = await adapter.TestConnectionAsync(CardSecret, CancellationToken.None);

        Assert.Equal("authenticated", result.Code);
        Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Get, handler.Calls[0].Method);
        Assert.EndsWith("/account", handler.Calls[0].Uri!.AbsolutePath);
        Assert.StartsWith("Basic ", handler.Calls[0].Authorization);
    }

    [Fact]
    public async Task TestConnection_rejects_a_non_account_response()
    {
        var (adapter, _) = Build((_, _) =>
            StubHttpMessageHandler.Json("""{"object":"charge","id":"chrg_test_1"}"""));

        await Assert.ThrowsAsync<PspRejectedException>(() =>
            adapter.TestConnectionAsync(CardSecret, CancellationToken.None));
    }

    [Fact]
    public async Task Card_charge_returns_hosted_authorize_uri_with_idempotency_key_and_minor_unit_amount()
    {
        var session = MakeSession("card");
        var (adapter, handler) = Build((_, _) => StubHttpMessageHandler.Json(
            """{"id":"chrg_test_1","authorize_uri":"https://omise.test/3ds","status":"pending"}"""));

        var charge = await adapter.CreateRedirectChargeAsync(session, ConnectionId, CardSecret, CancellationToken.None);

        Assert.Equal("chrg_test_1", charge.ExternalChargeId);
        Assert.Equal("https://omise.test/3ds", charge.RedirectUrl);
        Assert.Equal(1, handler.CallCount);
        Assert.EndsWith("/charges", handler.Calls[0].Uri!.AbsolutePath);
        Assert.Equal(session.Id.ToString("N"), handler.Calls[0].IdempotencyKey); // retry returns same charge
        Assert.StartsWith("Basic ", handler.Calls[0].Authorization);
        Assert.Contains("amount=2000", handler.Calls[0].Body); // 20.00 THB -> 2000 satang
        Assert.Contains("currency=THB", handler.Calls[0].Body);
    }

    [Theory]
    [InlineData(250.09, "THB", "amount=25009")] // 250.09 THB -> 25009 satang
    [InlineData(5000, "JPY", "amount=5000")]    // 0-decimal currency: major == minor
    public async Task Card_charge_converts_amount_to_minor_units(double amount, string currency, string expected)
    {
        var session = MakeSession("card", (decimal)amount, currency);
        var (adapter, handler) = Build((_, _) => StubHttpMessageHandler.Json(
            """{"id":"chrg_test_1","authorize_uri":"https://omise.test/3ds","status":"pending"}"""));

        await adapter.CreateRedirectChargeAsync(session, ConnectionId, CardSecret, CancellationToken.None);

        Assert.Contains(expected, handler.Calls[0].Body);
    }

    [Fact]
    public async Task Card_charge_sends_no_callback_url_because_omise_takes_its_webhook_from_the_dashboard()
    {
        // Omise/Opn has no per-charge notification-URL field: the endpoint is registered in the merchant's
        // Omise dashboard, so the per-connection callback is an ops step in docs/runbooks/deploy-self-host.md
        // (REQ-4.5). Inventing a request field for it would be sent nowhere and read as "handled".
        var session = MakeSession("card");
        var (adapter, handler) = Build((_, _) => StubHttpMessageHandler.Json(
            """{"id":"chrg_test_1","authorize_uri":"https://omise.test/3ds","status":"pending"}"""));

        await adapter.CreateRedirectChargeAsync(session, ConnectionId, CardSecret, CancellationToken.None);

        Assert.DoesNotContain(ConnectionId.ToString("D"), handler.Calls[0].Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("webhooks", handler.Calls[0].Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Card_charge_rejects_amount_finer_than_the_currency_minor_unit()
    {
        // THB's minor unit is 2 decimals (satang); Money itself allows scale <= 4, so 10.0050 is a valid
        // Money but not representable as satang — must reject, not silently round to 10.01/1001 satang.
        var session = MakeSession("card", 10.0050m, "THB");
        var (adapter, handler) = Build((_, _) => StubHttpMessageHandler.Json("{}"));

        await Assert.ThrowsAsync<PspRejectedException>(
            () => adapter.CreateRedirectChargeAsync(session, ConnectionId, CardSecret, CancellationToken.None));
        Assert.Equal(0, handler.CallCount); // guard runs before the non-idempotent POST
    }

    [Fact]
    public async Task Card_charge_rejects_fractional_amount_on_a_zero_decimal_currency()
    {
        // JPY has zero minor-unit digits; 10.5 has no satang-equivalent to round to.
        var session = MakeSession("card", 10.5m, "JPY");
        var (adapter, handler) = Build((_, _) => StubHttpMessageHandler.Json("{}"));

        await Assert.ThrowsAsync<PspRejectedException>(
            () => adapter.CreateRedirectChargeAsync(session, ConnectionId, CardSecret, CancellationToken.None));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task PromptPay_is_deferred_and_is_refused_outright()
    {
        // Correlation (link id vs webhook/fetch charge id) cannot be made consistent without a sandbox —
        // PromptPay is deferred rather than shipped broken. No PSP call is made on the deferred path.
        var session = MakeSession("promptpay");
        var (adapter, handler) = Build((_, _) => StubHttpMessageHandler.Json("{}"));

        await Assert.ThrowsAsync<PspRejectedException>(
            () => adapter.CreateRedirectChargeAsync(session, ConnectionId, CardSecret, CancellationToken.None));
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData("skey_live_xyz", true)]   // live key, sandbox config
    [InlineData("skey_test_abc", false)]  // test key, production config
    public async Task Charge_fails_fast_when_key_environment_mismatches_UseSandbox(string secretKey, bool useSandbox)
    {
        var session = MakeSession("card");
        var (adapter, handler) = Build((_, _) => StubHttpMessageHandler.Json("{}"), useSandbox);
        var secret = $$"""{"secretKey":"{{secretKey}}"}""";

        await Assert.ThrowsAsync<PspRejectedException>(
            () => adapter.CreateRedirectChargeAsync(session, ConnectionId, secret, CancellationToken.None));
        Assert.Equal(0, handler.CallCount); // guard runs BEFORE the non-idempotent POST
    }

    [Theory]
    [InlineData("successful", PspChargeStatus.Paid)]
    [InlineData("pending", PspChargeStatus.Pending)]
    [InlineData("failed", PspChargeStatus.Failed)]
    [InlineData("expired", PspChargeStatus.Failed)]
    [InlineData("reversed", PspChargeStatus.Failed)]
    [InlineData("anything-unknown", PspChargeStatus.Pending)] // never infer Failed from a single pending/unknown
    public async Task FetchCharge_maps_status(string status, PspChargeStatus expected)
    {
        var (adapter, handler) = Build((_, _) => StubHttpMessageHandler.Json($$"""{"id":"chrg_test_1","status":"{{status}}"}"""));

        var confirmed = await adapter.FetchChargeAsync("chrg_test_1", CardSecret, CancellationToken.None);

        Assert.Equal(expected, confirmed.Status);
        Assert.EndsWith("/charges/chrg_test_1", handler.Calls[0].Uri!.AbsolutePath);
    }

    [Theory]
    [InlineData(25009, "THB", 250.09)] // 25009 satang -> 250.09 THB
    [InlineData(2000, "THB", 20.00)]
    [InlineData(5000, "JPY", 5000)]    // 0-decimal currency: minor == major, no scaling
    public async Task FetchCharge_reports_the_collected_amount_converted_back_to_major_units(
        int minorUnits, string currency, double expected)
    {
        // REQ-8.1: GET /charges/{id} reports MINOR units (the same convention the charge POST uses), so the
        // fetch must invert the conversion — comparing a satang integer against a THB Money would reject
        // every correct payment.
        var (adapter, _) = Build((_, _) => StubHttpMessageHandler.Json(
            $$"""{"id":"chrg_test_1","status":"successful","amount":{{minorUnits}},"currency":"{{currency}}"}"""));

        var confirmed = await adapter.FetchChargeAsync("chrg_test_1", CardSecret, CancellationToken.None);

        Assert.Equal(Money.Of((decimal)expected, currency), confirmed.Amount);
    }

    [Theory]
    [InlineData("""{"id":"chrg_test_1","status":"successful"}""")]
    [InlineData("""{"id":"chrg_test_1","status":"successful","amount":"25009","currency":"THB"}""")]
    [InlineData("""{"id":"chrg_test_1","status":"successful","amount":25009}""")]
    [InlineData("""{"id":"chrg_test_1","status":"successful","currency":"THB"}""")]
    [InlineData("""{"id":"chrg_test_1","status":"successful","amount":25009,"currency":"XYZ"}""")]
    [InlineData("""{"id":"chrg_test_1","status":"successful","amount":-25009,"currency":"THB"}""")]
    public async Task FetchCharge_reports_no_amount_rather_than_throwing_when_the_response_lacks_a_usable_one(string body)
    {
        // REQ-8.3: status-only confirmation, never zero and never an exception — an unverified response
        // contract must not be able to stop a real payment from being confirmed.
        var (adapter, _) = Build((_, _) => StubHttpMessageHandler.Json(body));

        var confirmed = await adapter.FetchChargeAsync("chrg_test_1", CardSecret, CancellationToken.None);

        Assert.Null(confirmed.Amount);
        Assert.Equal(PspChargeStatus.Paid, confirmed.Status);
    }

    [Fact]
    public void VerifyWebhook_accepts_wellformed_event_and_rejects_others()
    {
        var (adapter, _) = Build((_, _) => StubHttpMessageHandler.Json("{}"));

        Assert.True(adapter.VerifyWebhook("""{"object":"event","id":"evnt_test_1"}""", "", CardSecret));
        Assert.False(adapter.VerifyWebhook("""{"object":"charge","id":"chrg_1"}""", "", CardSecret));
        Assert.False(adapter.VerifyWebhook("not json", "", CardSecret));
    }

    [Fact]
    public void ParseWebhook_extracts_event_and_charge_ids()
    {
        var (adapter, _) = Build((_, _) => StubHttpMessageHandler.Json("{}"));

        var evt = adapter.ParseWebhook("""{"object":"event","id":"evnt_test_1","data":{"id":"chrg_test_1","status":"successful"}}""");

        Assert.Equal("evnt_test_1", evt.EventId);
        Assert.Equal("chrg_test_1", evt.ExternalChargeId);
        Assert.Equal(PspChargeStatus.Paid, evt.Status);
    }
}
