using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;
using Payments.Infrastructure.Psp;
using SharedKernel;

namespace Payments.Tests.Psp;

/// <summary>
/// Unit tests for the real 2C2P adapter against a stub HTTP transport (no network, no real keys).
/// Covers: the hosted redirect happy path + the STABLE invoiceNo correlation key, major-unit amount
/// formatting, JWT webhook verify (incl. alg-pinning + merchant binding), status mapping, and that the
/// non-idempotent charge-create POST is single-shot while the fetch GET retries. Plus the two claims the
/// captive model turns on: backendReturnUrl derived PER CONNECTION (REQ-4.1) and paymentChannel derived
/// from the session's method with no card substitution (REQ-6.3/6.4).
/// </summary>
public sealed class TwoCTwoPAdapterTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private const string Secret = $$"""{"merchantId":"M123","secretKey":"{{Key}}"}""";
    private const string PublicBaseUrl = "https://api.pol.test";
    private const string FrontendReturnUrl = "https://console.pol.test/checkout/return";

    /// <summary>The connection being charged through — the value the backend-notification URL must carry.</summary>
    private static readonly Guid ConnectionId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");

    private static (TwoCTwoPAdapter Adapter, StubHttpMessageHandler Handler) Build(
        Func<HttpRequestMessage, string, HttpResponseMessage> responder,
        bool useSandbox = true,
        string publicBaseUrl = PublicBaseUrl)
    {
        var handler = new StubHttpMessageHandler(responder);
        var options = Options.Create(new PspOptions
        {
            UseSandbox = useSandbox,
            PublicBaseUrl = publicBaseUrl,
            TwoCTwoP = { FrontendReturnUrl = FrontendReturnUrl },
        });
        return (new TwoCTwoPAdapter(new FakeHttpClientFactory(handler), options), handler);
    }

    private static Session MakeSession(decimal amount = 250.09m, string currency = "THB", string method = "card") =>
        Session.Create(Guid.NewGuid(), Guid.NewGuid(), Money.Of(amount, currency), method, Code.TwoCTwoP, DateTime.UtcNow);

    private static HttpResponseMessage PaymentTokenOk(string webPaymentUrl) =>
        StubHttpMessageHandler.Json(JwtTestHelper.Envelope(JwtTestHelper.EncodeHs256(
            JsonSerializer.Serialize(new { respCode = "0000", webPaymentUrl, paymentToken = "tok_per_attempt" }), Key)));

    [Fact]
    public void SupportedMethods_declares_card_only()
    {
        var (adapter, _) = Build((_, _) => PaymentTokenOk("https://2c2p.test/hosted/pay"));

        // paymentChannel is built for a card charge, so claiming promptpay/installment here would let
        // create-session admit a method this adapter silently charges as a card instead.
        Assert.Equal(new[] { PaymentMethods.Card }, adapter.SupportedMethods);
    }

    [Fact]
    public async Task CreateRedirectCharge_returns_hosted_url_and_stable_invoiceNo_key()
    {
        var session = MakeSession();
        var (adapter, handler) = Build((_, _) => PaymentTokenOk("https://2c2p.test/hosted/pay"));

        var charge = await adapter.CreateRedirectChargeAsync(session, ConnectionId, Secret, CancellationToken.None);

        // The durable correlation key is invoiceNo (= session.Id 'N'), NOT the per-attempt paymentToken.
        Assert.Equal(session.Id.ToString("N"), charge.ExternalChargeId);
        Assert.Equal("https://2c2p.test/hosted/pay", charge.RedirectUrl);
        Assert.Equal(1, handler.CallCount);
        Assert.EndsWith("/payment/4.3/paymentToken", handler.Calls[0].Uri!.AbsolutePath);
    }

    [Theory]
    [InlineData(250.09, "THB", "250.09")]
    [InlineData(5000, "JPY", "5000")]
    public async Task CreateRedirectCharge_formats_major_unit_amount_and_alpha_currency(double amount, string currency, string expectedAmount)
    {
        var session = MakeSession((decimal)amount, currency);
        var (adapter, handler) = Build((_, _) => PaymentTokenOk("https://2c2p.test/hosted/pay"));

        await adapter.CreateRedirectChargeAsync(session, ConnectionId, Secret, CancellationToken.None);

        var claims = JwtTestHelper.DecodePayload(JwtTestHelper.PayloadOf(handler.Calls[0].Body));
        Assert.Equal(expectedAmount, claims.GetProperty("amount").GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(currency, claims.GetProperty("currencyCode").GetString());
        Assert.Equal(session.Id.ToString("N"), claims.GetProperty("invoiceNo").GetString());
        // The idempotencyID rides with the invoiceNo so a PSP-side retry returns the first charge.
        Assert.Equal(session.Id.ToString("N"), claims.GetProperty("idempotencyID").GetString());
    }

    [Fact]
    public async Task CreateRedirectCharge_charges_the_amount_the_session_carries()
    {
        // REQ-1.6: the amount the adapter puts on the wire is the session's own, which create-session reads
        // from the order row and nowhere else — so the amount the PSP collects traces back to an order.
        var session = MakeSession(1234.50m, "THB");
        var (adapter, handler) = Build((_, _) => PaymentTokenOk("https://2c2p.test/hosted/pay"));

        await adapter.CreateRedirectChargeAsync(session, ConnectionId, Secret, CancellationToken.None);

        var claims = JwtTestHelper.DecodePayload(JwtTestHelper.PayloadOf(handler.Calls[0].Body));
        Assert.Equal(session.Amount.Amount, claims.GetProperty("amount").GetDecimal());
        Assert.Equal(session.Amount.Currency, claims.GetProperty("currencyCode").GetString());
    }

    [Fact]
    public async Task CreateRedirectCharge_points_the_backend_notification_at_the_connection_being_charged()
    {
        // REQ-4.1: backendReturnUrl is DERIVED per connection, so every company's webhook reaches the route
        // (/api/v1/webhooks/{pspConnectionId}) instead of only whichever connection a global URL named.
        // frontendReturnUrl stays the configured platform-wide value (REQ-4.4) — one shared Tenant Console.
        var session = MakeSession();
        var (adapter, handler) = Build((_, _) => PaymentTokenOk("https://2c2p.test/hosted/pay"));

        await adapter.CreateRedirectChargeAsync(session, ConnectionId, Secret, CancellationToken.None);

        var claims = JwtTestHelper.DecodePayload(JwtTestHelper.PayloadOf(handler.Calls[0].Body));
        Assert.Equal(
            $"{PublicBaseUrl}/api/v1/webhooks/{ConnectionId:D}",
            claims.GetProperty("backendReturnUrl").GetString());
        Assert.Equal(FrontendReturnUrl, claims.GetProperty("frontendReturnUrl").GetString());

        // A second connection must get a DIFFERENT callback URL — a constant would pass the assertion above.
        var other = Guid.Parse("2f1c8d34-5b6a-4e7f-8a90-b1c2d3e4f506");
        await adapter.CreateRedirectChargeAsync(MakeSession(), other, Secret, CancellationToken.None);
        var secondClaims = JwtTestHelper.DecodePayload(JwtTestHelper.PayloadOf(handler.Calls[1].Body));
        Assert.Equal(
            $"{PublicBaseUrl}/api/v1/webhooks/{other:D}",
            secondClaims.GetProperty("backendReturnUrl").GetString());
    }

    [Fact]
    public async Task CreateRedirectCharge_does_not_double_the_slash_of_a_public_base_url()
    {
        // An operator-supplied origin with a trailing slash must not produce "...test//api/v1/webhooks/...":
        // 2C2P would call back a path our route never matches, which is the same silent miss as no URL at all.
        var session = MakeSession();
        var (adapter, handler) = Build(
            (_, _) => PaymentTokenOk("https://2c2p.test/hosted/pay"), publicBaseUrl: PublicBaseUrl + "/");

        await adapter.CreateRedirectChargeAsync(session, ConnectionId, Secret, CancellationToken.None);

        var claims = JwtTestHelper.DecodePayload(JwtTestHelper.PayloadOf(handler.Calls[0].Body));
        Assert.Equal(
            $"{PublicBaseUrl}/api/v1/webhooks/{ConnectionId:D}",
            claims.GetProperty("backendReturnUrl").GetString());
    }

    [Fact]
    public async Task CreateRedirectCharge_derives_the_payment_channel_from_the_session_method()
    {
        // REQ-6.3: the channel comes from Session.Method through an explicit mapping, not the hardcoded
        // ["CC"] this adapter used to send regardless of what the customer picked.
        var session = MakeSession(method: PaymentMethods.Card);
        var (adapter, handler) = Build((_, _) => PaymentTokenOk("https://2c2p.test/hosted/pay"));

        await adapter.CreateRedirectChargeAsync(session, ConnectionId, Secret, CancellationToken.None);

        var claims = JwtTestHelper.DecodePayload(JwtTestHelper.PayloadOf(handler.Calls[0].Body));
        Assert.Equal(
            new[] { "CC" },
            claims.GetProperty("paymentChannel").EnumerateArray().Select(c => c.GetString()).ToArray());
    }

    [Theory]
    [InlineData(PaymentMethods.PromptPay)]
    [InlineData(PaymentMethods.Installment)]
    public async Task CreateRedirectCharge_refuses_a_method_it_cannot_honour_rather_than_substituting_a_card_channel(string method)
    {
        // REQ-6.4: a method outside SupportedMethods must never reach the PSP at all. Before this, such a
        // session was charged as a card — the customer picked PromptPay and landed on a card page.
        var session = MakeSession(method: method);
        var (adapter, handler) = Build((_, _) => PaymentTokenOk("https://2c2p.test/hosted/pay"));

        var refusal = await Assert.ThrowsAsync<PspRejectedException>(
            () => adapter.CreateRedirectChargeAsync(session, ConnectionId, Secret, CancellationToken.None));

        Assert.Contains(method, refusal.Message, StringComparison.Ordinal); // names the method, not a generic 500
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task CreateRedirectCharge_rejects_amount_finer_than_the_currency_minor_unit()
    {
        // THB's minor unit is 2 decimals; Money itself allows scale <= 4, so 10.0050 is a valid Money but
        // not representable at THB's wire precision — must reject, not silently round to "10.01".
        var session = MakeSession(10.0050m, "THB");
        var (adapter, handler) = Build((_, _) => PaymentTokenOk("https://2c2p.test/hosted/pay"));

        await Assert.ThrowsAsync<PspRejectedException>(
            () => adapter.CreateRedirectChargeAsync(session, ConnectionId, Secret, CancellationToken.None));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task CreateRedirectCharge_rejects_fractional_amount_on_a_zero_decimal_currency()
    {
        // JPY has zero minor-unit digits; 10.5 has no representable rounding target.
        var session = MakeSession(10.5m, "JPY");
        var (adapter, handler) = Build((_, _) => PaymentTokenOk("https://2c2p.test/hosted/pay"));

        await Assert.ThrowsAsync<PspRejectedException>(
            () => adapter.CreateRedirectChargeAsync(session, ConnectionId, Secret, CancellationToken.None));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task CreateRedirectCharge_throws_on_declined_respCode()
    {
        var session = MakeSession();
        var declined = StubHttpMessageHandler.Json(JwtTestHelper.Envelope(JwtTestHelper.EncodeHs256(
            JsonSerializer.Serialize(new { respCode = "4009", respDesc = "declined" }), Key)));
        var (adapter, _) = Build((_, _) => declined);

        await Assert.ThrowsAsync<PspRejectedException>(
            () => adapter.CreateRedirectChargeAsync(session, ConnectionId, Secret, CancellationToken.None));
    }

    [Fact]
    public void VerifyWebhook_accepts_valid_signed_notification_for_our_merchant()
    {
        var (adapter, _) = Build((_, _) => StubHttpMessageHandler.Json("{}"));
        var body = JwtTestHelper.Envelope(JwtTestHelper.EncodeHs256(
            JsonSerializer.Serialize(new { merchantID = "M123", invoiceNo = "INV1", respCode = "0000", tranRef = "T1" }), Key));

        Assert.True(adapter.VerifyWebhook(body, signature: "", Secret));
    }

    [Fact]
    public void VerifyWebhook_rejects_wrong_secret_alg_none_and_foreign_merchant()
    {
        var (adapter, _) = Build((_, _) => StubHttpMessageHandler.Json("{}"));
        var claims = JsonSerializer.Serialize(new { merchantID = "M123", invoiceNo = "INV1", respCode = "0000" });

        var wrongSecret = JwtTestHelper.Envelope(JwtTestHelper.EncodeHs256(claims, "a-different-key-aaaaaaaaaaaaaaaaaa"));
        var algNone = JwtTestHelper.Envelope(JwtTestHelper.EncodeHs256(claims, Key, alg: "none"));
        var foreignMerchant = JwtTestHelper.Envelope(JwtTestHelper.EncodeHs256(
            JsonSerializer.Serialize(new { merchantID = "OTHER", invoiceNo = "INV1", respCode = "0000" }), Key));

        Assert.False(adapter.VerifyWebhook(wrongSecret, "", Secret));
        Assert.False(adapter.VerifyWebhook(algNone, "", Secret));
        Assert.False(adapter.VerifyWebhook(foreignMerchant, "", Secret));
    }

    [Fact]
    public void ParseWebhook_maps_invoiceNo_as_external_charge_key()
    {
        var (adapter, _) = Build((_, _) => StubHttpMessageHandler.Json("{}"));
        var body = JwtTestHelper.Envelope(JwtTestHelper.EncodeHs256(
            JsonSerializer.Serialize(new { merchantID = "M123", invoiceNo = "INV1", respCode = "0000", tranRef = "T1" }), Key));

        var evt = adapter.ParseWebhook(body);

        Assert.Equal("T1", evt.EventId);
        Assert.Equal("INV1", evt.ExternalChargeId); // same key create returned + fetch queries by
        Assert.Equal(PspChargeStatus.Paid, evt.Status);
    }

    [Theory]
    [InlineData("0000", PspChargeStatus.Paid)]
    [InlineData("0001", PspChargeStatus.Pending)]
    [InlineData("2001", PspChargeStatus.Pending)]
    [InlineData("9035", PspChargeStatus.Failed)]
    public async Task FetchCharge_maps_respCode_to_status(string respCode, PspChargeStatus expected)
    {
        var inquiry = StubHttpMessageHandler.Json(JwtTestHelper.Envelope(JwtTestHelper.EncodeHs256(
            JsonSerializer.Serialize(new { respCode }), Key)));
        var (adapter, handler) = Build((_, _) => inquiry);

        var confirmed = await adapter.FetchChargeAsync("INV1", Secret, CancellationToken.None);

        Assert.Equal(expected, confirmed.Status);
        Assert.EndsWith("/payment/4.3/paymentInquiry", handler.Calls[0].Uri!.AbsolutePath);
    }

    [Theory]
    [InlineData(250.09, "THB")]
    [InlineData(5000, "JPY")] // 0-decimal currency: 2C2P reports major units either way
    public async Task FetchCharge_reports_the_major_unit_amount_the_psp_collected(double amount, string currency)
    {
        // REQ-8.1: paymentInquiry carries the collected amount in MAJOR units under `amount`/`currencyCode`.
        // It is the only value in the whole flow that comes from the PSP rather than from our own row, so it
        // is what makes the pre-MarkPaid comparison something other than a tautology.
        var inquiry = StubHttpMessageHandler.Json(JwtTestHelper.Envelope(JwtTestHelper.EncodeHs256(
            JsonSerializer.Serialize(new { respCode = "0000", amount, currencyCode = currency }), Key)));
        var (adapter, _) = Build((_, _) => inquiry);

        var confirmed = await adapter.FetchChargeAsync("INV1", Secret, CancellationToken.None);

        Assert.Equal(Money.Of((decimal)amount, currency), confirmed.Amount);
    }

    [Theory]
    // Absent entirely — the shape of every fetch response before this feature existed.
    [InlineData("""{"respCode":"0000"}""")]
    // Present but not a JSON number (a string amount) — reads as "not reported", never as 0.
    [InlineData("""{"respCode":"0000","amount":"250.09","currencyCode":"THB"}""")]
    // Amount without a currency, and vice versa: neither half alone is comparable money.
    [InlineData("""{"respCode":"0000","amount":250.09}""")]
    [InlineData("""{"respCode":"0000","currencyCode":"THB"}""")]
    // A currency outside the platform's ISO 4217 allowlist (and a non-alpha-3 code).
    [InlineData("""{"respCode":"0000","amount":250.09,"currencyCode":"XYZ"}""")]
    [InlineData("""{"respCode":"0000","amount":250.09,"currencyCode":"764"}""")]
    public async Task FetchCharge_reports_no_amount_rather_than_throwing_when_the_response_lacks_a_usable_one(string claims)
    {
        // REQ-8.3: the amount field's contract is not sandbox-verified on every path, so an unusable value
        // must degrade to status-only confirmation. Throwing here would abandon a real paid charge; reading
        // it as zero would refuse one. The status must still come through intact.
        var inquiry = StubHttpMessageHandler.Json(JwtTestHelper.Envelope(JwtTestHelper.EncodeHs256(claims, Key)));
        var (adapter, _) = Build((_, _) => inquiry);

        var confirmed = await adapter.FetchChargeAsync("INV1", Secret, CancellationToken.None);

        Assert.Null(confirmed.Amount);
        Assert.Equal(PspChargeStatus.Paid, confirmed.Status);
    }

    [Fact]
    public async Task FetchCharge_retries_idempotent_inquiry_on_transient_then_succeeds()
    {
        var attempts = 0;
        var (adapter, handler) = Build((_, _) =>
        {
            attempts++;
            return attempts <= 2
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : StubHttpMessageHandler.Json(JwtTestHelper.Envelope(JwtTestHelper.EncodeHs256(
                    JsonSerializer.Serialize(new { respCode = "0000" }), Key)));
        });

        var confirmed = await adapter.FetchChargeAsync("INV1", Secret, CancellationToken.None);

        Assert.Equal(PspChargeStatus.Paid, confirmed.Status);
        Assert.Equal(3, handler.CallCount); // 2 transient + 1 success
    }

    [Fact]
    public async Task CreateRedirectCharge_does_not_retry_the_non_idempotent_post()
    {
        var session = MakeSession();
        var (adapter, handler) = Build((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.CreateRedirectChargeAsync(session, ConnectionId, Secret, CancellationToken.None));

        Assert.Equal(1, handler.CallCount); // single-shot: a retry could double-charge
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, true)]          // the PSP refused it: no charge exists
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.RequestTimeout, false)]     // retry-me statuses say nothing about the charge
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public async Task CreateRedirectCharge_separates_an_outright_refusal_from_an_unknown_outcome(
        HttpStatusCode status, bool refused)
    {
        // The exception TYPE is what tells the handler whether it may fail the session: get this wrong for a
        // 5xx/408/429 and a charge the PSP is holding gets a second, differently-keyed sibling (REQ-7.5).
        var session = MakeSession();
        var (adapter, _) = Build((_, _) => new HttpResponseMessage(status));

        var thrown = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => adapter.CreateRedirectChargeAsync(session, ConnectionId, Secret, CancellationToken.None));

        Assert.Equal(refused, thrown is PspRejectedException);
    }

    [Fact]
    public async Task FetchCharge_queries_by_the_same_invoiceNo_correlation_key()
    {
        // The create -> fetch leg of the correlation: paymentInquiry must query by the SAME invoiceNo the
        // session was stored under, or GetByExternalChargeAsync never resolves the session.
        var inquiry = StubHttpMessageHandler.Json(JwtTestHelper.Envelope(JwtTestHelper.EncodeHs256(
            JsonSerializer.Serialize(new { respCode = "0000" }), Key)));
        var (adapter, handler) = Build((_, _) => inquiry);

        await adapter.FetchChargeAsync("INV-XYZ", Secret, CancellationToken.None);

        var claims = JwtTestHelper.DecodePayload(JwtTestHelper.PayloadOf(handler.Calls[0].Body));
        Assert.Equal("INV-XYZ", claims.GetProperty("invoiceNo").GetString());
    }

    [Theory]
    [InlineData("0000", PspChargeStatus.Paid)]
    [InlineData("0001", PspChargeStatus.Pending)] // pending webhook is Pending, not Failed (matches fetch)
    [InlineData("9035", PspChargeStatus.Failed)]
    public void ParseWebhook_maps_respCode_to_status(string respCode, PspChargeStatus expected)
    {
        var (adapter, _) = Build((_, _) => StubHttpMessageHandler.Json("{}"));
        var body = JwtTestHelper.Envelope(JwtTestHelper.EncodeHs256(
            JsonSerializer.Serialize(new { merchantID = "M123", invoiceNo = "INV1", respCode, tranRef = "T1" }), Key));

        Assert.Equal(expected, adapter.ParseWebhook(body).Status);
    }

    [Fact]
    public async Task CreateRedirectCharge_rejects_a_response_signed_with_the_wrong_key()
    {
        // The adapter must not trust a forged/tampered PSP response (the response JWT is signature-verified).
        var session = MakeSession();
        var forged = StubHttpMessageHandler.Json(JwtTestHelper.Envelope(JwtTestHelper.EncodeHs256(
            JsonSerializer.Serialize(new { respCode = "0000", webPaymentUrl = "https://evil.test/pay" }),
            "a-different-key-aaaaaaaaaaaaaaaaaa")));
        var (adapter, _) = Build((_, _) => forged);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.CreateRedirectChargeAsync(session, ConnectionId, Secret, CancellationToken.None));
    }

    [Fact]
    public async Task FetchCharge_rejects_a_response_signed_with_the_wrong_key()
    {
        var forged = StubHttpMessageHandler.Json(JwtTestHelper.Envelope(JwtTestHelper.EncodeHs256(
            JsonSerializer.Serialize(new { respCode = "0000" }), "a-different-key-aaaaaaaaaaaaaaaaaa")));
        var (adapter, _) = Build((_, _) => forged);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.FetchChargeAsync("INV1", Secret, CancellationToken.None));
    }

    [Fact]
    public void VerifyWebhook_rejects_a_non_string_alg_header_without_throwing()
    {
        // A crafted header {"alg":1} must return false (clean Rejected), not throw 500 on the webhook.
        var (adapter, _) = Build((_, _) => StubHttpMessageHandler.Json("{}"));
        var jwt = JwtWithRawHeader("""{"alg":1,"typ":"JWT"}""",
            JsonSerializer.Serialize(new { merchantID = "M123", invoiceNo = "INV1", respCode = "0000" }));

        Assert.False(adapter.VerifyWebhook(JwtTestHelper.Envelope(jwt), "", Secret));
    }

    private static string JwtWithRawHeader(string headerJson, string claimsJson)
    {
        static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var h = B64Url(System.Text.Encoding.UTF8.GetBytes(headerJson));
        var p = B64Url(System.Text.Encoding.UTF8.GetBytes(claimsJson));
        return h + "." + p + ".c2ln"; // signature segment is irrelevant; the alg guard rejects first
    }
}
