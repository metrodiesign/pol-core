using System.Security.Claims;
using Admin.Application;
using Admin.Application.BindInvitedAdmin;
using Admin.Application.ResolveAdmin;
using Admin.Application.SelfProvisionSuperAdmin;
using Admin.Domain;
using Admin.Infrastructure.Persistence;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Web;
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
}

/// <summary>Per-request holder of the resolved admin (REQ-6.3). The <see cref="AdminResolutionMiddleware"/>
/// calls <see cref="Set"/> once; readers consume <see cref="IAdminScope"/>.</summary>
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

    public AdminQuery(IMediator mediator, IAdminScope scope)
    {
        _mediator = mediator;
        _scope = scope;
    }

    public async Task<TenantView?> GetTenantByCodeAsync(string code, CancellationToken cancellationToken)
    {
        TenantView view;
        try
        {
            view = await _mediator.Send(new GetTenantQuery(code), cancellationToken);
        }
        catch (NotFoundException)
        {
            return null; // unknown code -> 404
        }

        // Fail-closed: a Scoped admin asking for a tenant outside its set gets nothing — indistinguishable from
        // a missing tenant (no existence leak). Super is unrestricted (REQ-7.3 / 8.5).
        return _scope.Accessible.Allows(view.Id) ? view : null;
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

        services.AddScoped<AdminScope>();
        services.AddScoped<IAdminScope>(sp => sp.GetRequiredService<AdminScope>());
        services.AddScoped<IAdminQuery, AdminQuery>();
        return services;
    }
}

/// <summary>
/// Resolves the platform admin for an authenticated admin-SPA caller (REQ-5/6). For a caller with the
/// <c>admin</c> role it resolves the <see cref="AdminAccount"/> by Google subject and materializes the
/// accessible-tenant set into <see cref="IAdminScope"/> + an <c>admin_tier</c> claim. First login binds an
/// invited Scoped account by email, else self-provisions a Super from the config allowlist (idempotent). A
/// caller with no resolvable admin (not allowlisted, no invite) or a suspended admin is denied 403
/// (fail-closed). Unlike the read-only tenant middleware, this one has a write path (self-provision/bind).
/// </summary>
public sealed class AdminResolutionMiddleware
{
    private readonly RequestDelegate _next;

    public AdminResolutionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context, IMediator mediator, IAdminScope scope, IConfiguration configuration,
        ILogger<AdminResolutionMiddleware> logger)
    {
        var user = context.User;
        if (user.FindFirst(GoogleAuthenticationExtensions.RoleClaimType)?.Value != "admin"
            || user.FindFirstValue("sub") is not { Length: > 0 } subject)
        {
            await _next(context);
            return;
        }

        // MFA best-effort (REQ-9): assert an amr/acr claim if present; log its absence; never hard-gate
        // (Google ID tokens routinely omit it — Workspace org policy is the enforcing control).
        if (user.FindFirst("amr") is null && user.FindFirst("acr") is null)
            logger.LogInformation("Admin token for subject {Subject} carries no amr/acr MFA claim; relying on Workspace policy.", subject);

        var email = user.FindFirstValue("email") ?? string.Empty;
        var ct = context.RequestAborted;

        var result = await mediator.Send(new ResolveAdminQuery(subject), ct);
        if (result.Outcome == AdminResolveOutcome.NotFound)
            result = await ResolveFirstLoginAsync(mediator, configuration, subject, email, context.TraceIdentifier, ct);

        if (result.Outcome == AdminResolveOutcome.Resolved)
        {
            var resolution = result.Resolution!;
            ((ClaimsIdentity)user.Identity!).AddClaim(new Claim("admin_tier", resolution.Tier.ToString()));
            ((AdminScope)scope).Set(resolution); // the only registered IAdminScope impl is the host's AdminScope holder
            await _next(context);
            return;
        }

        // Suspended (REQ-5.6) or NotFound-and-not-bootstrappable (REQ-5.3): deny, bind nothing.
        await Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Your admin account is not active.").ExecuteAsync(context);
    }

    /// <summary>First login: bind an invited Scoped account by email, else self-provision a Super from the
    /// allowlist. Invite-bind is tried FIRST so an invited email never collides with the unique Email index
    /// via self-provision (REQ-3.5 before REQ-5.1).</summary>
    private static async Task<AdminResolveResult> ResolveFirstLoginAsync(
        IMediator mediator, IConfiguration configuration, string subject, string email, string correlationId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(email))
        {
            var bound = await mediator.Send(new BindInvitedAdminCommand(subject, email, correlationId), ct);
            if (bound.Outcome != AdminResolveOutcome.NotFound)
                return bound;
        }

        var allowlist = configuration.GetSection("AdminAllowlist:Subjects").Get<string[]>() ?? [];
        if (allowlist.Contains(subject, StringComparer.Ordinal)) // REQ-5.1
            return AdminResolveResult.Of(await mediator.Send(new SelfProvisionSuperAdminCommand(subject, email, correlationId), ct));

        return AdminResolveResult.NotFound; // REQ-5.3 -> 403
    }
}
