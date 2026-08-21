using Admins.Application.Roles;
using Admins.Domain.Users;
using Mediator;
using SharedKernel;

namespace Admins.Application.Users;

/// <summary>
/// Runtime resolution of an authenticated admin provider identity <c>(Provider, Subject)</c> -> its ACTIVE <see cref="User"/> with
/// the accessible-merchant set materialized (REQ-6). The outcome distinguishes <see cref="ResolveOutcome.NotFound"/>
/// (no account — the host may bootstrap from the allowlist or bind an invite) from
/// <see cref="ResolveOutcome.Suspended"/> (deny, never re-provision — REQ-5.6/5.7). Runs under the
/// pol_admin (RLS-bypass) connection because admin tables are control-plane.
/// </summary>
public sealed record ResolveQuery(ProviderIdentity Identity) : IQuery<ResolveResult>;

public enum ResolveOutcome { Resolved, Suspended, NotFound }

/// <summary>An active admin's identity + reach, materialized once per request into <c>IAdminScope</c>.</summary>
public sealed record Resolution(Guid AdminId, string Email, Tier Tier, AccessibleMerchants Accessible)
{
    private static readonly IReadOnlySet<string> NoPermissions = new HashSet<string>();

    /// <summary>Effective action permissions — the union over the admin's ACTIVE roles (admin-role-rbac REQ-5).
    /// A non-positional init member with an empty default so the callback/bootstrap resolutions that do not carry
    /// permissions keep compiling against the four-argument positional ctor (B1).</summary>
    public IReadOnlySet<string> Permissions { get; init; } = NoPermissions;

    /// <summary>Authorization snapshot revalidated inside privileged write transactions.</summary>
    public long AuthorizationVersion { get; init; }
}

public sealed record ResolveResult(ResolveOutcome Outcome, Resolution? Resolution)
{
    public static readonly ResolveResult NotFound = new(ResolveOutcome.NotFound, null);
    public static readonly ResolveResult Suspended = new(ResolveOutcome.Suspended, null);
    public static ResolveResult Of(Resolution resolution) => new(ResolveOutcome.Resolved, resolution);
}

public sealed class ResolveHandler : IQueryHandler<ResolveQuery, ResolveResult>
{
    private readonly IUserRepository _admins;
    private readonly IRoleRepository _roles;

    public ResolveHandler(IUserRepository admins, IRoleRepository roles)
    {
        _admins = admins;
        _roles = roles;
    }

    public async ValueTask<ResolveResult> Handle(ResolveQuery query, CancellationToken cancellationToken)
    {
        var account = await _admins.GetByIdentityAsync(query.Identity, cancellationToken);
        if (account is null)
            return ResolveResult.NotFound;
        if (account.Status == UserStatus.Suspended)
            return ResolveResult.Suspended;

        var accessible = await ResolveAccessibleAsync(account, _admins, cancellationToken);
        var permissions = await _roles.ListEffectivePermissionsAsync(account.Id, cancellationToken);
        return ResolveResult.Of(
            new Resolution(account.Id, account.Email, account.Tier, accessible)
            {
                Permissions = permissions,
                AuthorizationVersion = account.AuthorizationVersion
            });
    }

    /// <summary>Super = unrestricted; Scoped = exactly the assigned set (REQ-6.1/6.2). The design's
    /// <c>IAdminDirectory</c> is folded into this single caller rather than introducing a separate port.</summary>
    internal static async Task<AccessibleMerchants> ResolveAccessibleAsync(
        User account, IUserRepository admins, CancellationToken cancellationToken) =>
        account.Tier == Tier.Super
            ? AccessibleMerchants.All
            : AccessibleMerchants.Of(await admins.ListAssignedMerchantIdsAsync(account.Id, cancellationToken));
}
