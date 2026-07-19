using Admins.Application;
using Admins.Application.Users;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Iam.Domain.Permissions;
using Mediator;
using Merchants.Application.GetMerchant;
using Persistence.MerchantRuntime.Merchants;

namespace Api.Admins;

/// <summary>Merchant checks the Admin module needs, over the shared pol_app connection (task 8.5.7 — no more
/// pol_admin bypass principal, "1 principal"). Delegates to <see cref="IMerchantDirectoryReader"/>
/// (<c>Persistence.MerchantRuntime</c>) — the host may not resolve <c>MerchantRuntimeDbContext</c> directly
/// (design.md "Assembly split + Api host boundary"), so this class stays a thin adapter onto that public port,
/// living in the host so <c>Admins.Application</c> needs no Merchant dependency (mirrors
/// <see cref="MerchantDirectory"/>'s original role for Identity).</summary>
internal sealed class MerchantDirectory : IAdminMerchantDirectory
{
    private readonly IMerchantDirectoryReader _reader;

    public MerchantDirectory(IMerchantDirectoryReader reader) => _reader = reader;

    public Task<bool> IsActiveMerchantAsync(Guid merchantId, CancellationToken cancellationToken) =>
        _reader.IsActiveMerchantAsync(merchantId, cancellationToken);

    public Task<IReadOnlyDictionary<Guid, string>> GetCodesByIdsAsync(
        IReadOnlySet<Guid> merchantIds, CancellationToken cancellationToken) =>
        _reader.GetCodesByIdsAsync(merchantIds, cancellationToken);

    public Task<Guid?> GetIdByCodeAsync(string code, CancellationToken cancellationToken) =>
        _reader.GetIdByCodeAsync(code, cancellationToken);
}

/// <summary>Per-request holder of the resolved admin (REQ-6.3). The admin authentication handler
/// (<see cref="PlatformUserSessionAuthenticationHandler"/>) calls <see cref="Set"/> once per request; readers consume
/// <see cref="IAdminScope"/>.</summary>
internal sealed class AdminScope : IAdminScope
{
    private Resolution? _current;

    public bool IsBound => _current is not null;
    public Resolution Current => _current ?? throw new InvalidOperationException("No admin is bound to this request.");
    public AccessibleMerchants Accessible => Current.Accessible;

    public void Set(Resolution resolution) => _current = resolution;
}

/// <summary>The ONLY seam through which an admin handler may read a merchant-scoped business table cross-merchant
/// (REQ-7.1). pol_admin bypasses RLS at the DB, so this seam IS the floor: it applies the
/// <see cref="IAdminScope"/> filter (Super = unrestricted; Scoped = only assigned merchants). Architecture.Tests
/// forbids any other host type from sending <see cref="GetMerchantQuery"/> directly (REQ-7.2).</summary>
internal interface IAdminQuery
{
    Task<MerchantView?> GetMerchantByCodeAsync(string code, CancellationToken cancellationToken);
}

internal sealed class AdminQuery : IAdminQuery
{
    private readonly IMediator _mediator;
    private readonly IAdminScope _scope;
    private readonly IAdminMerchantDirectory _merchants;

    public AdminQuery(IMediator mediator, IAdminScope scope, IAdminMerchantDirectory merchants)
    {
        _mediator = mediator;
        _scope = scope;
        _merchants = merchants;
    }

    public async Task<MerchantView?> GetMerchantByCodeAsync(string code, CancellationToken cancellationToken)
    {
        // Apply the accessible-merchant floor BEFORE loading the projection (REQ-7.1): for a Scoped admin, resolve
        // the code to an id with a bare lookup and gate on the accessible set first. An unknown code OR a merchant
        // outside the set returns null without the full bypass projection (incl. PSP-metadata parsing) ever
        // running — fail-closed, no existence leak, and no handler-side error can surface for an inaccessible
        // merchant. A Super admin is unrestricted, so it skips the gate and loads directly (REQ-7.3 / 8.5).
        if (!_scope.Accessible.IsUnrestricted)
        {
            var id = await _merchants.GetIdByCodeAsync(code, cancellationToken);
            if (id is null || !_scope.Accessible.Allows(id.Value))
                return null;
        }

        try
        {
            return await _mediator.Send(new GetMerchantQuery(code), cancellationToken);
        }
        catch (NotFoundException)
        {
            return null; // unknown code -> 404
        }
    }
}

/// <summary>Tier gate for Super-only admin actions (REQ-8.1): 403 unless the resolved <c>admin_tier</c> claim
/// is in the allowed set. Mirrors <see cref="MerchantRoleAuthorization"/>.</summary>
internal static class TierAuthorization
{
    public static RouteHandlerBuilder RequirePlatformUserTier(this RouteHandlerBuilder builder, params Tier[] allowed)
    {
        var allowedNames = allowed.Select(t => t.ToString()).ToArray();
        return builder.AddEndpointFilter(async (context, next) =>
            IsTierAllowed(context.HttpContext.User.FindFirst("admin_tier")?.Value, allowedNames)
                ? await next(context)
                : Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Your admin tier is not permitted for this action."));
    }

    internal static bool IsTierAllowed(string? tierClaim, string[] allowedTierNames) =>
        tierClaim is not null && allowedTierNames.Contains(tierClaim, StringComparer.Ordinal);
}

// RequiredPermission/PermissionAuthorization/PermissionParity moved to Api.Iam (rf2 REQ-4/5) — one gate +
// one boot parity guard now serve both the admin and merchant-user consoles.

internal static class HostWiring
{
    /// <summary>Binds the host-only Admin seams (task 8.5.7 — every repository/session-store/audit seam now
    /// binds directly to <c>ControlPlaneDbContext</c> via <c>AddControlPlanePersistence</c>, shared unkeyed by
    /// both hosts; only the pieces that genuinely need the host's own types stay here).</summary>
    public static IServiceCollection AddAdminIdentity(this IServiceCollection services)
    {
        services.AddScoped<IAdminMerchantDirectory, MerchantDirectory>();

        // Admin BFF session cookie service (stateless, singleton).
        services.AddSingleton<SessionCookies>();

        services.AddScoped<AdminScope>();
        services.AddScoped<IAdminScope>(sp => sp.GetRequiredService<AdminScope>());
        services.AddScoped<IAdminQuery, AdminQuery>();
        return services;
    }
}
