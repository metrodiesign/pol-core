using BuildingBlocks.Application;
using Mediator;
using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Application.GetSession;

/// <summary>Reads a single payment session by id. Merchant-scoped.</summary>
public sealed record GetSessionQuery(Guid PaymentSessionId)
    : IQuery<SessionView>, IMerchantScoped;

/// <summary>A read-model projection of a <see cref="Session"/> for display/status polling.</summary>
public sealed record SessionView(
    Guid PaymentSessionId,
    Guid OrderId,
    Guid MerchantId,
    Money Amount,
    string Method,
    Code Psp,
    SessionStatus Status,
    string? PspExternalChargeId,
    string? RedirectUrl,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    [property: System.Text.Json.Serialization.JsonIgnore] long Version = 0);
