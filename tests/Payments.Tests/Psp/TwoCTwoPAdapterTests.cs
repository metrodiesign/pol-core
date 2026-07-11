using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Infrastructure.Psp;
using SharedKernel;

namespace Payments.Tests.Psp;

/// <summary>
/// Unit tests for the real 2C2P adapter against a stub HTTP transport (no network, no real keys).
/// Covers: the hosted redirect happy path + the STABLE invoiceNo correlation key, major-unit amount
/// formatting, JWT webhook verify (incl. alg-pinning + merchant binding), status mapping, and that the
/// non-idempotent charge-create POST is single-shot while the fetch GET retries.
/// </summary>
public sealed class TwoCTwoPAdapterTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private const string Secret = $$"""{"merchantId":"M123","secretKey":"{{Key}}"}""";

    private static (TwoCTwoPAdapter Adapter, StubHttpMessageHandler Handler) Build(
        Func<HttpRequestMessage, string, HttpResponseMessage> responder, bool useSandbox = true)
    {
        var handler = new StubHttpMessageHandler(responder);
        var options = Options.Create(new PspOptions { UseSandbox = useSandbox });
        return (new TwoCTwoPAdapter(new FakeHttpClientFactory(handler), options), handler);
    }

    private static PaymentSession Session(decimal amount = 250.09m, string currency = "THB") =>
        PaymentSession.Create(Guid.NewGuid(), Guid.NewGuid(), Money.Of(amount, currency), "card", PspCode.TwoCTwoP, DateTime.UtcNow);

    private static HttpResponseMessage PaymentTokenOk(string webPaymentUrl) =>
        StubHttpMessageHandler.Json(JwtTestHelper.Envelope(JwtTestHelper.EncodeHs256(
            JsonSerializer.Serialize(new { respCode = "0000", webPaymentUrl, paymentToken = "tok_per_attempt" }), Key)));

    [Fact]
    public async Task CreateRedirectCharge_returns_hosted_url_and_stable_invoiceNo_key()
    {
        var session = Session();
        var (adapter, handler) = Build((_, _) => PaymentTokenOk("https://2c2p.test/hosted/pay"));

        var charge = await adapter.CreateRedirectChargeAsync(session, Secret, CancellationToken.None);

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
        var session = Session((decimal)amount, currency);
        var (adapter, handler) = Build((_, _) => PaymentTokenOk("https://2c2p.test/hosted/pay"));

        await adapter.CreateRedirectChargeAsync(session, Secret, CancellationToken.None);

        var claims = JwtTestHelper.DecodePayload(JwtTestHelper.PayloadOf(handler.Calls[0].Body));
        Assert.Equal(expectedAmount, claims.GetProperty("amount").GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(currency, claims.GetProperty("currencyCode").GetString());
        Assert.Equal(session.Id.ToString("N"), claims.GetProperty("invoiceNo").GetString());
        // The idempotencyID rides with the invoiceNo so a PSP-side retry returns the first charge.
        Assert.Equal(session.Id.ToString("N"), claims.GetProperty("idempotencyID").GetString());
    }

    [Fact]
    public async Task CreateRedirectCharge_throws_on_declined_respCode()
    {
        var session = Session();
        var declined = StubHttpMessageHandler.Json(JwtTestHelper.Envelope(JwtTestHelper.EncodeHs256(
            JsonSerializer.Serialize(new { respCode = "4009", respDesc = "declined" }), Key)));
        var (adapter, _) = Build((_, _) => declined);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.CreateRedirectChargeAsync(session, Secret, CancellationToken.None));
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

        var status = await adapter.FetchChargeAsync("INV1", Secret, CancellationToken.None);

        Assert.Equal(expected, status);
        Assert.EndsWith("/payment/4.3/paymentInquiry", handler.Calls[0].Uri!.AbsolutePath);
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

        var status = await adapter.FetchChargeAsync("INV1", Secret, CancellationToken.None);

        Assert.Equal(PspChargeStatus.Paid, status);
        Assert.Equal(3, handler.CallCount); // 2 transient + 1 success
    }

    [Fact]
    public async Task CreateRedirectCharge_does_not_retry_the_non_idempotent_post()
    {
        var session = Session();
        var (adapter, handler) = Build((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.CreateRedirectChargeAsync(session, Secret, CancellationToken.None));

        Assert.Equal(1, handler.CallCount); // single-shot: a retry could double-charge
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
        var session = Session();
        var forged = StubHttpMessageHandler.Json(JwtTestHelper.Envelope(JwtTestHelper.EncodeHs256(
            JsonSerializer.Serialize(new { respCode = "0000", webPaymentUrl = "https://evil.test/pay" }),
            "a-different-key-aaaaaaaaaaaaaaaaaa")));
        var (adapter, _) = Build((_, _) => forged);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.CreateRedirectChargeAsync(session, Secret, CancellationToken.None));
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
