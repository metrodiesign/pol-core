using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Infrastructure.Psp;

/// <summary>
/// Shared, PSP-agnostic primitives for the real HTTP adapters: pooled HttpClient access via
/// IHttpClientFactory (so the adapters stay DI singletons without socket exhaustion), JSON, a
/// minimal alg-pinned HS256 JWT codec (2C2P), HMAC-SHA256 + constant-time compare, major-unit amount
/// formatting, and two HTTP send paths — a SINGLE-SHOT one for the non-idempotent charge-create POST
/// (a blind retry there could double-charge) and a bounded-retry one for the idempotent fetch GET.
/// Protocol-specific bodies (2C2P JWT vs Omise Basic-auth + Payment Links+) stay as per-adapter
/// overrides; only the genuinely-shared crypto/IO lives here.
/// </summary>
public abstract class PspAdapterBase : IPspAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;

    protected PspAdapterBase(IHttpClientFactory httpClientFactory, PspOptions options)
    {
        _httpClientFactory = httpClientFactory;
        Options = options;
    }

    protected PspOptions Options { get; }

    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public abstract Code Psp { get; }

    /// <summary>Abstract, not a shared default: a base default would hand its capability set to a future
    /// adapter that cannot honour those methods, which is the exact silent-substitution this declaration
    /// exists to prevent. Every adapter states its own truth next to the code that implements it.</summary>
    public abstract IReadOnlySet<string> SupportedMethods { get; }

    public abstract Task<PspCharge> CreateRedirectChargeAsync(
        Session session, Guid pspConnectionId, string secret, CancellationToken cancellationToken);

    public abstract bool VerifyWebhook(string rawPayload, string signature, string secret);

    public abstract Task<PspChargeStatus> FetchChargeAsync(
        string externalChargeId, string secret, CancellationToken cancellationToken);

    public abstract WebhookEvent ParseWebhook(string rawPayload);

    /// <summary>A pooled, handler-rotated client for this PSP (named by its code). Cheap to create per call.</summary>
    protected HttpClient CreateClient() => _httpClientFactory.CreateClient(Psp.ToCode());

    // ---- backend-notification URL ----

    /// <summary>The per-connection backend-notification URL this charge must call back on:
    /// <c>{PublicBaseUrl}/api/v1/webhooks/{pspConnectionId}</c>. Derived, never configured per deployment —
    /// a single global callback URL cannot carry the connection id that the webhook route (and with it the
    /// per-company isolation) requires, so it could only ever be right for one connection (REQ-4.1).</summary>
    protected string WebhookUrlFor(Guid pspConnectionId) =>
        $"{Options.PublicBaseUrl.TrimEnd('/')}/api/v1/webhooks/{pspConnectionId:D}";

    // ---- amount ----

    /// <summary>Renders <see cref="Money"/> as a major-unit decimal string (e.g. THB 250.09 -> "250.09",
    /// JPY 5000 -> "5000") at the currency's ISO 4217 minor-unit scale. Invariant culture so the decimal
    /// separator is always '.'.</summary>
    /// <exception cref="ArgumentException">See <see cref="RequireRepresentableDigits"/>.</exception>
    protected static string FormatMajorUnitAmount(Money amount)
    {
        var digits = RequireRepresentableDigits(amount);
        return amount.Amount.ToString("F" + digits, CultureInfo.InvariantCulture);
    }

    /// <summary>Renders <see cref="Money"/> as a minor-unit integer string (e.g. THB 250.09 -> "25009",
    /// JPY 5000 -> "5000") for PSPs (Omise) whose API takes the smallest currency unit.</summary>
    /// <exception cref="ArgumentException">See <see cref="RequireRepresentableDigits"/>.</exception>
    protected static string FormatMinorUnitAmount(Money amount)
    {
        var digits = RequireRepresentableDigits(amount);
        var scale = (decimal)Math.Pow(10, digits);
        var minorUnits = decimal.Round(amount.Amount * scale, 0, MidpointRounding.AwayFromZero);
        return minorUnits.ToString("F0", CultureInfo.InvariantCulture);
    }

    /// <summary>Guards that <paramref name="amount"/> has no precision beyond its currency's ISO 4217
    /// minor-unit scale. <see cref="Money"/> allows up to 4 decimal places, which can exceed what a PSP's
    /// wire format (or a zero-decimal currency like JPY) can represent — silently rounding here would
    /// charge the PSP a different amount than the unrounded one stored on the session/order (Codex review
    /// #79, pullrequestreview-4678411626).</summary>
    private static int RequireRepresentableDigits(Money amount)
    {
        var digits = Iso4217.MinorUnitDigits(amount.Currency);
        if (amount.Amount != decimal.Round(amount.Amount, digits))
            throw new ArgumentException(
                $"{amount.Currency} amount {amount.Amount} is not representable at its {digits}-decimal minor unit.",
                nameof(amount));
        return digits;
    }

    // ---- HS256 JWT (2C2P): alg-pinned, symmetric ----

    /// <summary>Encodes a compact HS256 JWT over <paramref name="claimsJson"/> signed with the UTF-8 bytes
    /// of <paramref name="secret"/>. The header alg is fixed to HS256.</summary>
    protected static string EncodeJwtHs256(string claimsJson, string secret)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        var payload = Base64Url(Encoding.UTF8.GetBytes(claimsJson));
        var signingInput = header + "." + payload;
        var signature = Base64Url(HmacSha256(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signingInput)));
        return signingInput + "." + signature;
    }

    /// <summary>Verifies a compact JWT's HS256 signature with <paramref name="secret"/> and returns its
    /// decoded payload claims. Pins alg to HS256 (rejects "none"/alg-confusion) and uses a constant-time
    /// signature compare. Returns false on any malformation or signature mismatch.</summary>
    protected static bool TryReadVerifiedJwtHs256(string jwt, string secret, out JsonElement claims)
    {
        claims = default;
        if (string.IsNullOrWhiteSpace(jwt))
            return false;

        var parts = jwt.Split('.');
        if (parts.Length != 3)
            return false;

        // Pin the algorithm from the header before trusting anything else.
        try
        {
            using var headerDoc = JsonDocument.Parse(Base64UrlDecode(parts[0]));
            // alg must be a STRING equal to HS256 — guard ValueKind first so a crafted non-string alg
            // ({"alg":1}) returns false instead of throwing InvalidOperationException out of GetString.
            if (!headerDoc.RootElement.TryGetProperty("alg", out var alg)
                || alg.ValueKind != JsonValueKind.String
                || !string.Equals(alg.GetString(), "HS256", StringComparison.Ordinal))
                return false;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }

        var expected = Base64Url(HmacSha256(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(parts[0] + "." + parts[1])));
        if (!ConstantTimeEquals(expected, parts[2]))
            return false;

        try
        {
            using var payloadDoc = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            claims = payloadDoc.RootElement.Clone();
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    /// <summary>Decodes a compact JWT's payload claims WITHOUT verifying the signature. Only safe to call
    /// after the same token has already passed <see cref="TryReadVerifiedJwtHs256"/> (the webhook handler
    /// verifies before it parses). Throws on a malformed token.</summary>
    protected static JsonElement ReadJwtPayloadUnverified(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
            throw new InvalidOperationException("Malformed JWT.");
        using var doc = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        return doc.RootElement.Clone();
    }

    /// <summary>Reads a string-valued JSON property, or null if it is absent or not a JSON string.</summary>
    protected static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // ---- crypto primitives ----

    protected static byte[] HmacSha256(byte[] key, byte[] data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data);
    }

    /// <summary>Constant-time string compare over UTF-8 bytes (no early-out on length or content).</summary>
    protected static bool ConstantTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    // ---- HTTP send paths ----

    /// <summary>Sends a request EXACTLY ONCE (no retry) and returns the body. Used for the non-idempotent
    /// charge-create POST: a blind retry after a timeout could create a second charge, so a failure here
    /// surfaces to the caller rather than being retried.</summary>
    protected async Task<string> SendOnceAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{Psp.ToCode()} returned HTTP {(int)response.StatusCode}.");
        return body;
    }

    /// <summary>Sends an IDEMPOTENT request (the fetch-to-confirm GET) with bounded retry on transient
    /// failures (5xx/408/429/transport), exponential backoff + jitter. <paramref name="requestFactory"/>
    /// is invoked per attempt because an HttpRequestMessage cannot be resent.</summary>
    protected async Task<string> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken, int maxRetries = 2)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var client = CreateClient();
                using var request = requestFactory();
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (IsTransient(response.StatusCode) && attempt < maxRetries)
                {
                    await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"{Psp.ToCode()} returned HTTP {(int)response.StatusCode}.");
                return body;
            }
            catch (HttpRequestException) when (attempt < maxRetries && !cancellationToken.IsCancellationRequested)
            {
                await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (attempt < maxRetries && !cancellationToken.IsCancellationRequested)
            {
                // A per-attempt timeout (not a caller cancellation) — safe to retry an idempotent GET.
                await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    private static Task BackoffAsync(int attempt, CancellationToken cancellationToken)
    {
        // ponytail: tiny expo backoff + jitter; bounded retries (<=2) so the worst case is sub-second.
        var baseMs = 120 * (1 << attempt);
        var jitter = Random.Shared.Next(0, 80);
        return Task.Delay(baseMs + jitter, cancellationToken);
    }
}
