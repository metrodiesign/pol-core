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
/// rejection, and suspension are all traceable: <see cref="TargetUserId"/> is the canonical target key (subjects
/// are no longer unique across providers — microsoft-oidc-ciam-alignment REQ-4.8), <see cref="ActorAdminId"/> the
/// canonical actor for admin-performed actions (REQ-4.9). <see cref="ActorSubject"/>/<see cref="TargetSubject"/>
/// remain display-only. Control-plane (no merchant predicate); insert-only; never holds a secret, token, or ticket.
/// </summary>
public sealed class RegistrationAudit : Entity<Guid>
{
    public string Action { get; private set; } = default!;

    /// <summary>The merchant user acted on — the canonical read key of the history timeline (REQ-4.8).</summary>
    public Guid TargetUserId { get; private set; }

    /// <summary>The acting admin's internal id; NULL only for actor-less self-service actions (REQ-4.9).</summary>
    public Guid? ActorAdminId { get; private set; }

    /// <summary>Display-only — the acting admin's subject; NULL for an actor-less self-service registration.</summary>
    public string? ActorSubject { get; private set; }

    /// <summary>Display-only — the target's subject at the time of the action.</summary>
    public string TargetSubject { get; private set; } = default!;

    /// <summary>The role assigned at approval; NULL for non-approval events.</summary>
    public string? Role { get; private set; }

    /// <summary>The admin's free-text rejection rationale; NULL for non-rejection events.</summary>
    public string? Reason { get; private set; }

    public Guid? MerchantId { get; private set; }

    public string CorrelationId { get; private set; } = default!;

    public DateTime OccurredAt { get; private set; }

    private RegistrationAudit() { }

    private RegistrationAudit(Guid id, string action, Guid targetUserId, Guid? actorAdminId, string? actorSubject,
        string targetSubject, string? role, string? reason, Guid? merchantId, string correlationId,
        DateTime occurredAt) : base(id)
    {
        Action = action;
        TargetUserId = targetUserId;
        ActorAdminId = actorAdminId;
        ActorSubject = actorSubject;
        TargetSubject = targetSubject;
        Role = role;
        Reason = reason;
        MerchantId = merchantId;
        CorrelationId = correlationId;
        OccurredAt = occurredAt;
    }

    /// <summary>Builds an audit row for one of <see cref="RegistrationAuditAction"/>. <paramref name="actorAdminId"/>
    /// is REQUIRED for admin-performed actions (approve/reject/reveal/suspend) — null only for self-service.</summary>
    public static RegistrationAudit For(string action, Guid targetUserId, string targetSubject, string correlationId,
        DateTime occurredAt, Guid? actorAdminId = null, string? actorSubject = null, string? role = null,
        Guid? merchantId = null, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSubject);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        if (targetUserId == Guid.Empty)
            throw new ArgumentException("TargetUserId is required.", nameof(targetUserId));
        return new RegistrationAudit(Guid.NewGuid(), action, targetUserId, actorAdminId, actorSubject, targetSubject,
            role, reason, merchantId, correlationId, occurredAt);
    }
}
