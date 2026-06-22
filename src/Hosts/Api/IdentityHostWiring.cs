using System.Security.Claims;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Web;
using Identity.Application;
using Identity.Application.ResolveTenantUser;
using Identity.Domain;
using Identity.Infrastructure.Persistence;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Api;

/// <summary>Checks a tenant the admin selected at approval exists AND is active, over the pol_admin
/// (bypass) connection. Lives in the host so Identity.Application needs no Tenant dependency.</summary>
internal sealed class TenantDirectory : ITenantDirectory
{
    private readonly ProducerDbContext _admin;

    public TenantDirectory(ProducerDbContext admin) => _admin = admin;

    public Task<bool> IsActiveTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        _admin.Set<Tenant.Domain.Tenant>()
            .AnyAsync(t => t.Id == tenantId && t.Status == Tenant.Domain.TenantStatus.Active, cancellationToken);
}

internal static class IdentityHostWiring
{
    /// <summary>Binds the Identity seams to the pol_admin keyed <see cref="ProducerDbContext"/> registered by
    /// <c>AddTenantAdminScope</c> (registration / approval / resolve all run before a tenant is bound, so they
    /// need bypass). Call AFTER AddTenantAdminScope.</summary>
    public static IServiceCollection AddIdentityAdminScope(this IServiceCollection services)
    {
        static ProducerDbContext Admin(IServiceProvider sp) => sp.GetRequiredKeyedService<ProducerDbContext>("admin");

        services.AddScoped<ITenantUserRepository>(sp => new TenantUserRepository(Admin(sp)));
        services.AddScoped<IRegistrationTicketStore>(sp => new RegistrationTicketStore(Admin(sp)));
        services.AddScoped<IRegistrationAuditWriter>(sp => new RegistrationAuditWriter(Admin(sp)));
        services.AddScoped<ITenantDirectory>(sp => new TenantDirectory(Admin(sp)));
        return services;
    }
}

/// <summary>Role gate for tenant-console actions (REQ-7): 403 if the resolved <c>tenant_role</c> is not in
/// the allowed set. The minimal enforced matrix is "Viewer is read-only" — financial/config writes require
/// Finance or TenantAdmin. The exact per-action split beyond that is an open question (see requirements).</summary>
internal static class TenantRoleAuthorization
{
    public static RouteHandlerBuilder RequireTenantRole(this RouteHandlerBuilder builder, params TenantUserRole[] allowed)
    {
        var allowedNames = allowed.Select(r => r.ToString()).ToArray();
        return builder.AddEndpointFilter(async (context, next) =>
            IsRoleAllowed(context.HttpContext.User.FindFirst("tenant_role")?.Value, allowedNames)
                ? await next(context)
                : Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Your role is not permitted for this action."));
    }

    /// <summary>Allowed when the platform resolved a role in the set. A null claim means no TenantUser was
    /// resolved (the Development tenant_id shim, or an applicant): defer to the route's own auth — in
    /// production such a caller has no tenant bound and is denied downstream regardless. Any other
    /// (non-null) role not in the set is denied.</summary>
    internal static bool IsRoleAllowed(string? roleClaim, string[] allowedRoleNames) =>
        roleClaim is null || allowedRoleNames.Contains(roleClaim, StringComparer.Ordinal);
}

/// <summary>
/// Resolves the platform tenant + role for an authenticated tenant-SPA caller (reference 2.5: role/tenant
/// are decided at the platform, NOT by the token). For a caller with the <c>tenant</c> role, it looks up the
/// ACTIVE <see cref="Identity.Domain.TenantUser"/> by Google subject, binds its TenantId as the ambient
/// tenant (so RLS scopes the request) and adds a <c>tenant_role</c> claim. If there is no active TenantUser
/// it binds nothing and falls through — in production that means the tenant routes have no tenant and deny
/// (REQ-6.3); the legacy <c>tenant_id</c> dev claim still works in Development only.
/// </summary>
public sealed class TenantUserResolutionMiddleware
{
    private readonly RequestDelegate _next;

    public TenantUserResolutionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IMediator mediator, ITenantScope scope)
    {
        var user = context.User;
        if (user.FindFirst(GoogleAuthenticationExtensions.RoleClaimType)?.Value == "tenant"
            && user.FindFirstValue("sub") is { Length: > 0 } subject)
        {
            var resolution = await mediator.Send(new ResolveTenantUserQuery(subject), context.RequestAborted);
            if (resolution is not null)
            {
                ((ClaimsIdentity)user.Identity!).AddClaim(new Claim("tenant_role", resolution.Role));
                using (scope.Begin(resolution.TenantId))
                {
                    await _next(context);
                    return;
                }
            }
        }

        await _next(context);
    }
}
