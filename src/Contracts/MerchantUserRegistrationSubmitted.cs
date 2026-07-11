using Mediator;

namespace Contracts;

/// <summary>
/// MerchantUserRegistrationSubmitted v1 — emitted (via the transactional outbox) when a merchant-user registration is
/// submitted or resubmitted, so the Admin side learns of a pending merchant-user without a synchronous coupling
/// (producer-google-sso REQ-20). Enqueued in the SAME pol_admin transaction as the registration write by a
/// Merchants outbox writer (not the stock pol_app <c>EfOutbox</c>), stamped with a fixed platform/sentinel merchant
/// id (registration runs merchant-less). Published at-least-once; the Admin-side consumer is idempotent on
/// <see cref="MerchantUserId"/> and touches only the control-plane notice table (never a merchant-scoped table, so
/// the sentinel SESSION_CONTEXT cannot FILTER/BLOCK it).
/// </summary>
public sealed record MerchantUserRegistrationSubmitted(
    Guid MerchantUserId,
    string Subject,
    string Email,
    string? HostedDomain,
    string DisplayName,
    DateTime OccurredAt) : INotification
{
    public const string SchemaVersion = "v1";
}
