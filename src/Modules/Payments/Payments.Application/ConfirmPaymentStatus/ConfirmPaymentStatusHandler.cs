using System.Text.Json;
using BuildingBlocks.Application;
using Mediator;
using Payments.Application.Confirmation;
using Payments.Application.Ports;

namespace Payments.Application.ConfirmPaymentStatus;

/// <summary>
/// The customer's side of the confirm line. It reads the ORDER first and short-circuits on a terminal one
/// (REQ-8.12): an order that is already Paid or Cancelled has nothing left to ask the PSP, and asking
/// anyway would spend a vault reveal + a PSP inquiry on every poll of a finished order — the reason the
/// endpoint's rate limit is as tight as it is.
///
/// Everything past that is <see cref="PaymentConfirmationService"/>, unchanged and unduplicated, so a
/// status check and a webhook that arrive for the same charge reach the same decision by construction. The
/// mapping down to four customer-facing values is where this handler earns its keep: three outcomes that
/// changed nothing (<see cref="ConfirmationOutcome.Duplicate"/>, <see cref="ConfirmationOutcome.AmountMismatch"/>,
/// and an inquiry we could not complete) all answer <c>pending</c> — never <c>paid</c>, because confirming a
/// payment we have not verified is the one wrong answer on this surface (REQ-8.7).
/// </summary>
public sealed class ConfirmPaymentStatusHandler
    : ICommandHandler<ConfirmPaymentStatusCommand, PaymentStatusResult>
{
    private readonly IPayableOrderReader _orders;
    private readonly ISessionRepository _sessions;
    private readonly PaymentConfirmationService _confirmation;

    public ConfirmPaymentStatusHandler(
        IPayableOrderReader orders,
        ISessionRepository sessions,
        PaymentConfirmationService confirmation)
    {
        _orders = orders;
        _sessions = sessions;
        _confirmation = confirmation;
    }

    public async ValueTask<PaymentStatusResult> Handle(
        ConfirmPaymentStatusCommand command,
        CancellationToken cancellationToken)
    {
        var order = await _orders.GetAsync(command.OrderId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Order {command.OrderId} not found.");

        // Answered from the order itself, with no session read and no PSP call (REQ-8.12).
        if (order.Status is PayableOrderStatus.Paid or PayableOrderStatus.Refunded)
            return PaymentStatusResult.Paid;
        if (order.Status is PayableOrderStatus.Cancelled)
            return PaymentStatusResult.Cancelled;
        if (order.Status is PayableOrderStatus.Failed or PayableOrderStatus.Expired)
            return PaymentStatusResult.Failed;

        // No chargeable attempt: nothing has been started, or the last one already ended and left the order
        // payable again. Both read as "not paid yet" to the customer, who can simply pay (REQ-8.12).
        // ponytail: a just-failed attempt therefore reports pending rather than failed for the customer who
        // polls after a webhook already retired it — upgrade path is a "latest session" read on the
        // repository, if the SPA ever needs to distinguish the two.
        var open = await _sessions.GetOpenForOrderAsync(command.OrderId, cancellationToken).ConfigureAwait(false);
        if (open is null)
            return PaymentStatusResult.Pending;

        ConfirmationOutcome outcome;
        try
        {
            outcome = await _confirmation.ConfirmAsync(open, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ambiguous) when (
            ambiguous is HttpRequestException or TaskCanceledException or JsonException or PspAmbiguousException
            && !cancellationToken.IsCancellationRequested)
        {
            // The confirmation service deliberately lets a failed inquiry through: the PSP may be holding
            // money we have not heard about. The customer is told to wait — never that it failed, which
            // would send them off to pay a second time. PspAmbiguousException is the adapter's own
            // classification of the same case (unverifiable/unreadable inquiry response, persistent 5xx);
            // a PspRejectedException or a plain InvalidOperationException here is NOT ambiguous — it is a
            // wiring/config fault that must surface as the 409 ops can see, not dissolve into pending.
            return PaymentStatusResult.Pending;
        }

        return outcome switch
        {
            ConfirmationOutcome.Paid or ConfirmationOutcome.AlreadyPaid => PaymentStatusResult.Paid,
            ConfirmationOutcome.Failed or ConfirmationOutcome.Expired =>
                PaymentStatusResult.Failed,
            _ => PaymentStatusResult.Pending,
        };
    }
}
