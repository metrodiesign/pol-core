using System.Text.Json;
using BuildingBlocks.Application;
using Mediator;
using Payments.Application.Confirmation;
using Payments.Application.Ports;

namespace Payments.Application.ReleaseOpenSession;

/// <summary>
/// Order-cancel's half of the money path. It never decides anything itself: the session's fate comes from
/// <see cref="PaymentConfirmationService"/>, which fetches from the PSP before retiring any session that
/// carries a charge id, and the only outcomes that let a cancel proceed are the two that PROVE no money will
/// arrive — <see cref="ConfirmationOutcome.Expired"/> and <see cref="ConfirmationOutcome.Failed"/>. A session
/// that is still live (REQ-4.3: the customer may be on the PSP page right now), one the PSP has settled, and
/// one whose status we could not read are all refusals, because cancelling an order that money is about to
/// land on is unrecoverable from here.
/// </summary>
public sealed class ReleaseOpenSessionHandler : ICommandHandler<ReleaseOpenSessionCommand, Unit>
{
    private readonly ISessionRepository _sessions;
    private readonly PaymentConfirmationService _confirmation;

    public ReleaseOpenSessionHandler(ISessionRepository sessions, PaymentConfirmationService confirmation)
    {
        _sessions = sessions;
        _confirmation = confirmation;
    }

    public async ValueTask<Unit> Handle(ReleaseOpenSessionCommand command, CancellationToken cancellationToken)
    {
        // No chargeable session: nothing holds the order (REQ-4.1). Terminal sessions are already excluded here.
        var open = await _sessions.GetOpenForOrderAsync(command.OrderId, cancellationToken).ConfigureAwait(false);
        if (open is null)
            return Unit.Value;

        ConfirmationOutcome outcome;
        try
        {
            outcome = await _confirmation.ConfirmAsync(open, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ambiguous) when (
            ambiguous is HttpRequestException or TaskCanceledException or JsonException or PspAmbiguousException
            && !cancellationToken.IsCancellationRequested)
        {
            // The confirmation service deliberately does not catch a failed inquiry: a timeout/5xx/garbage
            // response means the PSP may be holding money we have not heard about. Reading that as "not paid"
            // is exactly how a paid order gets cancelled, so it answers 409 — the merchant retries later.
            // PspAmbiguousException is the adapter's own classification of that same case (unverifiable or
            // unreadable inquiry response, persistent 5xx).
            throw new ConflictException(
                $"Order {command.OrderId} has a payment session whose status could not be confirmed with the PSP.",
                ambiguous);
        }

        if (outcome is not (ConfirmationOutcome.Expired or ConfirmationOutcome.Failed))
            throw new ConflictException(
                $"Order {command.OrderId} has a payment session that could not be released ({outcome}).");

        return Unit.Value;
    }
}
