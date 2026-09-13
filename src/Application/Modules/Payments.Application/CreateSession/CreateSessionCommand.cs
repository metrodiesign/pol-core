using BuildingBlocks.Application;
using Mediator;
using Payments.Domain;

namespace Payments.Application.CreateSession;

/// <summary>
/// Creates a <see cref="SessionStatus.Created"/> session bound to an order+amount+method up-front
/// (PLAN #15). Merchant-scoped: rejected by the merchant guard if no merchant is in context.
/// There is deliberately NO amount here: the platform is a payment channel, never the party that decides
/// what a charge is worth, so the handler reads the amount off the order row itself. There is deliberately
/// NO PSP either: the server is the sole routing authority (REQ-6.8-6.18), so the handler selects the
/// connection through <c>IPaymentRouteSelector</c> — no caller, browser or console, supplies it.
/// </summary>
public sealed record CreateSessionCommand(
    Guid OrderId,
    Guid MerchantId,
    string Method) : ICommand<CreateSessionResult>, IMerchantScoped;

/// <summary>The id of the newly created payment session.</summary>
public sealed record CreateSessionResult(Guid PaymentSessionId);
