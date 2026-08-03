using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;

namespace Payments.Infrastructure.Psp;

/// <summary>
/// Real Omise/Opn adapter. Redirect-only CARD flow: POST /charges with NO card data on our side; the
/// cardholder enters the card + 3DS on Omise's hosted authorize_uri. A deterministic Idempotency-Key makes
/// a retried POST return the same charge. Auth = HTTP Basic with the secret key (username=key, empty
/// password). The single revealed secret is a JSON envelope {secretKey}. The charge id (chrg_...) is the
/// one correlation key: returned by create, carried by the webhook (data.id), and queried by fetch.
/// <para>PromptPay (Payment Links+) is DEFERRED: a paid link produces a charge whose id differs from the
/// link/transaction id, so the create -> webhook(data.id=charge) -> fetch -> GetByExternalCharge correlation
/// cannot be made consistent without the verbatim Links+ webhook/charge mapping, which needs a sandbox to
/// confirm. Shipping it now would charge the customer and never fulfil the order. It throws until a
/// follow-up wires the link->charge correlation against the real API.</para>
/// <para>Webhook HMAC is DEFERRED: the Omise signing secret differs from the API secret and the signature
/// timestamp is not carried through the (rawPayload, signature, secret) seam, so VerifyWebhook only checks
/// well-formedness. The mandatory server-side fetch-to-confirm (the handler runs it before every MarkPaid)
/// is the authority and the webhook body's status is never trusted; the PR2 webhook rate limiter bounds
/// forged-id probe exposure.</para>
/// </summary>
public sealed class OmiseAdapter : PspAdapterBase
{
    public OmiseAdapter(IHttpClientFactory httpClientFactory, IOptions<PspOptions> options)
        : base(httpClientFactory, options.Value)
    {
    }

    public override Code Psp => Code.Omise;

    /// <summary>Card only today: PromptPay via Payment Links+ is deferred (see class summary) and
    /// installment was never wired, so both are refused up-front rather than at the charge call.</summary>
    public override IReadOnlySet<string> SupportedMethods { get; } =
        new HashSet<string>(StringComparer.Ordinal) { PaymentMethods.Card };

    /// <summary><paramref name="pspConnectionId"/> is unused here by design: Omise takes its webhook
    /// endpoint from the dashboard, not from the charge request, so the per-connection callback URL is an ops
    /// step in the deploy runbook rather than a request field (REQ-4.5).</summary>
    public override async Task<PspCharge> CreateRedirectChargeAsync(
        Session session, Guid pspConnectionId, string secret, CancellationToken cancellationToken)
    {
        var creds = ParseSecret(secret);
        GuardKeyEnvironment(creds.SecretKey);

        var method = session.Method.Trim().ToLowerInvariant();
        return method switch
        {
            "card" => await CreateCardChargeAsync(session, creds, cancellationToken).ConfigureAwait(false),
            // Rejected, not ambiguous: both refusals happen before any request is sent (REQ-7.5).
            "promptpay" => throw new PspRejectedException(
                "Omise PromptPay (Payment Links+) is deferred pending sandbox-verified link->charge correlation."),
            _ => throw new PspRejectedException($"Omise adapter does not support method '{session.Method}'."),
        };
    }

    private async Task<PspCharge> CreateCardChargeAsync(Session session, OmiseSecret creds, CancellationToken ct)
    {
        // No card/token/source is sent: Omise returns a pending charge with a hosted authorize_uri where
        // the cardholder enters their card and completes 3DS. No PAN ever touches us (PCI SAQ A).
        // ponytail: the exact required field set for the hosted-3DS charge is contract-unverified until the
        // sandbox smoke-test (real Omise may require a token/source) — adjust the form on key handoff.
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("amount", FormatMinorUnitAmount(session.Amount)),
            new KeyValuePair<string, string>("currency", session.Amount.Currency),
            new KeyValuePair<string, string>("return_uri", Options.Omise.ReturnUri),
            new KeyValuePair<string, string>("capture", "true"),
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Options.Omise.ApiBaseUrl}/charges")
        {
            Content = form,
        };
        request.Headers.Authorization = BasicAuth(creds.SecretKey);
        request.Headers.Add("Idempotency-Key", session.Id.ToString("N"));

        var body = await SendOnceAsync(request, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var id = GetString(root, "id") ?? throw new PspAmbiguousException("Omise charge response missing id.");
        var authorizeUri = GetString(root, "authorize_uri")
            ?? throw new PspAmbiguousException("Omise charge response missing authorize_uri (no hosted redirect).");

        return new PspCharge(id, authorizeUri);
    }

    public override bool VerifyWebhook(string rawPayload, string signature, string secret)
    {
        // HMAC deferred (see class summary) — this is a well-formedness gate only, NOT an authenticity
        // claim. The handler's fetch-to-confirm is the real authority and never trusts this body.
        if (string.IsNullOrWhiteSpace(rawPayload))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(rawPayload);
            var root = doc.RootElement;
            return GetString(root, "object") == "event" && !string.IsNullOrEmpty(GetString(root, "id"));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public override WebhookEvent ParseWebhook(string rawPayload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawPayload);

        using var doc = JsonDocument.Parse(rawPayload);
        var root = doc.RootElement;

        var eventId = GetString(root, "id") ?? throw new InvalidOperationException("Omise webhook missing event id.");
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Omise webhook missing data object.");

        var chargeId = GetString(data, "id") ?? throw new InvalidOperationException("Omise webhook missing data.id.");
        return new WebhookEvent(eventId, chargeId, MapStatus(GetString(data, "status")));
    }

    public override async Task<PspChargeConfirmation> FetchChargeAsync(
        string externalChargeId, string secret, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalChargeId);
        var creds = ParseSecret(secret);
        GuardKeyEnvironment(creds.SecretKey);

        var body = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{Options.Omise.ApiBaseUrl}/charges/{externalChargeId}");
            request.Headers.Authorization = BasicAuth(creds.SecretKey);
            return request;
        }, cancellationToken).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // GET /charges/{id} reports the collected amount in MINOR units (satang) alongside `currency`, the
        // same convention the charge request uses — scale it back to major units to compare against a Money.
        return new PspChargeConfirmation(
            MapStatus(GetString(root, "status")),
            TryReadMinorUnitMoney(GetDecimal(root, "amount"), GetString(root, "currency")));
    }

    // ---- helpers ----

    /// <summary>Rejected, not ambiguous: an unusable secret envelope stops us before any request is sent
    /// (REQ-7.5). The message never echoes the envelope.</summary>
    private static OmiseSecret ParseSecret(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
            throw new PspRejectedException("Omise secret is empty.");

        try
        {
            return JsonSerializer.Deserialize<OmiseSecret>(secret, Json)
                ?? throw new PspRejectedException("Omise secret envelope could not be parsed.");
        }
        catch (JsonException)
        {
            throw new PspRejectedException("Omise secret envelope could not be parsed.");
        }
    }

    /// <summary>Fails fast if the key's test/live prefix disagrees with UseSandbox — a sandbox config with
    /// a live key (or vice versa) is a latent double-charge/auth bug. Names the mismatch, never the key.
    /// Rejected, not ambiguous: it runs before the request goes out (REQ-7.5).</summary>
    private void GuardKeyEnvironment(string secretKey)
    {
        var isTestKey = secretKey.StartsWith("skey_test_", StringComparison.Ordinal);
        if (isTestKey != Options.UseSandbox)
            throw new PspRejectedException(
                $"Omise key environment mismatch: UseSandbox={Options.UseSandbox} but the secret key is {(isTestKey ? "test" : "live")}.");
    }

    private static AuthenticationHeaderValue BasicAuth(string secretKey) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(secretKey + ":")));

    private static PspChargeStatus MapStatus(string? status) => status switch
    {
        "successful" => PspChargeStatus.Paid,
        "failed" or "expired" or "reversed" => PspChargeStatus.Failed,
        // pending and any unknown -> Pending, never Failed.
        _ => PspChargeStatus.Pending,
    };
}
