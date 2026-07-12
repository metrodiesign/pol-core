namespace Admins.Application.Users;

/// <summary>
/// The set of merchants an admin may reach, resolved once per request (REQ-6). A Super is
/// <see cref="IsUnrestricted"/> (all merchants); a Scoped admin carries exactly its assigned set.
/// <see cref="Allows"/> is the single decision the scoped app-layer floor (REQ-7) calls.
/// </summary>
public readonly record struct AccessibleMerchants(bool IsUnrestricted, IReadOnlySet<Guid> Merchants)
{
    private static readonly IReadOnlySet<Guid> None = new HashSet<Guid>();

    /// <summary>Unrestricted reach (a Super admin).</summary>
    public static AccessibleMerchants All { get; } = new(true, None);

    /// <summary>Exactly the assigned set (a Scoped admin).</summary>
    public static AccessibleMerchants Of(IReadOnlySet<Guid> merchants) => new(false, merchants);

    public bool Allows(Guid merchantId) => IsUnrestricted || Merchants.Contains(merchantId);
}
