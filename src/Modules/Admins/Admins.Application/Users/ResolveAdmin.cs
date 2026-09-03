using Admins.Application.Roles;
using Admins.Domain.Users;
using Mediator;
using SharedKernel;

namespace Admins.Application.Users;

/// <summary>
/// Historical non-Microsoft resolution of <c>(Provider, Subject)</c> to an ACTIVE <see cref="User"/> with
/// the accessible-merchant set materialized (REQ-6). Microsoft callbacks use
/// <see cref="ResolveMicrosoftAdminCommand"/> exclusively. The outcome distinguishes
/// <see cref="ResolveOutcome.NotFound"/> from <see cref="ResolveOutcome.Suspended"/> (deny, never
/// re-provision — REQ-5.6/5.7). Admin tables live in control-plane persistence without merchant query filters.
/// </summary>
public sealed record ResolveQuery(ProviderIdentity Identity) : IQuery<ResolveResult>;

/// <summary>The four <c>EmployeeProfile*</c> members are tier0-graph-employee-profile denials (REQ-1/3/4/5); hosts must
/// switch over this enum EXHAUSTIVELY (no discard arm) so a new member can never fall through to "not-provisioned".</summary>
public enum ResolveOutcome
{
    Resolved, Suspended, NotFound, IdentityConflict,
    EmployeeProfileMissing, EmployeeProfileInvalid, EmployeeProfileUnmapped, EmployeeProfileUnavailable
}

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

/// <summary><paramref name="DenialReason"/> = the internal audit reason when it differs from the browser reason
/// (tier0-graph-employee-profile REQ-2.17/3.19): <c>employee-mismatch</c>/<c>employee-taken</c> behind an
/// <see cref="ResolveOutcome.IdentityConflict"/>, <c>hr-source-unavailable</c> behind
/// <see cref="ResolveOutcome.EmployeeProfileUnavailable"/>. Null = audit the browser reason (pre-existing outcomes).</summary>
public sealed record ResolveResult(ResolveOutcome Outcome, Resolution? Resolution, string? DenialReason = null)
{
    public const string EmployeeMismatchReason = "employee-mismatch";
    public const string EmployeeTakenReason = "employee-taken";
    public const string HrSourceUnavailableReason = "hr-source-unavailable";

    public static readonly ResolveResult NotFound = new(ResolveOutcome.NotFound, null);
    public static readonly ResolveResult Suspended = new(ResolveOutcome.Suspended, null);
    public static readonly ResolveResult IdentityConflict = new(ResolveOutcome.IdentityConflict, null);
    public static readonly ResolveResult EmployeeProfileMissing = new(ResolveOutcome.EmployeeProfileMissing, null);
    public static readonly ResolveResult EmployeeProfileInvalid = new(ResolveOutcome.EmployeeProfileInvalid, null);
    public static readonly ResolveResult EmployeeProfileUnmapped = new(ResolveOutcome.EmployeeProfileUnmapped, null);
    public static readonly ResolveResult HrSourceUnavailable =
        new(ResolveOutcome.EmployeeProfileUnavailable, null, HrSourceUnavailableReason);
    public static ResolveResult EmployeeConflict(string reason) => new(ResolveOutcome.IdentityConflict, null, reason);
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
        if (string.Equals(query.Identity.Provider, User.MicrosoftProvider, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Microsoft identities require tenant-aware resolution.", nameof(query));

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
