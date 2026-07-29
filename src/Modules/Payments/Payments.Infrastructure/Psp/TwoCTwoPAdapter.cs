using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;

namespace Payments.Infrastructure.Psp;

/// <summary>
/// Real 2C2P Payment Gateway (PGW v4.3) adapter. Redirect-only card flow: every request/response body is
/// {"payload": HS256-JWT} signed with the merchant secret key. CreateRedirectChargeAsync -> paymentToken
/// returns the HOSTED webPaymentUrl (PCI SAQ A — no card fields touch us). FetchChargeAsync -> paymentInquiry
/// is the authoritative status (respCode). The webhook's authenticity IS the body JWT's HS256 signature.
/// The single revealed secret is a JSON envelope {merchantId, secretKey}.
/// <para>The correlation key across all three calls is a STABLE invoiceNo derived from PaymentSession.Id —
/// returned by create, emitted by ParseWebhook, and queried by FetchChargeAsync — so the webhook handler's
/// GetByExternalChargeAsync always resolves the session, and a retried paymentToken POST (same invoiceNo +
/// idempotencyID) returns the first result instead of double-charging.</para>
/// </summary>
public sealed class TwoCTwoPAdapter : PspAdapterBase
{
    public TwoCTwoPAdapter(IHttpClientFactory httpClientFactory, IOptions<PspOptions> options)
        : base(httpClientFactory, options.Value)
    {
    }

    public override Code Psp => Code.TwoCTwoP;

    /// <summary>Card only today: the paymentToken request is built with a card payment channel, so a
    /// promptpay/installment request must be refused up-front instead of being charged as a card.</summary>
    public override IReadOnlySet<string> SupportedMethods { get; } =
        new HashSet<string>(StringComparer.Ordinal) { PaymentMethods.Card };

    private string BaseUrl => Options.UseSandbox
        ? Options.TwoCTwoP.SandboxBaseUrl
        : Options.TwoCTwoP.ProductionBaseUrl;

    public override async Task<PspCharge> CreateRedirectChargeAsync(
        Session session, Guid pspConnectionId, string secret, CancellationToken cancellationToken)
    {
        var creds = ParseSecret(secret);
        var invoiceNo = session.Id.ToString("N");

        var claims = JsonSerializer.Serialize(new
        {
            merchantID = creds.MerchantId,
            invoiceNo,
            description = $"Order {session.OrderId:N}",
            amount = decimal.Parse(FormatMajorUnitAmount(session.Amount), System.Globalization.CultureInfo.InvariantCulture),
            currencyCode = session.Amount.Currency,
            paymentChannel = new[] { PaymentChannelFor(session.Method) },
            frontendReturnUrl = Options.TwoCTwoP.FrontendReturnUrl,
            backendReturnUrl = WebhookUrlFor(pspConnectionId),
            idempotencyID = invoiceNo,
        });

        var responseJwt = await PostPayloadAsync("paymentToken", claims, creds.SecretKey, cancellationToken).ConfigureAwait(false);
        if (!TryReadVerifiedJwtHs256(responseJwt, creds.SecretKey, out var resp))
            throw new InvalidOperationException("2c2p paymentToken response failed signature verification.");

        var respCode = GetString(resp, "respCode");
        if (respCode != "0000")
            // A signature-verified decline is the PSP naming its own refusal: no payment token was issued, so
            // no charge exists and this session may be failed (REQ-7.5). The signature check above is what
            // makes that safe to trust; an UNVERIFIABLE response stays ambiguous.
            throw new PspRejectedException($"2c2p paymentToken declined (respCode {respCode}).");

        var webPaymentUrl = GetString(resp, "webPaymentUrl")
            ?? throw new InvalidOperationException("2c2p paymentToken response missing webPaymentUrl.");

        // invoiceNo is the durable correlation key, NOT the per-attempt paymentToken.
        return new PspCharge(invoiceNo, webPaymentUrl);
    }

    public override bool VerifyWebhook(string rawPayload, string signature, string secret)
    {
        if (string.IsNullOrWhiteSpace(rawPayload))
            return false;

        var creds = ParseSecret(secret);
        // 2C2P carries the signature INSIDE the body JWT (envelope {"payload": jwt}); the separate
        // `signature` arg is not part of its contract, so it is unused here.
        var jwt = ExtractPayloadJwt(rawPayload) ?? rawPayload;

        if (!TryReadVerifiedJwtHs256(jwt, creds.SecretKey, out var claims))
            return false;

        // Bind the notification to our merchant — a valid signature for a different merchant is not ours.
        return GetString(claims, "merchantID") == creds.MerchantId;
    }

    public override WebhookEvent ParseWebhook(string rawPayload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawPayload);

        var jwt = ExtractPayloadJwt(rawPayload) ?? rawPayload;
        var claims = ReadJwtPayloadUnverified(jwt);

        var invoiceNo = GetString(claims, "invoiceNo")
            ?? throw new InvalidOperationException("2c2p webhook missing invoiceNo.");
        var eventId = GetString(claims, "tranRef") ?? invoiceNo;
        // Same respCode -> status mapping as FetchChargeAsync (pending codes are Pending, not Failed). The
        // webhook status is advisory only — the handler re-fetches before any state transition.
        var status = MapRespCode(GetString(claims, "respCode"));

        return new WebhookEvent(eventId, invoiceNo, status);
    }

    public override async Task<PspChargeConfirmation> FetchChargeAsync(
        string externalChargeId, string secret, CancellationToken cancellationToken)
    {
        var creds = ParseSecret(secret);
        var claims = JsonSerializer.Serialize(new
        {
            merchantID = creds.MerchantId,
            invoiceNo = externalChargeId,
            locale = "en",
        });

        var responseJwt = await PostPayloadWithRetryAsync("paymentInquiry", claims, creds.SecretKey, cancellationToken).ConfigureAwait(false);
        if (!TryReadVerifiedJwtHs256(responseJwt, creds.SecretKey, out var resp))
            throw new InvalidOperationException("2c2p paymentInquiry response failed signature verification.");

        // paymentInquiry reports the collected amount in MAJOR units under the same field names the
        // paymentToken request used (amount + currencyCode). Read from the signature-verified claims only.
        return new PspChargeConfirmation(
            MapRespCode(GetString(resp, "respCode")),
            TryReadMajorUnitMoney(GetDecimal(resp, "amount"), GetString(resp, "currencyCode")));
    }

    /// <summary>Maps a canonical payment method to the 2C2P paymentChannel code it must be charged through.
    /// Card only today — the same truth <see cref="SupportedMethods"/> declares, so create-session has
    /// already refused everything else (REQ-6.2). A method that still reaches here is a wiring bug and must
    /// fail naming itself, never be substituted with the card channel: sending a customer who picked
    /// PromptPay to a card page is the silent mis-routing REQ-6.3/6.4 exist to stop.</summary>
    private static string PaymentChannelFor(string method) => method.Trim().ToLowerInvariant() switch
    {
        PaymentMethods.Card => "CC",
        _ => throw new PspRejectedException(
            $"2c2p adapter cannot honour payment method '{method}' — it declares card only."),
    };

    /// <summary>Maps a 2C2P respCode to the normalized status: "0000"=Paid, in-progress codes=Pending,
    /// everything else (declines/cancels/failures)=Failed.</summary>
    private static PspChargeStatus MapRespCode(string? respCode) => respCode switch
    {
        "0000" => PspChargeStatus.Paid,
        "0001" or "2001" or "4009" => PspChargeStatus.Pending,
        _ => PspChargeStatus.Failed,
    };

    // ---- helpers ----

    /// <summary>Rejected, not ambiguous: an unusable secret envelope stops us before any request is sent
    /// (REQ-7.5). The message never echoes the envelope.</summary>
    private static TwoCTwoPSecret ParseSecret(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
            throw new PspRejectedException("2c2p secret is empty.");

        try
        {
            return JsonSerializer.Deserialize<TwoCTwoPSecret>(secret, Json)
                ?? throw new PspRejectedException("2c2p secret envelope could not be parsed.");
        }
        catch (JsonException)
        {
            throw new PspRejectedException("2c2p secret envelope could not be parsed.");
        }
    }

    /// <summary>POSTs {"payload": jwt(claims)} to a v4.3 endpoint ONCE (charge-create is non-idempotent)
    /// and returns the response's inner JWT string.</summary>
    private async Task<string> PostPayloadAsync(string endpoint, string claimsJson, string secretKey, CancellationToken ct)
    {
        var jwt = EncodeJwtHs256(claimsJson, secretKey);
        using var request = BuildPayloadRequest(endpoint, jwt);
        var body = await SendOnceAsync(request, ct).ConfigureAwait(false);
        return ExtractPayloadJwt(body) ?? throw new InvalidOperationException($"2c2p {endpoint} response missing payload.");
    }

    /// <summary>POSTs to an IDEMPOTENT v4.3 read endpoint (paymentInquiry) with bounded retry.</summary>
    private async Task<string> PostPayloadWithRetryAsync(string endpoint, string claimsJson, string secretKey, CancellationToken ct)
    {
        var jwt = EncodeJwtHs256(claimsJson, secretKey);
        var body = await SendWithRetryAsync(() => BuildPayloadRequest(endpoint, jwt), ct).ConfigureAwait(false);
        return ExtractPayloadJwt(body) ?? throw new InvalidOperationException($"2c2p {endpoint} response missing payload.");
    }

    private HttpRequestMessage BuildPayloadRequest(string endpoint, string jwt)
    {
        var envelope = JsonSerializer.Serialize(new { payload = jwt });
        return new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/payment/4.3/{endpoint}")
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
        };
    }

    private static string? ExtractPayloadJwt(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            // Only a string payload is a JWT; a non-string value returns null (so a crafted {"payload":1}
            // body cannot throw InvalidOperationException out of GetString into the webhook endpoint).
            return GetString(doc.RootElement, "payload");
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
