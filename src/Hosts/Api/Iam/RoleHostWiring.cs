using Admins.Application;
using Admins.Application.Users;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Iam.Application.Roles;
using Iam.Infrastructure.Persistence.Roles;
using Merchants.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AdminRoleAssignment = Admins.Domain.Roles.RoleAssignment;
using MerchantRoleAssignment = Merchants.Domain.Users.Roles.RoleAssignment;

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
/// (module-reference rule), so this class is the bridge. It writes into the SAME keyed "admin"
/// <c>PolDbContext</c>/transaction the Iam role store commits through (both are Scoped, resolved once per
/// request), so the audit row can never be separated from the write it describes by a crash. No-ops when no
/// admin is bound — merchant-side role CRUD has never been audited (the old Merchants catalog's own
/// "not in REQ-21" note), so one registration transparently serves both consoles.
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
/// rule), so this class counts both through ordinary EF queries the host CAN see — no raw SQL, provider-
/// agnostic (works against the SQLite unit-test harness the same as SQL Server).
/// </summary>
internal sealed class HostRoleAssignmentCounter : IRoleAssignmentCounter
{
    private readonly PolDbContext _db;

    public HostRoleAssignmentCounter(PolDbContext db) => _db = db;

    public async Task<int> CountAsync(Guid roleId, CancellationToken cancellationToken)
    {
        var admin = await _db.Set<AdminRoleAssignment>().CountAsync(a => a.RoleId == roleId, cancellationToken);
        var merch = await _db.Set<MerchantRoleAssignment>().CountAsync(a => a.RoleId == roleId, cancellationToken);
        return admin + merch;
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountManyAsync(
        IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken)
    {
        if (roleIds.Count == 0)
            return new Dictionary<Guid, int>();

        var admin = await _db.Set<AdminRoleAssignment>().Where(a => roleIds.Contains(a.RoleId))
            .GroupBy(a => a.RoleId).Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoleId, x => x.Count, cancellationToken);
        var merch = await _db.Set<MerchantRoleAssignment>().Where(a => roleIds.Contains(a.RoleId))
            .GroupBy(a => a.RoleId).Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoleId, x => x.Count, cancellationToken);

        var combined = new Dictionary<Guid, int>();
        foreach (var id in roleIds)
            combined[id] = admin.GetValueOrDefault(id) + merch.GetValueOrDefault(id);
        return combined;
    }
}

internal static class RoleHostWiring
{
    /// <summary>Binds the Iam role store + audit sink + assignment counter to the pol_admin keyed context
    /// (control-plane, same as every other identity/RBAC seam). Call once; both consoles share the
    /// registration. Must run AFTER AddAdminIdentity (needs <c>IAdminScope</c>/<c>IAuditWriter</c> already
    /// registered).</summary>
    public static IServiceCollection AddIamRoleManagement(this IServiceCollection services)
    {
        static PolDbContext Admin(IServiceProvider sp) => sp.GetRequiredKeyedService<PolDbContext>("admin");

        services.AddScoped<IRoleStore>(sp =>
            new RoleStore(Admin(sp), sp.GetRequiredService<ILogger<RoleStore>>()));
        services.AddScoped<IRoleAssignmentCounter>(sp => new HostRoleAssignmentCounter(Admin(sp)));
        services.AddScoped<IRoleAuditSink, AdminRoleAuditSink>();
        return services;
    }
}
