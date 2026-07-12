using BuildingBlocks.Application;
using Mediator;
using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Application.CreateSession;

/// <summary>
/// Creates a <see cref="SessionStatus.Created"/> session bound to an order+amount+method+PSP up-front
/// (PLAN #15). Merchant-scoped: rejected by the merchant guard if no merchant is in context.
/// </summary>
public sealed record CreateSessionCommand(
    Guid OrderId,
    Guid MerchantId,
    Money Amount,
    string Method,
    Code Psp) : ICommand<CreateSessionResult>, IMerchantScoped;

/// <summary>The id of the newly created payment session.</summary>
public sealed record CreateSessionResult(Guid PaymentSessionId);
