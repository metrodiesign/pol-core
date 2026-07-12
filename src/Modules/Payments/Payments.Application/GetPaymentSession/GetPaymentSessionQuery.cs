using BuildingBlocks.Application;
using Mediator;
using Payments.Domain;
using SharedKernel;

namespace Payments.Application.GetPaymentSession;

/// <summary>Reads a single payment session by id. Merchant-scoped.</summary>
public sealed record GetPaymentSessionQuery(Guid PaymentSessionId)
    : IQuery<PaymentSessionView>, IMerchantScoped;

/// <summary>A read-model projection of a <see cref="PaymentSession"/> for display/status polling.</summary>
public sealed record PaymentSessionView(
    Guid PaymentSessionId,
    Guid OrderId,
    Guid MerchantId,
    Money Amount,
    string Method,
    PspCode Psp,
    PaymentStatus Status,
    string? PspExternalChargeId,
    string? RedirectUrl,
    DateTime CreatedAt,
    DateTime UpdatedAt);
