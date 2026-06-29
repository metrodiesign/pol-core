using Mediator;

namespace Contracts;

/// <summary>
/// TenantUserRegistrationSubmitted v1 — emitted (via the transactional outbox) when a producer registration is
/// submitted or resubmitted, so the Admin side learns of a pending producer without a synchronous coupling
/// (producer-google-sso REQ-20). Enqueued in the SAME pol_admin transaction as the registration write by a
/// Producer outbox writer (not the stock pol_app <c>EfOutbox</c>), stamped with a fixed platform/sentinel tenant
/// id (registration runs tenant-less). Published at-least-once; the Admin-side consumer is idempotent on
/// <see cref="TenantUserId"/> and touches only the control-plane notice table (never a tenant-scoped table, so
/// the sentinel SESSION_CONTEXT cannot FILTER/BLOCK it).
/// </summary>
public sealed record TenantUserRegistrationSubmitted(
    Guid TenantUserId,
    string Subject,
    string Email,
    string? HostedDomain,
    string DisplayName,
    DateTime OccurredAt) : INotification
{
    public const string SchemaVersion = "v1";
}
