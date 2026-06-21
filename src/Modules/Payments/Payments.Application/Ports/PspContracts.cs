namespace Payments.Application.Ports;

/// <summary>A hosted charge created at the PSP: its external id plus the hosted redirect URL the
/// browser is sent to. Redirect-only — never card data, hosted fields, or an offline QR.</summary>
public sealed record PspCharge(string ExternalChargeId, string RedirectUrl);

/// <summary>The normalized status of a PSP charge as confirmed by a server-to-server fetch.</summary>
public enum PspChargeStatus
{
    Pending = 0,
    Paid = 1,
    Failed = 2,
}

/// <summary>A parsed PSP webhook: the event id (for idempotency), the external charge it refers to,
/// and the normalized status it claims. The claim is re-confirmed by a fetch before it is trusted.</summary>
public sealed record WebhookEvent(string EventId, string ExternalChargeId, PspChargeStatus Status);
