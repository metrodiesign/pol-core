using SharedKernel;

namespace Producer.Domain;

/// <summary>
/// The server-side authority backing a short-lived, single-use registration handle (REQ-3). The wire ticket is a
/// signed+encrypted self-contained token the client carries and returns at submission; this row, keyed by the
/// ticket's <see cref="Entity{TId}.Id"/>, is the replay guard — a valid signature alone is replayable until expiry,
/// so <see cref="UsedAt"/> (set once, atomically) is authoritative (REQ-3.4). Carries the identity captured ONLY
/// from the validated id_token (REQ-3.1). Control-plane (no tenant predicate). A ticket is NOT a session (REQ-3.5).
/// </summary>
public sealed class RegistrationTicket : AggregateRoot<Guid>
{
    public string Subject { get; private set; } = default!;
    public string Email { get; private set; } = default!;

    /// <summary>The Google hosted domain (<c>hd</c>) claim, when present.</summary>
    public string? HostedDomain { get; private set; }

    public TicketPurpose Purpose { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }

    /// <summary>When the ticket was consumed; NULL while unused. The single-use replay guard (REQ-3.4).</summary>
    public DateTime? UsedAt { get; private set; }

    private RegistrationTicket() { }

    private RegistrationTicket(Guid id, string subject, string email, string? hostedDomain, TicketPurpose purpose,
        DateTime createdAt, DateTime expiresAt) : base(id)
    {
        Subject = subject;
        Email = email;
        HostedDomain = hostedDomain;
        Purpose = purpose;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    /// <summary>Issues a ticket valid for <paramref name="ttl"/> from <paramref name="now"/> (REQ-3.1/3.2). Identity
    /// fields come from the verified id_token, never a form body.</summary>
    public static RegistrationTicket Issue(string subject, string email, string? hostedDomain, TicketPurpose purpose,
        DateTime now, TimeSpan ttl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "Ticket TTL must be positive.");
        var hd = string.IsNullOrWhiteSpace(hostedDomain) ? null : hostedDomain.Trim();
        return new RegistrationTicket(Guid.NewGuid(), subject.Trim(), email.Trim(), hd, purpose, now, now + ttl);
    }

    /// <summary>Single-use consume guard (REQ-3.3/3.6): succeeds and stamps <see cref="UsedAt"/> only if the ticket
    /// is unused and not yet expired at <paramref name="now"/>; otherwise returns <c>false</c> and changes nothing,
    /// so a ticket can never be replayed. (The DB write is additionally guarded by a conditional UPDATE in the
    /// registration transaction — REQ-3.3/4.1.)</summary>
    public bool Consume(DateTime now)
    {
        if (UsedAt is not null || now >= ExpiresAt)
            return false;
        UsedAt = now;
        return true;
    }
}
