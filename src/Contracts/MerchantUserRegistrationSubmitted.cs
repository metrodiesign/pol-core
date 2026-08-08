using Mediator;

namespace Contracts;

/// <summary>
/// MerchantUserRegistrationSubmitted v1 — emitted (via the transactional outbox) when a merchant-user registration is
/// submitted or resubmitted, so the Admin side learns of a pending merchant-user without a synchronous coupling
/// (producer-google-sso REQ-20). Enqueued in the SAME pol_admin transaction as the registration write by a
/// Merchants outbox writer (not the stock pol_app <c>EfOutbox</c>), stamped with a fixed platform/sentinel merchant
/// id (registration runs merchant-less). Published at-least-once; the Admin-side consumer is idempotent on
/// <see cref="UserId"/> and touches only the control-plane notice table (never a merchant-scoped table, so
/// the sentinel SESSION_CONTEXT cannot FILTER/BLOCK it).
/// </summary>
public sealed record MerchantUserRegistrationSubmitted(
    Guid UserId,
    DateTime OccurredAt) : INotification
{
    public const string SchemaVersion = "v1";
}

/// <summary>
/// Internal object-store lifecycle request for a KYC photo. It is persisted only in the merchant-user outbox;
/// no public API or registration-history projection exposes either object key. Delivery is at-least-once, so
/// both operations must be idempotent: promote the new staged object, then remove the replaced object if any.
/// </summary>
public sealed record KycPhotoLifecycleRequested(
    string NewObjectKey,
    string? OldObjectKey) : INotification
{
    public const string SchemaVersion = "v1";
}
