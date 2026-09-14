using Admins.Domain.Users;

namespace Admins.Application.Users;

/// <summary>An active admin's identity + reach, materialized once per request into <c>IAdminScope</c>.</summary>
public sealed record Resolution(Guid AdminId, string? Email, Tier Tier, AccessibleMerchants Accessible)
{
    private static readonly IReadOnlySet<string> NoPermissions = new HashSet<string>();

    /// <summary>Effective action permissions — the union over the admin's ACTIVE roles (admin-role-rbac REQ-5).
    /// A non-positional init member with an empty default so the callback/bootstrap resolutions that do not carry
    /// permissions keep compiling against the four-argument positional ctor (B1).</summary>
    public IReadOnlySet<string> Permissions { get; init; } = NoPermissions;

    /// <summary>Authorization snapshot revalidated inside privileged write transactions.</summary>
    public long AuthorizationVersion { get; init; }
}
