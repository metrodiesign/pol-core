using SharedKernel;

namespace Identity.Domain;

/// <summary>
/// Maps an external IdP identity (provider + subject) to exactly one <see cref="TenantUser"/>. Google is
/// the only provider in this slice. The (Provider, Subject) pair is unique, so a subject links to one user.
/// </summary>
public sealed class ExternalLogin : Entity<Guid>
{
    public const string GoogleProvider = "google";

    public string Provider { get; private set; } = default!;

    public string Subject { get; private set; } = default!;

    public Guid TenantUserId { get; private set; }

    private ExternalLogin() { }

    private ExternalLogin(Guid id, string provider, string subject, Guid tenantUserId) : base(id)
    {
        Provider = provider;
        Subject = subject;
        TenantUserId = tenantUserId;
    }

    public static ExternalLogin Create(string provider, string subject, Guid tenantUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        if (tenantUserId == Guid.Empty)
            throw new ArgumentException("TenantUserId is required.", nameof(tenantUserId));

        return new ExternalLogin(Guid.NewGuid(), provider.Trim().ToLowerInvariant(), subject.Trim(), tenantUserId);
    }

    public static ExternalLogin Google(string subject, Guid tenantUserId) =>
        Create(GoogleProvider, subject, tenantUserId);
}
