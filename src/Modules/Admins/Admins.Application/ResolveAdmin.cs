using Admins.Domain;
using Mediator;

namespace Admins.Application.ResolveAdmin;

/// <summary>
/// Runtime resolution of an authenticated admin Google subject -> its ACTIVE <see cref="PlatformUser"/> with
/// the accessible-merchant set materialized (REQ-6). The outcome distinguishes <see cref="AdminResolveOutcome.NotFound"/>
/// (no account — the host may bootstrap from the allowlist or bind an invite) from
/// <see cref="AdminResolveOutcome.Suspended"/> (deny, never re-provision — REQ-5.6/5.7). Runs under the
/// pol_admin (RLS-bypass) connection because admin tables are control-plane.
/// </summary>
public sealed record ResolveAdminQuery(string Subject) : IQuery<AdminResolveResult>;

public enum AdminResolveOutcome { Resolved, Suspended, NotFound }

/// <summary>An active admin's identity + reach, materialized once per request into <c>IAdminScope</c>.</summary>
public sealed record AdminResolution(Guid AdminId, string Email, PlatformUserTier Tier, AccessibleMerchants Accessible)
{
    private static readonly IReadOnlySet<string> NoPermissions = new HashSet<string>();

    /// <summary>Effective action permissions — the union over the admin's ACTIVE roles (admin-role-rbac REQ-5).
    /// A non-positional init member with an empty default so the callback/bootstrap resolutions that do not carry
    /// permissions keep compiling against the four-argument positional ctor (B1).</summary>
    public IReadOnlySet<string> Permissions { get; init; } = NoPermissions;
}

public sealed record AdminResolveResult(AdminResolveOutcome Outcome, AdminResolution? Resolution)
{
    public static readonly AdminResolveResult NotFound = new(AdminResolveOutcome.NotFound, null);
    public static readonly AdminResolveResult Suspended = new(AdminResolveOutcome.Suspended, null);
    public static AdminResolveResult Of(AdminResolution resolution) => new(AdminResolveOutcome.Resolved, resolution);
}

public sealed class ResolveAdminHandler : IQueryHandler<ResolveAdminQuery, AdminResolveResult>
{
    private readonly IPlatformUserRepository _admins;
    private readonly IAdminRoleRepository _roles;

    public ResolveAdminHandler(IPlatformUserRepository admins, IAdminRoleRepository roles)
    {
        _admins = admins;
        _roles = roles;
    }

    public async ValueTask<AdminResolveResult> Handle(ResolveAdminQuery query, CancellationToken cancellationToken)
    {
        var account = await _admins.GetBySubjectAsync(query.Subject, cancellationToken);
        if (account is null)
            return AdminResolveResult.NotFound;
        if (account.Status == AdminStatus.Suspended)
            return AdminResolveResult.Suspended;

        var accessible = await ResolveAccessibleAsync(account, _admins, cancellationToken);
        var permissions = await _roles.ListEffectivePermissionsAsync(account.Id, cancellationToken);
        return AdminResolveResult.Of(
            new AdminResolution(account.Id, account.Email, account.Tier, accessible) { Permissions = permissions });
    }

    /// <summary>Super = unrestricted; Scoped = exactly the assigned set (REQ-6.1/6.2). The design's
    /// <c>IAdminDirectory</c> is folded into this single caller rather than introducing a separate port.</summary>
    internal static async Task<AccessibleMerchants> ResolveAccessibleAsync(
        PlatformUser account, IPlatformUserRepository admins, CancellationToken cancellationToken) =>
        account.Tier == PlatformUserTier.Super
            ? AccessibleMerchants.All
            : AccessibleMerchants.Of(await admins.ListAssignedMerchantIdsAsync(account.Id, cancellationToken));
}
