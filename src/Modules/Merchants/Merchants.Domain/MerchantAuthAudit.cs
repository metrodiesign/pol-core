using SharedKernel;

namespace Merchants.Domain;

/// <summary>The auth lifecycle events recorded in the append-only <c>MerchantAuthAudits</c> table.</summary>
public static class MerchantAuthEventType
{
    public const string LoginSuccess = "login-success";
    public const string Logout = "logout";
    public const string LogoutAll = "logout-all";
    public const string Rotated = "rotated";
    public const string FamilyRevokedReuse = "family-revoked-reuse";
    public const string AuthDenied = "auth-denied";
}

/// <summary>
/// An append-only auth audit row. Records ids, the Google subject, an event type, a non-sensitive
/// reason, and a correlation id; NEVER a secret, token, or raw session id. The MerchantUser id is
/// OPTIONAL — an auth attempt can be denied before any account is resolved.
/// </summary>
public sealed class MerchantAuthAudit : Entity<Guid>
{
    public string EventType { get; private set; } = default!;
    public Guid? MerchantUserId { get; private set; }
    public string? Subject { get; private set; }
    public string? Reason { get; private set; }
    public string CorrelationId { get; private set; } = default!;
    public DateTime OccurredAt { get; private set; }

    private MerchantAuthAudit() { } // EF materialisation

    private MerchantAuthAudit(Guid id, string eventType, Guid? merchantUserId, string? subject, string? reason,
        string correlationId, DateTime occurredAt) : base(id)
    {
        EventType = eventType;
        MerchantUserId = merchantUserId;
        Subject = subject;
        Reason = reason;
        CorrelationId = correlationId;
        OccurredAt = occurredAt;
    }

    /// <summary>Builds an audit row. <paramref name="reason"/> must be a short, non-sensitive label (no secret,
    /// token, or raw session id). The MerchantUser id is OPTIONAL (an auth attempt can be denied before
    /// any account is resolved).</summary>
    public static MerchantAuthAudit For(string eventType, string correlationId, DateTime occurredAt,
        Guid? merchantUserId = null, string? subject = null, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        return new MerchantAuthAudit(Guid.NewGuid(), eventType, merchantUserId, subject, reason, correlationId, occurredAt);
    }
}
