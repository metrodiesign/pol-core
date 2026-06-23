using SharedKernel;

namespace Identity.Domain;

/// <summary>
/// A short-lived, single-use handle issued AFTER Google sign-in to a caller with no existing
/// <see cref="ExternalLogin"/>, so they can submit the registration form (reference 2.5). It carries the
/// verified identity from the validated Google token and is NOT a session. Single-use + expiry are
/// enforced by <see cref="Consume"/> failing closed on a replayed or expired ticket.
/// </summary>
public sealed class RegistrationTicket : Entity<Guid>
{
    public string Subject { get; private set; } = default!;

    public string Email { get; private set; } = default!;

    /// <summary>Hosted-domain (`hd`) claim captured at issue time, when present.</summary>
    public string? HostedDomain { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime ExpiresAtUtc { get; private set; }

    /// <summary>Null until consumed; set once, then the ticket can never be replayed.</summary>
    public DateTime? UsedAtUtc { get; private set; }

    private RegistrationTicket() { }

    private RegistrationTicket(Guid id, string subject, string email, string? hostedDomain,
        DateTime createdAtUtc, DateTime expiresAtUtc) : base(id)
    {
        Subject = subject;
        Email = email;
        HostedDomain = hostedDomain;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        UsedAtUtc = null;
    }

    public static RegistrationTicket Issue(string subject, string email, string? hostedDomain,
        DateTime createdAtUtc, TimeSpan timeToLive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        if (timeToLive <= TimeSpan.Zero)
            throw new ArgumentException("Ticket TTL must be positive.", nameof(timeToLive));

        var hd = string.IsNullOrWhiteSpace(hostedDomain) ? null : hostedDomain.Trim();
        return new RegistrationTicket(Guid.NewGuid(), subject.Trim(), email.Trim(), hd,
            createdAtUtc, createdAtUtc + timeToLive);
    }

    public bool IsUsable(DateTime now) => UsedAtUtc is null && now < ExpiresAtUtc;

    /// <summary>Marks the ticket used. Throws if already used (replay) or expired — fail closed.</summary>
    public void Consume(DateTime now)
    {
        if (UsedAtUtc is not null)
            throw new InvalidOperationException("The registration ticket has already been used.");
        if (now >= ExpiresAtUtc)
            throw new InvalidOperationException("The registration ticket has expired.");

        UsedAtUtc = now;
    }
}
