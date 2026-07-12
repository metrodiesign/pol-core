using SharedKernel;

namespace Admins.Domain.Users;

/// <summary>The auth lifecycle events recorded in the append-only <c>PlatformAuthAudits</c> table (REQ-12).</summary>
public static class AuthEventType
{
    public const string LoginSuccess = "login-success";
    public const string Logout = "logout";
    public const string LogoutAll = "logout-all";
    public const string Rotated = "rotated";
    public const string FamilyRevokedReuse = "family-revoked-reuse";
    public const string AuthDenied = "auth-denied";
}

/// <summary>
/// An append-only auth audit row (REQ-12). SEPARATE from <see cref="Audit"/> because an auth event
/// may have NO resolved admin id (a state mismatch or not-allowlisted denial) — which <c>Audit</c>
/// forbids (it requires a non-empty actor). Records ids, the Google subject, an event type, a non-sensitive
/// reason, and a correlation id; NEVER a secret, token, or raw session id (REQ-12.3).
/// </summary>
public sealed class AuthAudit : Entity<Guid>
{
    public string EventType { get; private set; } = default!;
    public Guid? PlatformUserId { get; private set; }
    public string? Subject { get; private set; }
    public string? Reason { get; private set; }
    public string CorrelationId { get; private set; } = default!;
    public DateTime OccurredAt { get; private set; }

    private AuthAudit() { } // EF materialisation

    private AuthAudit(Guid id, string eventType, Guid? adminAccountId, string? subject, string? reason,
        string correlationId, DateTime occurredAt) : base(id)
    {
        EventType = eventType;
        PlatformUserId = adminAccountId;
        Subject = subject;
        Reason = reason;
        CorrelationId = correlationId;
        OccurredAt = occurredAt;
    }

    /// <summary>Builds an audit row. <paramref name="reason"/> must be a short, non-sensitive label (no secret,
    /// token, or raw session id — REQ-12.3/12.4). Unlike <see cref="Audit"/>, the admin id is
    /// OPTIONAL (an auth attempt can be denied before any admin is resolved).</summary>
    public static AuthAudit For(string eventType, string correlationId, DateTime occurredAt,
        Guid? adminAccountId = null, string? subject = null, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        return new AuthAudit(Guid.NewGuid(), eventType, adminAccountId, subject, reason, correlationId, occurredAt);
    }
}
