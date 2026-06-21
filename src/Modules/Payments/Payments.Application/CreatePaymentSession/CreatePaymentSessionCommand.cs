using BuildingBlocks.Application;
using Mediator;
using Payments.Domain;

namespace Payments.Application.CreatePaymentSession;

/// <summary>
/// Creates a <see cref="PaymentStatus.Created"/> session bound to an order+amount+method+PSP up-front
/// (PLAN #15). Tenant-scoped: rejected by the tenant guard if no tenant is in context.
/// </summary>
public sealed record CreatePaymentSessionCommand(
    Guid OrderId,
    Guid TenantId,
    long AmountMinorUnits,
    string Currency,
    string Method,
    PspCode Psp) : ICommand<CreatePaymentSessionResult>, ITenantScoped;

/// <summary>The id of the newly created payment session.</summary>
public sealed record CreatePaymentSessionResult(Guid PaymentSessionId);
