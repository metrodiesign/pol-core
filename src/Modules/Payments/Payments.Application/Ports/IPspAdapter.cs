using Payments.Domain;
using Payments.Domain.Psp;

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
    /// The canonical <see cref="PaymentMethods"/> codes this adapter can actually honour today. Distinct
    /// from a connection's <c>EnabledMethods</c>, which is the company's commercial arrangement with the
    /// PSP: a method may be commercially enabled while our adapter cannot yet drive it, and admitting it
    /// would silently charge the customer through a different channel. The intersection of the two is the
    /// real eligibility.
    /// </summary>
    IReadOnlySet<string> SupportedMethods { get; }

    /// <summary>
    /// Creates a hosted charge for the session and returns its external id + hosted redirect URL.
    /// <paramref name="secret"/> is revealed from the vault by the caller, used here, never logged.
    /// </summary>
    Task<PspCharge> CreateRedirectChargeAsync(Session session, string secret, CancellationToken cancellationToken);

    /// <summary>Verifies a webhook signature against the raw payload using the connection's secret.</summary>
    bool VerifyWebhook(string rawPayload, string signature, string secret);

    /// <summary>Server-to-server confirm of a charge's true status (fetch-to-confirm). Never trusts the webhook body alone.</summary>
    Task<PspChargeStatus> FetchChargeAsync(string externalChargeId, string secret, CancellationToken cancellationToken);

    /// <summary>Parses a verified webhook payload into the normalized <see cref="WebhookEvent"/>.</summary>
    WebhookEvent ParseWebhook(string rawPayload);
}
