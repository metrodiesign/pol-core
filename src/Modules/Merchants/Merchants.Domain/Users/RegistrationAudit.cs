using SharedKernel;

namespace Merchants.Domain.Users;

/// <summary>The audited merchant-user identity actions. Constants so the writer and tests never drift on a string.</summary>
public static class RegistrationAuditAction
{
    public const string Registered = "registered";
    public const string Resubmitted = "resubmitted";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Suspended = "suspended";

    /// <summary>An admin viewed full (unmasked) PII via the registration-history endpoint's
    /// <c>?reveal=true</c> (registration-attempt-history REQ-3.5). Excluded from the history timeline —
    /// it records access, not a lifecycle event.</summary>
    public const string Revealed = "revealed";
}

/// <summary>
/// Append-only record of a sensitive merchant-user identity/assignment change. Registration, approval,
/// rejection, and suspension are all traceable: <see cref="ActorSubject"/> is the acting admin's subject (NULL
/// for a self-service registration with no admin actor), <see cref="TargetSubject"/> is the merchant user acted on.
/// Control-plane (no merchant predicate); insert-only; never holds a secret, token, or ticket.
/// </summary>
public sealed class RegistrationAudit : Entity<Guid>
{
    public string Action { get; private set; } = default!;

    /// <summary>The acting admin's subject; NULL for an actor-less self-service registration.</summary>
    public string? ActorSubject { get; private set; }

    public string TargetSubject { get; private set; } = default!;

    /// <summary>The role assigned at approval; NULL for non-approval events.</summary>
    public string? Role { get; private set; }

    /// <summary>The admin's free-text rejection rationale; NULL for non-rejection events.</summary>
    public string? Reason { get; private set; }

    public Guid? MerchantId { get; private set; }

    public string CorrelationId { get; private set; } = default!;

    public DateTime OccurredAt { get; private set; }

    private RegistrationAudit() { }

    private RegistrationAudit(Guid id, string action, string? actorSubject, string targetSubject, string? role,
        string? reason, Guid? merchantId, string correlationId, DateTime occurredAt) : base(id)
    {
        Action = action;
        ActorSubject = actorSubject;
        TargetSubject = targetSubject;
        Role = role;
        Reason = reason;
        MerchantId = merchantId;
        CorrelationId = correlationId;
        OccurredAt = occurredAt;
    }

    /// <summary>Builds an audit row for one of <see cref="RegistrationAuditAction"/>.</summary>
    public static RegistrationAudit For(string action, string targetSubject, string correlationId, DateTime occurredAt,
        string? actorSubject = null, string? role = null, Guid? merchantId = null, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSubject);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        return new RegistrationAudit(Guid.NewGuid(), action, actorSubject, targetSubject, role, reason, merchantId,
            correlationId, occurredAt);
    }
}
