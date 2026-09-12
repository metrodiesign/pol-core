using SharedKernel;

namespace Merchants.Domain.Users;

/// <summary>Maps a provider identity to exactly one <see cref="User"/>. Keyed by
/// <c>(Provider, Subject)</c> with a unique index so a returning user resolves to their record and a second
/// registration for the same subject is rejected. Control-plane child row (no merchant predicate).</summary>
public sealed class ExternalLogin : Entity<Guid>
{
    /// <summary>The identity provider slug. <see cref="Microsoft"/> is the only one that can be minted today;
    /// historical rows may carry a retired provider slug.</summary>
    public string Provider { get; private set; } = default!;

    /// <summary>The provider's stable subject (Entra <c>oid</c>).</summary>
    public string Subject { get; private set; } = default!;

    public Guid UserId { get; private set; }

    public const string Microsoft = "microsoft";

    private ExternalLogin() { }

    private ExternalLogin(Guid id, string provider, string subject, Guid merchantUserId) : base(id)
    {
        Provider = provider;
        Subject = subject;
        UserId = merchantUserId;
    }

    /// <summary>Links a provider subject to a <see cref="User"/> at registration. <paramref name="provider"/>
    /// defaults to <see cref="Microsoft"/>.</summary>
    public static ExternalLogin Create(string subject, Guid merchantUserId, string provider = Microsoft)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (merchantUserId == Guid.Empty)
            throw new ArgumentException("UserId is required.", nameof(merchantUserId));
        return new ExternalLogin(Guid.NewGuid(), provider.Trim(), subject.Trim(), merchantUserId);
    }
}
