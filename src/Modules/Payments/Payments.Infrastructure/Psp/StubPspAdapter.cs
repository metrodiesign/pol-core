using System.Text.Json;
using Payments.Application.Ports;
using Payments.Domain;

namespace Payments.Infrastructure.Psp;

/// <summary>
/// Shared deterministic behaviour for the stub PSP adapters.
/// <para>
/// ponytail: NO real HTTP calls yet. CreateRedirectChargeAsync mints a deterministic hosted redirect
/// URL (https://redirect.example/{psp}/{externalId}); VerifyWebhook does a deterministic constant-time
/// check against a secret-derived token ("valid-" + secret hint); FetchChargeAsync echoes the parsed
/// webhook status (so the confirmed-Paid path runs end-to-end in tests). Upgrade path: replace each
/// override with the PSP's real charge-create / signature-HMAC / charge-fetch over IHttpClientFactory,
/// keeping the verbatim external field names. The redirect-only constraint (PCI SAQ A) and the
/// fetch-to-confirm contract must be preserved — these stubs already model both.
/// </para>
/// </summary>
public abstract class StubPspAdapter : IPspAdapter
{
    /// <inheritdoc />
    public abstract PspCode Psp { get; }

    /// <summary>Stable hint derived from the secret used to build the deterministic webhook token.
    /// Never the secret itself — only its short suffix, mirroring how real PSPs publish a key id.</summary>
    private static string Hint(string secret) =>
        secret.Length <= 4 ? secret : secret[^4..];

    /// <summary>The deterministic signature the stub accepts as valid for a given secret.</summary>
    internal static string ExpectedSignature(string secret) => "valid-" + Hint(secret);

    /// <inheritdoc />
    public Task<PspCharge> CreateRedirectChargeAsync(
        PaymentSession session,
        string secret,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        // Deterministic external charge id + HOSTED redirect URL only (no card data, no QR, no iframe).
        var externalChargeId = $"{Psp.ToCode()}_chg_{session.Id:N}";
        var redirectUrl = $"https://redirect.example/{Psp.ToCode()}/{externalChargeId}";
        return Task.FromResult(new PspCharge(externalChargeId, redirectUrl));
    }

    /// <inheritdoc />
    public bool VerifyWebhook(string rawPayload, string signature, string secret)
    {
        ArgumentNullException.ThrowIfNull(rawPayload);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        if (string.IsNullOrEmpty(signature))
            return false;

        // Constant-time compare against the deterministic token (modelling an HMAC verify).
        var expected = ExpectedSignature(secret);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(signature),
            System.Text.Encoding.UTF8.GetBytes(expected));
    }

    /// <inheritdoc />
    public Task<PspChargeStatus> FetchChargeAsync(
        string externalChargeId,
        string secret,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalChargeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        // ponytail: stub fetch-to-confirm always confirms Paid so the happy path runs end-to-end.
        return Task.FromResult(PspChargeStatus.Paid);
    }

    /// <inheritdoc />
    public WebhookEvent ParseWebhook(string rawPayload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawPayload);

        var payload = JsonSerializer.Deserialize<PspWebhookPayload>(rawPayload)
            ?? throw new InvalidOperationException("Webhook payload could not be parsed.");

        return new WebhookEvent(payload.EventId, payload.ExternalChargeId, payload.NormalizedStatus);
    }
}
