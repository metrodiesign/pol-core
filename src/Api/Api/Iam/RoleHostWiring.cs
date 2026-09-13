using Admins.Application;
using Admins.Application.Users;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Iam.Application.Roles;
using Iam.Domain.Permissions;
using Merchants.Application;
using Persistence.ControlPlane;
using Persistence.MerchantUsers;

namespace Api.Iam;

/// <summary>
/// The single point that derives <see cref="RoleSideContext"/> from whichever scope is bound to the request
/// (design.md "ห้าม endpoint ประกอบ record เองต่อ call site"): <c>IAdminScope</c> bound -> Platform/null,
/// <c>IUserScope</c> bound -> Merchant/<c>MerchantId</c>. Neither <c>Admins.Application</c> nor
/// <c>Merchants.Application</c> may see the other's scope type, and <c>Iam.Application</c> may see neither
/// (module-reference rule), so this derivation can only live at the host — the one place that sees all three.
/// </summary>
internal static class RoleSideContextResolver
{
    public static RoleSideContext ForAdmin(IAdminScope scope)
    {
        if (!scope.IsBound)
            throw new InvalidOperationException("No admin is bound to this request.");
        return RoleSideContext.Platform();
    }

    public static RoleSideContext ForMerchantUser(IUserScope scope)
    {
        if (!scope.IsBound)
            throw new InvalidOperationException("No merchant user is bound to this request.");
        return RoleSideContext.Merchant(scope.Current.MerchantId);
    }
}

/// <summary>
/// The concrete <see cref="IRoleAuditSink"/> the host registers. <c>Iam.Application</c> cannot reference
/// Admins' <see cref="IAuditWriter"/>/<see cref="Audit"/>/<see cref="AuditAction"/> types directly
/// (module-reference rule), so this class is the bridge. It writes into the SAME <c>ControlPlaneDbContext</c>/
/// transaction the Iam role store commits through (both are Scoped, resolved once per request), so the audit
/// row can never be separated from the write it describes by a crash. No-ops when no admin is bound —
/// merchant-side role CRUD has never been audited (the old Merchants catalog's own "not in REQ-21" note), so
/// one registration transparently serves both consoles.
/// </summary>
internal sealed class AdminRoleAuditSink : IRoleAuditSink
{
    private readonly IAdminScope _scope;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public AdminRoleAuditSink(IAdminScope scope, IAuditWriter audit, IClock clock)
    {
        _scope = scope;
        _audit = audit;
        _clock = clock;
    }

    public void RoleCreated(Guid roleId, string correlationId) => Append(AuditAction.RoleCreated, roleId, correlationId);
    public void RoleUpdated(Guid roleId, string correlationId) => Append(AuditAction.RoleUpdated, roleId, correlationId);
    public void RoleDeleted(Guid roleId, string correlationId) => Append(AuditAction.RoleDeleted, roleId, correlationId);

    private void Append(string action, Guid roleId, string correlationId)
    {
        if (!_scope.IsBound)
            return; // merchant-side request: no admin ever bound, so no audit row (unchanged behavior)
        _audit.Append(Audit.For(action, _scope.Current.AdminId, correlationId, _clock.UtcNow, targetRoleId: roleId));
    }
}

/// <summary>
/// The concrete <see cref="IRoleAssignmentCounter"/> the host registers. <c>Iam.Application</c> cannot
/// reference the <c>admin.RoleAssignments</c>/<c>merch.RoleAssignments</c> entity types (module-reference
/// rule), and this host may not resolve <c>ControlPlaneDbContext</c>/<c>MerchantUserDbContext</c> directly
/// either (design.md "Assembly split + Api host boundary", task 8.5.7) — so it composes the two narrow count
/// ports each cluster exposes instead of querying either context itself.
/// </summary>
internal sealed class HostRoleAssignmentCounter : IRoleAssignmentCounter
{
    private readonly IAdminRoleAssignmentCountReader _admin;
    private readonly IMerchantRoleAssignmentCountReader _merchant;

    public HostRoleAssignmentCounter(IAdminRoleAssignmentCountReader admin, IMerchantRoleAssignmentCountReader merchant)
    {
        _admin = admin;
        _merchant = merchant;
    }

    public async Task<int> CountAsync(RoleSideContext context, Guid roleId, CancellationToken cancellationToken)
    {
        // Merchant console: only its OWN merchant's rows count — a shared role's global total would leak
        // other tenants' user counts (REQ-3.6). Admin assignments never reference Merchant-scope roles
        // (cross-side grant is rejected + the assignment drift guard pins it), so that reader is skipped.
        if (context.Scope == Scope.Merchant)
            return await _merchant.CountForMerchantAsync(roleId, context.MerchantId!.Value, cancellationToken);

        var admin = await _admin.CountAsync(roleId, cancellationToken);
        var merch = await _merchant.CountGlobalAsync(roleId, cancellationToken);
        return admin + merch;
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountManyAsync(
        RoleSideContext context, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken)
    {
        if (roleIds.Count == 0)
            return new Dictionary<Guid, int>();

        if (context.Scope == Scope.Merchant)
            return await _merchant.CountManyForMerchantAsync(roleIds, context.MerchantId!.Value, cancellationToken);

        var admin = await _admin.CountManyAsync(roleIds, cancellationToken);
        var merch = await _merchant.CountManyGlobalAsync(roleIds, cancellationToken);

        var combined = new Dictionary<Guid, int>();
        foreach (var id in roleIds)
            combined[id] = admin.GetValueOrDefault(id) + merch.GetValueOrDefault(id);
        return combined;
    }
}

internal static class RoleHostWiring
{
    /// <summary>Binds the Iam audit sink + assignment counter (task 8.5.7 — <c>IRoleStore</c> is no longer
    /// registered here: <c>AddControlPlanePersistence</c> already registers it unkeyed, shared by both
    /// hosts). Call once; both consoles share the registration. Must run AFTER AddAdminIdentity (needs
    /// <c>IAdminScope</c>/<c>IAuditWriter</c> already registered) and AFTER AddControlPlanePersistence +
    /// AddMerchantUserPersistence (needs the two count-reader ports).</summary>
    public static IServiceCollection AddIamRoleManagement(this IServiceCollection services)
    {
        services.AddScoped<IRoleAssignmentCounter, HostRoleAssignmentCounter>();
        services.AddScoped<IRoleAuditSink, AdminRoleAuditSink>();
        return services;
    }
}
