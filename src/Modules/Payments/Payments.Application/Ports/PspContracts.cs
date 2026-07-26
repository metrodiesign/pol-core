using SharedKernel;

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

/// <summary>
/// A server-confirmed charge: the normalized status plus the amount the PSP reports having collected,
/// when its response carries one.
/// <para><see cref="Amount"/> is nullable and a null means "the PSP did not report an amount — confirm on
/// STATUS ALONE", never "the PSP collected zero" (REQ-8.3). The per-path response contract for the amount
/// field is not sandbox-verified, so an absent, mistyped, or unrepresentable value must degrade to
/// status-only confirmation: failing closed on an unverified contract would stop confirming real payments,
/// leaving customers charged and their orders unfulfilled.</para>
/// </summary>
public sealed record PspChargeConfirmation(PspChargeStatus Status, Money? Amount);

/// <summary>A parsed PSP webhook: the event id (for idempotency), the external charge it refers to,
/// and the normalized status it claims. The claim is re-confirmed by a fetch before it is trusted.</summary>
public sealed record WebhookEvent(string EventId, string ExternalChargeId, PspChargeStatus Status);
