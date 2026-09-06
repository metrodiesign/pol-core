using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Application.Ports;

/// <summary>
/// The redirect-only seam to one PSP (PCI SAQ A). Adapters return a HOSTED redirect URL only — no
/// card-number fields, no hosted-fields/iframe, no display QR. The webhook is the source of truth:
/// a signature is verified, then the charge is re-fetched server-side before any state transition.
/// </summary>
public interface IPspAdapter
{
    /// <summary>Which PSP this adapter speaks to.</summary>
    Code Psp { get; }

    /// <summary>
    /// The canonical <see cref="PaymentMethods"/> codes this adapter has PROVEN against the PSP sandbox
    /// (merchant-psp-settings REQ-5.4/5.11): a method is listed only once its redirect -> webhook ->
    /// fetch-to-confirm contract has sandbox evidence, and is removed from nothing else. Distinct from the
    /// catalog (<c>cfg.PaymentProviderMethods</c>, what the PSP offers) and from an account method row
    /// (what the merchant has enabled): the control plane requires all three, so implemented-but-unverified
    /// code can never charge a customer (REQ-5.5 fail-closed, REQ-5.12 opens it by editing this set).
    /// </summary>
    IReadOnlySet<string> SupportedMethods { get; }

    /// <summary>Runs a read-only authenticated provider probe against <paramref name="environment"/>'s
    /// endpoint family. No fake success and no charge creation (REQ-7.1/7.2).</summary>
    Task<PspProbeResult> TestConnectionAsync(
        string secret, PspEnvironment environment, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This PSP adapter does not implement a connection probe.");

    /// <summary>The per-connection backend-notification URL a PSP must call back on
    /// (<c>{PublicBaseUrl}/api/v1/webhooks/{pspConnectionId}</c>). Safe to display: carries no secret
    /// (REQ-11.1). The default is the route alone (no origin) for adapters/doubles that do not know the
    /// public base URL; the real adapters override it with the absolute URL.</summary>
    string CallbackUrlFor(Guid pspConnectionId) => $"/api/v1/webhooks/{pspConnectionId:D}";

    /// <summary>
    /// Creates a hosted charge for the session and returns its external id + hosted redirect URL.
    /// <paramref name="secret"/> is revealed from the vault by the caller, used here, never logged.
    /// <paramref name="pspConnectionId"/> is the connection actually being charged through: it is what the
    /// backend-notification URL a PSP calls back on must carry (<c>/api/v1/webhooks/{pspConnectionId}</c>),
    /// so a confirmation reaches the handler and stays isolated to that company. Only the id is passed —
    /// an adapter has no business seeing the connection's secret ref or enabled methods.
    /// <paramref name="environment"/> selects the sandbox/live endpoint family PER CALL (never from host
    /// config) so merchants in different environments share one runtime (REQ-2.3/2.4).
    /// </summary>
    Task<PspCharge> CreateRedirectChargeAsync(
        Session session, Guid pspConnectionId, string secret, PspEnvironment environment,
        CancellationToken cancellationToken);

    /// <summary>Verifies a webhook signature against the raw payload using the connection's secret.</summary>
    bool VerifyWebhook(string rawPayload, string signature, string secret);

    /// <summary>Server-to-server confirm of a charge's true status AND the amount the PSP reports having
    /// collected (fetch-to-confirm). Never trusts the webhook body alone. The amount is what lets the
    /// caller check the collection against the order that backs it — see
    /// <see cref="PspChargeConfirmation"/> for why it is nullable.</summary>
    Task<PspChargeConfirmation> FetchChargeAsync(
        string externalChargeId, string secret, PspEnvironment environment, CancellationToken cancellationToken);

    /// <summary>Parses a verified webhook payload into the normalized <see cref="WebhookEvent"/>.</summary>
    WebhookEvent ParseWebhook(string rawPayload);
}

public sealed record PspProbeResult(string Code, string Detail);
