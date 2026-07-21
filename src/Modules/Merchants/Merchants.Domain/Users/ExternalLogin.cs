using SharedKernel;

namespace Merchants.Domain.Users;

/// <summary>Maps a Google identity to exactly one <see cref="User"/>. Keyed by
/// <c>(Provider, Subject)</c> with a unique index so a returning user resolves to their record and a second
/// registration for the same subject is rejected. Control-plane child row (no merchant predicate).</summary>
public sealed class ExternalLogin : Entity<Guid>
{
    /// <summary>The identity provider slug: <see cref="Google"/> or <see cref="Microsoft"/>.</summary>
    public string Provider { get; private set; } = default!;

    /// <summary>The provider's stable subject (Google <c>sub</c> / Entra <c>oid</c>).</summary>
    public string Subject { get; private set; } = default!;

    public Guid MerchantUserId { get; private set; }

    public const string Google = "google";
    public const string Microsoft = "microsoft";

    private ExternalLogin() { }

    private ExternalLogin(Guid id, string provider, string subject, Guid merchantUserId) : base(id)
    {
        Provider = provider;
        Subject = subject;
        MerchantUserId = merchantUserId;
    }

    /// <summary>Links a Google subject to a <see cref="User"/> at registration. <paramref name="provider"/>
    /// defaults to <see cref="Google"/>.</summary>
    public static ExternalLogin Create(string subject, Guid merchantUserId, string provider = Google)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (merchantUserId == Guid.Empty)
            throw new ArgumentException("MerchantUserId is required.", nameof(merchantUserId));
        return new ExternalLogin(Guid.NewGuid(), provider.Trim(), subject.Trim(), merchantUserId);
    }
}
