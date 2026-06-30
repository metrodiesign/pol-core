using SharedKernel;

namespace Producer.Domain;

/// <summary>The auth lifecycle events recorded in the append-only <c>ProducerAuthAudits</c> table (REQ-12/21).</summary>
public static class ProducerAuthEventType
{
    public const string LoginSuccess = "login-success";
    public const string Logout = "logout";
    public const string LogoutAll = "logout-all";
    public const string Rotated = "rotated";
    public const string FamilyRevokedReuse = "family-revoked-reuse";
    public const string AuthDenied = "auth-denied";
}

/// <summary>
/// An append-only auth audit row (REQ-12/21). Records ids, the Google subject, an event type, a non-sensitive
/// reason, and a correlation id; NEVER a secret, token, or raw session id (REQ-12.3). The ProducerAccount id is
/// OPTIONAL — an auth attempt can be denied before any account is resolved (REQ-12.4).
/// </summary>
// ponytail: DUPLICATE of Admin.Domain.AdminAuthAudit (AdminAccountId -> ProducerAccountId) — deliberate debt, do not refactor into a shared base.
public sealed class ProducerAuthAudit : Entity<Guid>
{
    public string EventType { get; private set; } = default!;
    public Guid? ProducerAccountId { get; private set; }
    public string? Subject { get; private set; }
    public string? Reason { get; private set; }
    public string CorrelationId { get; private set; } = default!;
    public DateTime OccurredAt { get; private set; }

    private ProducerAuthAudit() { } // EF materialisation

    private ProducerAuthAudit(Guid id, string eventType, Guid? producerAccountId, string? subject, string? reason,
        string correlationId, DateTime occurredAt) : base(id)
    {
        EventType = eventType;
        ProducerAccountId = producerAccountId;
        Subject = subject;
        Reason = reason;
        CorrelationId = correlationId;
        OccurredAt = occurredAt;
    }

    /// <summary>Builds an audit row. <paramref name="reason"/> must be a short, non-sensitive label (no secret,
    /// token, or raw session id — REQ-12.3). The ProducerAccount id is OPTIONAL (an auth attempt can be denied before
    /// any account is resolved, REQ-12.4).</summary>
    public static ProducerAuthAudit For(string eventType, string correlationId, DateTime occurredAt,
        Guid? producerAccountId = null, string? subject = null, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        return new ProducerAuthAudit(Guid.NewGuid(), eventType, producerAccountId, subject, reason, correlationId, occurredAt);
    }
}
