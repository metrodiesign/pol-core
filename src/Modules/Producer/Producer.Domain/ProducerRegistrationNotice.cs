using SharedKernel;

namespace Producer.Domain;

/// <summary>
/// Control-plane "a producer registration is awaiting approval" notice recorded by the Admin-side outbox consumer
/// (REQ-20.4). One row per registration — unique on <see cref="TenantUserId"/> makes the consumer idempotent under
/// at-least-once delivery. No tenant predicate (control-plane): granted INSERT/SELECT to <c>pol_worker</c> (the
/// outbox dispatcher principal, NOT in <c>pol_rls_bypass</c>) and <c>pol_admin</c>, never <c>pol_app</c> (S5), so a
/// sentinel-tenant SESSION_CONTEXT can neither FILTER nor BLOCK the write. The table itself is created in raw SQL
/// by <c>AddProducerIdentityTables</c>; this entity + its EF config map onto it for the consumer.
/// </summary>
public sealed class ProducerRegistrationNotice : Entity<Guid>
{
    public Guid TenantUserId { get; private set; }
    public string Subject { get; private set; } = default!;
    public string Email { get; private set; } = default!;
    public string DisplayName { get; private set; } = default!;
    public string? HostedDomain { get; private set; }
    public DateTime OccurredAt { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private ProducerRegistrationNotice() { }

    private ProducerRegistrationNotice(Guid id, Guid tenantUserId, string subject, string email, string displayName,
        string? hostedDomain, DateTime occurredAt, DateTime createdAt) : base(id)
    {
        TenantUserId = tenantUserId;
        Subject = subject;
        Email = email;
        DisplayName = displayName;
        HostedDomain = hostedDomain;
        OccurredAt = occurredAt;
        CreatedAt = createdAt;
    }

    /// <summary>Builds a notice for a submitted/resubmitted registration. <paramref name="occurredAt"/> is the
    /// event time; <paramref name="now"/> is when the notice row was recorded.</summary>
    public static ProducerRegistrationNotice For(Guid tenantUserId, string subject, string email, string displayName,
        string? hostedDomain, DateTime occurredAt, DateTime now)
    {
        if (tenantUserId == Guid.Empty)
            throw new ArgumentException("TenantUserId is required.", nameof(tenantUserId));
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        return new ProducerRegistrationNotice(Guid.NewGuid(), tenantUserId, subject.Trim(), email.Trim(),
            displayName.Trim(), string.IsNullOrWhiteSpace(hostedDomain) ? null : hostedDomain.Trim(), occurredAt, now);
    }
}
