using Mediator;

namespace Contracts;

/// <summary>Protected invitation delivery payload; raw capability is never persisted.</summary>
public sealed record MerchantUserInvitationDeliveryRequested(
    Guid InvitationId,
    string Email,
    string ProtectedToken,
    DateTime ExpiresAt) : INotification;
