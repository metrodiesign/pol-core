using Admins.Application;
using Admins.Application.MasterData;
using Admins.Application.Roles;
using Admins.Application.Users;
using Admins.Domain.Permissions;
using Admins.Domain.Users;
using Admins.Infrastructure.Persistence;
using Admins.Infrastructure.Persistence.MasterData;
using Admins.Infrastructure.Persistence.Roles;
using Admins.Infrastructure.Persistence.Users;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Merchants.Application.GetMerchant;

namespace Api;

/// <summary>Merchant checks the Admin module needs, over the pol_admin (bypass) connection. Lives in the host so
/// Admins.Application needs no Merchant dependency (mirrors <see cref="MerchantDirectory"/> for Identity).</summary>
internal sealed class AdminMerchantDirectory : IAdminMerchantDirectory
{
    private readonly PolDbContext _admin;

    public AdminMerchantDirectory(PolDbContext admin) => _admin = admin;

    public Task<bool> IsActiveMerchantAsync(Guid merchantId, CancellationToken cancellationToken) =>
        _admin.Set<Merchants.Domain.Merchant>()
            .AnyAsync(t => t.Id == merchantId && t.Status == Merchants.Domain.MerchantStatus.Active, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, string>> GetCodesByIdsAsync(
        IReadOnlySet<Guid> merchantIds, CancellationToken cancellationToken)
    {
        if (merchantIds.Count == 0)
            return new Dictionary<Guid, string>();
        return await _admin.Set<Merchants.Domain.Merchant>()
            .Where(t => merchantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Code, cancellationToken);
    }

    // Bare id lookup (no projection, no PSP metadata) so the read seam can apply the accessible-merchant floor
    // before loading a full merchant view. Unknown code -> null (the seam treats null as inaccessible).
    public Task<Guid?> GetIdByCodeAsync(string code, CancellationToken cancellationToken) =>
        _admin.Set<Merchants.Domain.Merchant>()
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
internal static class PlatformUserTierAuthorization
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

/// <summary>Marks an endpoint as requiring a specific admin permission (admin-role-rbac REQ-6/11). The boot parity
/// guard reads this metadata; the filter below enforces it.</summary>
internal sealed record RequiredAdminPermission(string Permission);

/// <summary>Permission gate for role-driven admin actions (REQ-6.1/6.2): 403 unless the per-request effective
/// permission set (resolved into <see cref="IAdminScope"/> by the auth handler) contains the required key. Reads
/// the scope rather than a claim to keep the principal lean and the decision fresh. Fail-closed when no admin is
/// bound (S4) — a 403, never a 500. Orthogonal to <see cref="PlatformUserTierAuthorization"/> (REQ-7).</summary>
internal static class AdminPermissionAuthorization
{
    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, string permission)
    {
        builder.WithMetadata(new RequiredAdminPermission(permission));
        return builder.AddEndpointFilter(async (context, next) =>
            IsAllowed(context.HttpContext.RequestServices.GetRequiredService<IAdminScope>(), permission)
                ? await next(context)
                : Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "You do not have permission for this action."));
    }

    /// <summary>Fail-closed permission decision (REQ-6.1/6.2/S4): a bound admin whose effective set holds the key.</summary>
    internal static bool IsAllowed(IAdminScope scope, string permission) =>
        scope.IsBound && scope.Current.Permissions.Contains(permission);
}

/// <summary>Boot-time parity guard (REQ-11): every permission key a <c>RequirePermission</c> gate references MUST
/// exist in the code-canonical catalog vocabulary (<see cref="Keys.AllKeys"/>, which the migration
/// seeds the DB from). Pure in-memory — no DB — so it cannot crash a host that boots without the pol_admin
/// connection (Program.cs already fails fast on a missing admin credential). Call right before <c>app.Run()</c>,
/// after all endpoints are mapped.</summary>
internal static class AdminPermissionParity
{
    public static void Assert(IServiceProvider services)
    {
        var gated = services.GetRequiredService<EndpointDataSource>().Endpoints
            .SelectMany(e => e.Metadata.GetOrderedMetadata<RequiredAdminPermission>())
            .Select(m => m.Permission);
        var unknown = FindUnknown(gated);
        if (unknown.Count > 0)
            throw new InvalidOperationException(
                $"RequirePermission references permission key(s) absent from the catalog: {string.Join(", ", unknown)}.");
    }

    /// <summary>The gated keys that are NOT in the code-canonical catalog (REQ-11). Pure — unit-testable.</summary>
    internal static IReadOnlyList<string> FindUnknown(IEnumerable<string> gatedKeys) =>
        [.. gatedKeys.Distinct(StringComparer.Ordinal).Where(p => !Keys.AllKeys.Contains(p))];
}

internal static class AdminHostWiring
{
    /// <summary>Binds the Admin seams to the pol_admin keyed <see cref="PolDbContext"/> (admin tables are
    /// control-plane — resolution/provisioning run cross-merchant). Call AFTER AddMerchantAdminScope.</summary>
    public static IServiceCollection AddAdminIdentity(this IServiceCollection services)
    {
        static PolDbContext Admin(IServiceProvider sp) => sp.GetRequiredKeyedService<PolDbContext>("admin");

        services.AddScoped<IUserRepository>(sp =>
            new UserRepository(Admin(sp), sp.GetRequiredService<ILogger<UserRepository>>()));
        services.AddScoped<IAuditWriter>(sp => new AuditWriter(Admin(sp)));
        services.AddScoped<IAdminMerchantDirectory>(sp => new AdminMerchantDirectory(Admin(sp)));
        services.AddScoped<IRoleRepository>(sp =>
            new RoleRepository(Admin(sp), sp.GetRequiredService<ILogger<RoleRepository>>())); // admin-role-rbac
        services.AddScoped<IMasterDataStore>(sp =>
            new MasterDataStore(Admin(sp), sp.GetRequiredKeyedService<IUnitOfWork>("admin"))); // profile master lists

        // Admin BFF session substrate (REQ-3/5/6/11/12): store + append-only auth audit on the keyed pol_admin
        // context; the cookie service is stateless (singleton).
        services.AddScoped<ISessionStore>(sp => new SessionStore(Admin(sp)));
        services.AddScoped<IAuthAuditWriter>(sp => new AuthAuditWriter(Admin(sp)));
        services.AddSingleton<PlatformUserSessionCookies>();

        services.AddScoped<AdminScope>();
        services.AddScoped<IAdminScope>(sp => sp.GetRequiredService<AdminScope>());
        services.AddScoped<IAdminQuery, AdminQuery>();
        return services;
    }
}
