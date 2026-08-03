using BuildingBlocks.Application;
using Mediator;

namespace Payments.Application.ConfirmPaymentStatus;

/// <summary>
/// What the customer's own status check answers. Four values, deliberately coarser than
/// <c>SessionStatus</c>: the customer is told whether their money landed, not which attempt or which
/// internal state the platform is in.
/// </summary>
public enum PaymentStatusResult
{
    /// <summary>Nothing decided yet — no attempt, an attempt still live, or an answer we could not trust
    /// (an unreachable PSP, a claim another caller holds, an amount that does not match). Poll again.</summary>
    Pending = 0,

    /// <summary>The money is in: either the order is already Paid, or the PSP confirmed this attempt.</summary>
    Paid = 1,

    /// <summary>This attempt is over without payment — refused, or aged out. The order can be paid again.</summary>
    Failed = 2,

    /// <summary>The merchant cancelled the order; there is nothing left to pay.</summary>
    Cancelled = 3,
}

/// <summary>
/// Asks — and, where the PSP answers definitively, SETTLES — the payment state of an order for the
/// anonymous customer who is holding its link. A command, not a query: verifying with the PSP is exactly
/// what turns a live attempt into Paid/Failed/Expired, on the same confirm line as the webhook.
/// Merchant-scoped; the host binds the order's merchant before sending it (the customer has no session).
/// </summary>
public sealed record ConfirmPaymentStatusCommand(Guid OrderId)
    : ICommand<PaymentStatusResult>, IMerchantScoped;
