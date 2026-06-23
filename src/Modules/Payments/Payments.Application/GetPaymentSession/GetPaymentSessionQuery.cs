using BuildingBlocks.Application;
using Mediator;
using Payments.Domain;

namespace Payments.Application.GetPaymentSession;

/// <summary>Reads a single payment session by id. Tenant-scoped.</summary>
public sealed record GetPaymentSessionQuery(Guid PaymentSessionId)
    : IQuery<PaymentSessionView>, ITenantScoped;

/// <summary>A read-model projection of a <see cref="PaymentSession"/> for display/status polling.</summary>
public sealed record PaymentSessionView(
    Guid PaymentSessionId,
    Guid OrderId,
    Guid TenantId,
    long AmountMinorUnits,
    string Currency,
    string Method,
    PspCode Psp,
    PaymentStatus Status,
    string? PspExternalChargeId,
    string? RedirectUrl,
    DateTime CreatedAt,
    DateTime UpdatedAt);
