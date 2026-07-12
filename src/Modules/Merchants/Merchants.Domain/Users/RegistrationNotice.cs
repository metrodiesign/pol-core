using SharedKernel;

namespace Merchants.Domain.Users;

/// <summary>
/// Control-plane "a merchant-user registration is awaiting approval" notice recorded by the Admin-side outbox
/// consumer. One row per registration — unique on <see cref="MerchantUserId"/> makes the consumer idempotent
/// under at-least-once delivery. No merchant predicate (control-plane): granted INSERT/SELECT to
/// <c>pol_worker</c> (the outbox dispatcher principal, NOT in <c>pol_rls_bypass</c>) and <c>pol_admin</c>,
/// never <c>pol_app</c>, so a sentinel SESSION_CONTEXT can neither FILTER nor BLOCK the write. The table
/// itself is created in raw SQL by the SecurityObjects migration; this entity + its EF config map onto it
/// for the consumer.
/// </summary>
public sealed class RegistrationNotice : Entity<Guid>
{
    public Guid MerchantUserId { get; private set; }
    public string Subject { get; private set; } = default!;
    public string Email { get; private set; } = default!;
    public string DisplayName { get; private set; } = default!;
    public string? HostedDomain { get; private set; }
    public DateTime OccurredAt { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private RegistrationNotice() { }

    private RegistrationNotice(Guid id, Guid merchantUserId, string subject, string email, string displayName,
        string? hostedDomain, DateTime occurredAt, DateTime createdAt) : base(id)
    {
        MerchantUserId = merchantUserId;
        Subject = subject;
        Email = email;
        DisplayName = displayName;
        HostedDomain = hostedDomain;
        OccurredAt = occurredAt;
        CreatedAt = createdAt;
    }

    /// <summary>Builds a notice for a submitted/resubmitted registration. <paramref name="occurredAt"/> is the
    /// event time; <paramref name="now"/> is when the notice row was recorded.</summary>
    public static RegistrationNotice For(Guid merchantUserId, string subject, string email, string displayName,
        string? hostedDomain, DateTime occurredAt, DateTime now)
    {
        if (merchantUserId == Guid.Empty)
            throw new ArgumentException("MerchantUserId is required.", nameof(merchantUserId));
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        return new RegistrationNotice(Guid.NewGuid(), merchantUserId, subject.Trim(), email.Trim(),
            displayName.Trim(), string.IsNullOrWhiteSpace(hostedDomain) ? null : hostedDomain.Trim(), occurredAt, now);
    }
}
