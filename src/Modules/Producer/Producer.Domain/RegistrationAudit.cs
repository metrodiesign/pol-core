using SharedKernel;

namespace Producer.Domain;

/// <summary>The audited producer identity actions (REQ-21.1). Constants so the writer and tests never drift on a string.</summary>
public static class RegistrationAuditAction
{
    public const string Registered = "registered";
    public const string Resubmitted = "resubmitted";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Suspended = "suspended";
}

/// <summary>
/// Append-only record of a sensitive producer identity/assignment change (REQ-21). Registration, approval,
/// rejection, and suspension are all traceable: <see cref="ActorSubject"/> is the acting admin's subject (NULL
/// for a self-service registration with no admin actor), <see cref="TargetSubject"/> is the producer acted on.
/// Control-plane (no tenant predicate); insert-only; never holds a secret, token, or ticket (REQ-21.3).
/// </summary>
public sealed class RegistrationAudit : Entity<Guid>
{
    public string Action { get; private set; } = default!;

    /// <summary>The acting admin's subject; NULL for an actor-less self-service registration.</summary>
    public string? ActorSubject { get; private set; }

    public string TargetSubject { get; private set; } = default!;

    /// <summary>The role assigned at approval; NULL for non-approval events.</summary>
    public string? Role { get; private set; }

    /// <summary>The admin's free-text rejection rationale (REQ-5.1); NULL for non-rejection events.</summary>
    public string? Reason { get; private set; }

    public Guid? TenantId { get; private set; }

    public string CorrelationId { get; private set; } = default!;

    public DateTime OccurredAt { get; private set; }

    private RegistrationAudit() { }

    private RegistrationAudit(Guid id, string action, string? actorSubject, string targetSubject, string? role,
        string? reason, Guid? tenantId, string correlationId, DateTime occurredAt) : base(id)
    {
        Action = action;
        ActorSubject = actorSubject;
        TargetSubject = targetSubject;
        Role = role;
        Reason = reason;
        TenantId = tenantId;
        CorrelationId = correlationId;
        OccurredAt = occurredAt;
    }

    /// <summary>Builds an audit row for one of <see cref="RegistrationAuditAction"/>.</summary>
    public static RegistrationAudit For(string action, string targetSubject, string correlationId, DateTime occurredAt,
        string? actorSubject = null, string? role = null, Guid? tenantId = null, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSubject);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        return new RegistrationAudit(Guid.NewGuid(), action, actorSubject, targetSubject, role, reason, tenantId,
            correlationId, occurredAt);
    }
}
