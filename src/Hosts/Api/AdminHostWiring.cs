using Admin.Application;
using Admin.Application.ResolveAdmin;
using Admin.Domain;
using Admin.Infrastructure.Persistence;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Tenant.Application.GetTenant;

namespace Api;

/// <summary>Tenant checks the Admin module needs, over the pol_admin (bypass) connection. Lives in the host so
/// Admin.Application needs no Tenant dependency (mirrors <see cref="TenantDirectory"/> for Identity).</summary>
internal sealed class AdminTenantDirectory : IAdminTenantDirectory
{
    private readonly ProducerDbContext _admin;

    public AdminTenantDirectory(ProducerDbContext admin) => _admin = admin;

    public Task<bool> IsActiveTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        _admin.Set<Tenant.Domain.Tenant>()
            .AnyAsync(t => t.Id == tenantId && t.Status == Tenant.Domain.TenantStatus.Active, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, string>> GetCodesByIdsAsync(
        IReadOnlySet<Guid> tenantIds, CancellationToken cancellationToken)
    {
        if (tenantIds.Count == 0)
            return new Dictionary<Guid, string>();
        return await _admin.Set<Tenant.Domain.Tenant>()
            .Where(t => tenantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Code, cancellationToken);
    }

    // Bare id lookup (no projection, no PSP metadata) so the read seam can apply the accessible-tenant floor
    // before loading a full tenant view. Unknown code -> null (the seam treats null as inaccessible).
    public Task<Guid?> GetIdByCodeAsync(string code, CancellationToken cancellationToken) =>
        _admin.Set<Tenant.Domain.Tenant>()
            .Where(t => t.Code == code)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(cancellationToken);
}

/// <summary>Per-request holder of the resolved admin (REQ-6.3). The admin authentication handler
/// (<see cref="AdminSessionAuthenticationHandler"/>) calls <see cref="Set"/> once per request; readers consume
/// <see cref="IAdminScope"/>.</summary>
internal sealed class AdminScope : IAdminScope
{
    private AdminResolution? _current;

    public bool IsBound => _current is not null;
    public AdminResolution Current => _current ?? throw new InvalidOperationException("No admin is bound to this request.");
    public AccessibleTenants Accessible => Current.Accessible;

    public void Set(AdminResolution resolution) => _current = resolution;
}

/// <summary>The ONLY seam through which an admin handler may read a tenant-scoped business table cross-tenant
/// (REQ-7.1). pol_admin bypasses RLS at the DB, so this seam IS the floor: it applies the
/// <see cref="IAdminScope"/> filter (Super = unrestricted; Scoped = only assigned tenants). Architecture.Tests
/// forbids any other host type from sending <see cref="GetTenantQuery"/> directly (REQ-7.2).</summary>
internal interface IAdminQuery
{
    Task<TenantView?> GetTenantByCodeAsync(string code, CancellationToken cancellationToken);
}

internal sealed class AdminQuery : IAdminQuery
{
    private readonly IMediator _mediator;
    private readonly IAdminScope _scope;
    private readonly IAdminTenantDirectory _tenants;

    public AdminQuery(IMediator mediator, IAdminScope scope, IAdminTenantDirectory tenants)
    {
        _mediator = mediator;
        _scope = scope;
        _tenants = tenants;
    }

    public async Task<TenantView?> GetTenantByCodeAsync(string code, CancellationToken cancellationToken)
    {
        // Apply the accessible-tenant floor BEFORE loading the projection (REQ-7.1): for a Scoped admin, resolve
        // the code to an id with a bare lookup and gate on the accessible set first. An unknown code OR a tenant
        // outside the set returns null without the full bypass projection (incl. PSP-metadata parsing) ever
        // running — fail-closed, no existence leak, and no handler-side error can surface for an inaccessible
        // tenant. A Super admin is unrestricted, so it skips the gate and loads directly (REQ-7.3 / 8.5).
        if (!_scope.Accessible.IsUnrestricted)
        {
            var id = await _tenants.GetIdByCodeAsync(code, cancellationToken);
            if (id is null || !_scope.Accessible.Allows(id.Value))
                return null;
        }

        try
        {
            return await _mediator.Send(new GetTenantQuery(code), cancellationToken);
        }
        catch (NotFoundException)
        {
            return null; // unknown code -> 404
        }
    }
}

/// <summary>Tier gate for Super-only admin actions (REQ-8.1): 403 unless the resolved <c>admin_tier</c> claim
/// is in the allowed set. Mirrors <see cref="TenantRoleAuthorization"/>.</summary>
internal static class AdminTierAuthorization
{
    public static RouteHandlerBuilder RequireAdminTier(this RouteHandlerBuilder builder, params AdminTier[] allowed)
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

internal static class AdminHostWiring
{
    /// <summary>Binds the Admin seams to the pol_admin keyed <see cref="ProducerDbContext"/> (admin tables are
    /// control-plane — resolution/provisioning run cross-tenant). Call AFTER AddTenantAdminScope.</summary>
    public static IServiceCollection AddAdminIdentity(this IServiceCollection services)
    {
        static ProducerDbContext Admin(IServiceProvider sp) => sp.GetRequiredKeyedService<ProducerDbContext>("admin");

        services.AddScoped<IAdminAccountRepository>(sp => new AdminAccountRepository(Admin(sp)));
        services.AddScoped<IAdminAccountAuditWriter>(sp => new AdminAccountAuditWriter(Admin(sp)));
        services.AddScoped<IAdminTenantDirectory>(sp => new AdminTenantDirectory(Admin(sp)));

        // Admin BFF session substrate (REQ-3/5/6/11/12): store + append-only auth audit on the keyed pol_admin
        // context; the cookie service is stateless (singleton).
        services.AddScoped<IAdminSessionStore>(sp => new AdminSessionStore(Admin(sp)));
        services.AddScoped<IAdminAuthAuditWriter>(sp => new AdminAuthAuditWriter(Admin(sp)));
        services.AddSingleton<AdminSessionCookies>();

        services.AddScoped<AdminScope>();
        services.AddScoped<IAdminScope>(sp => sp.GetRequiredService<AdminScope>());
        services.AddScoped<IAdminQuery, AdminQuery>();
        return services;
    }
}
