using BuildingBlocks.Application;
using Mediator;
using Payments.Domain;
using SharedKernel;

namespace Payments.Application.CreatePaymentSession;

/// <summary>
/// Creates a <see cref="PaymentStatus.Created"/> session bound to an order+amount+method+PSP up-front
/// (PLAN #15). Merchant-scoped: rejected by the merchant guard if no merchant is in context.
/// </summary>
public sealed record CreatePaymentSessionCommand(
    Guid OrderId,
    Guid MerchantId,
    Money Amount,
    string Method,
    PspCode Psp) : ICommand<CreatePaymentSessionResult>, IMerchantScoped;

/// <summary>The id of the newly created payment session.</summary>
public sealed record CreatePaymentSessionResult(Guid PaymentSessionId);
