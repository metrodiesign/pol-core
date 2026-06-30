using SharedKernel;

namespace Producer.Domain;

/// <summary>Maps a Google identity to exactly one <see cref="ProducerAccount"/> (REQ-2). Keyed by
/// <c>(Provider, Subject)</c> with a unique index so a returning user resolves to their record and a second
/// registration for the same subject is rejected (REQ-2.1/4.6). Control-plane child row (no tenant predicate).</summary>
public sealed class ExternalLogin : Entity<Guid>
{
    /// <summary>The identity provider; <c>"google"</c> for this slice (REQ-2.2).</summary>
    public string Provider { get; private set; } = default!;

    /// <summary>The provider's stable subject (Google <c>sub</c>).</summary>
    public string Subject { get; private set; } = default!;

    public Guid ProducerAccountId { get; private set; }

    public const string Google = "google";

    private ExternalLogin() { }

    private ExternalLogin(Guid id, string provider, string subject, Guid producerAccountId) : base(id)
    {
        Provider = provider;
        Subject = subject;
        ProducerAccountId = producerAccountId;
    }

    /// <summary>Links a Google subject to a <see cref="ProducerAccount"/> at registration. <paramref name="provider"/>
    /// defaults to <see cref="Google"/>.</summary>
    public static ExternalLogin Create(string subject, Guid producerAccountId, string provider = Google)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (producerAccountId == Guid.Empty)
            throw new ArgumentException("ProducerAccountId is required.", nameof(producerAccountId));
        return new ExternalLogin(Guid.NewGuid(), provider.Trim(), subject.Trim(), producerAccountId);
    }
}
