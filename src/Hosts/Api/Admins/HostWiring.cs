using Admins.Application;
using Admins.Application.Roles;
using Admins.Application.Users;
using Admins.Domain.Users;
using Admins.Infrastructure.Persistence;
using Admins.Infrastructure.Persistence.Roles;
using Admins.Infrastructure.Persistence.Users;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Iam.Domain.Permissions;
using Mediator;
using MasterData.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Merchants.Application.GetMerchant;
using Merchants.Domain;

namespace Api.Admins;

/// <summary>Merchant checks the Admin module needs, over the pol_admin (bypass) connection. Lives in the host so
/// Admins.Application needs no Merchant dependency (mirrors <see cref="MerchantDirectory"/> for Identity).</summary>
internal sealed class MerchantDirectory : IAdminMerchantDirectory
{
    private readonly PolDbContext _admin;

    public MerchantDirectory(PolDbContext admin) => _admin = admin;

    public Task<bool> IsActiveMerchantAsync(Guid merchantId, CancellationToken cancellationToken) =>
        _admin.Set<Merchant>()
            .AnyAsync(t => t.Id == merchantId && t.Status == MerchantStatus.Active, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, string>> GetCodesByIdsAsync(
        IReadOnlySet<Guid> merchantIds, CancellationToken cancellationToken)
    {
        if (merchantIds.Count == 0)
            return new Dictionary<Guid, string>();
        return await _admin.Set<Merchant>()
            .Where(t => merchantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Code, cancellationToken);
    }

    // Bare id lookup (no projection, no PSP metadata) so the read seam can apply the accessible-merchant floor
    // before loading a full merchant view. Unknown code -> null (the seam treats null as inaccessible).
    public Task<Guid?> GetIdByCodeAsync(string code, CancellationToken cancellationToken) =>
        _admin.Set<Merchant>()
            .Where(t => t.Code == code)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(cancellationToken);
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
    /// <summary>Binds the Admin seams to the pol_admin keyed <see cref="PolDbContext"/> (admin tables are
    /// control-plane — resolution/provisioning run cross-merchant). Call AFTER AddMerchantAdminScope.</summary>
    public static IServiceCollection AddAdminIdentity(this IServiceCollection services)
    {
        static PolDbContext Admin(IServiceProvider sp) => sp.GetRequiredKeyedService<PolDbContext>("admin");

        services.AddScoped<IUserRepository>(sp =>
            new UserRepository(Admin(sp), sp.GetRequiredService<ILogger<UserRepository>>()));
        services.AddScoped<IAuditWriter>(sp => new AuditWriter(Admin(sp)));
        services.AddScoped<IAdminMerchantDirectory>(sp => new MerchantDirectory(Admin(sp)));
        services.AddScoped<IRoleRepository>(sp => new RoleRepository(Admin(sp))); // admin-role-rbac (rf2: assignment+resolution only)
        services.AddMasterDataModule(Admin); // profile master lists (masterdata-module)
        services.AddScoped<IMasterDataLookup>(sp => new MasterDataLookup(Admin(sp))); // profile FK exists/lookup

        // Admin BFF session substrate (REQ-3/5/6/11/12): store + append-only auth audit on the keyed pol_admin
        // context; the cookie service is stateless (singleton).
        services.AddScoped<ISessionStore>(sp => new SessionStore(Admin(sp)));
        services.AddScoped<IAuthAuditWriter>(sp => new AuthAuditWriter(Admin(sp)));
        services.AddSingleton<SessionCookies>();

        services.AddScoped<AdminScope>();
        services.AddScoped<IAdminScope>(sp => sp.GetRequiredService<AdminScope>());
        services.AddScoped<IAdminQuery, AdminQuery>();
        return services;
    }
}
