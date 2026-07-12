using BuildingBlocks.Infrastructure.Persistence;
using Iam.Domain.Permissions;
using Iam.Domain.Roles;
using Merchants.Application.Users.Roles;
using Merchants.Domain.Users.Roles;
using Microsoft.EntityFrameworkCore;

namespace Merchants.Infrastructure.Persistence.Users.Roles;

/// <summary>
/// Merchant-user-side role assignment + resolution persistence, bound by the host to the pol_admin
/// (RLS-bypass) connection — a "resolution repository" (design.md confinement carve-out): it queries the
/// central <c>iam.Roles</c>/<c>iam.RolePermissions</c> tables directly through the published <c>Iam.Domain</c>
/// types, always through <see cref="RoleVisibility"/> for the TARGET merchant (REQ-3.5/3.6/3.9). The
/// assignment edge (<c>merch.RoleAssignments</c>) is this module's own.
/// </summary>
public sealed class RoleRepository : IRoleRepository
{
    private readonly PolDbContext _db;

    public RoleRepository(PolDbContext db) => _db = db;

    public void AddAssignment(RoleAssignment assignment) => _db.Set<RoleAssignment>().Add(assignment);
    public void RemoveAssignment(RoleAssignment assignment) => _db.Set<RoleAssignment>().Remove(assignment);

    public async Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(
        Guid merchantId, IReadOnlyCollection<string> codes, CancellationToken cancellationToken)
    {
        if (codes.Count == 0)
            return new Dictionary<string, Guid>();
        return await _db.Set<Role>()
            .Where(RoleVisibility.For(Scope.Merchant, merchantId))
            .Where(r => codes.Contains(r.Code))
            .ToDictionaryAsync(r => r.Code, r => r.Id, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, Guid>> GetActiveRoleIdsByCodesAsync(
        Guid merchantId, IReadOnlyCollection<string> codes, CancellationToken cancellationToken)
    {
        if (codes.Count == 0)
            return new Dictionary<string, Guid>();
        return await _db.Set<Role>()
            .Where(RoleVisibility.For(Scope.Merchant, merchantId))
            .Where(r => r.Status == RoleStatus.Active && codes.Contains(r.Code))
            .ToDictionaryAsync(r => r.Code, r => r.Id, cancellationToken);
    }

    public async Task<IReadOnlySet<Guid>> ListRoleIdsForUserAsync(Guid merchantUserId, CancellationToken cancellationToken)
    {
        var ids = await _db.Set<RoleAssignment>()
            .Where(a => a.MerchantUserId == merchantUserId)
            .Select(a => a.RoleId)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    public Task<RoleAssignment?> GetAssignmentAsync(Guid merchantUserId, Guid roleId, CancellationToken cancellationToken) =>
        _db.Set<RoleAssignment>()
            .FirstOrDefaultAsync(a => a.MerchantUserId == merchantUserId && a.RoleId == roleId, cancellationToken);

    public Task<bool> AssignmentExistsAsync(Guid merchantUserId, Guid roleId, CancellationToken cancellationToken) =>
        _db.Set<RoleAssignment>().AnyAsync(a => a.MerchantUserId == merchantUserId && a.RoleId == roleId, cancellationToken);

    public async Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(
        Guid merchantUserId, Guid merchantId, CancellationToken cancellationToken)
    {
        // Union of keys over the user's assigned roles that are (a) scoped to the merchant they were approved
        // into, (b) Active, AND (c) still visible to that merchant under RoleVisibility — defense-in-depth
        // (REQ-4.2): an assignment whose role belongs to a DIFFERENT merchant does not contribute even if the
        // assignment row itself carries the right MerchantId.
        var keys = await _db.Set<RoleAssignment>()
            .Where(a => a.MerchantUserId == merchantUserId && a.MerchantId == merchantId)
            .Join(
                _db.Set<Role>().Where(RoleVisibility.For(Scope.Merchant, merchantId)).Where(r => r.Status == RoleStatus.Active),
                a => a.RoleId, r => r.Id, (a, r) => r.Id)
            .Join(_db.Set<RolePermission>(), roleId => roleId, p => p.RoleId, (roleId, p) => p.PermissionKey)
            .Distinct()
            .ToListAsync(cancellationToken);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<string>> ListActiveRoleCodesForUserAsync(
        Guid merchantUserId, Guid merchantId, CancellationToken cancellationToken) =>
        await _db.Set<RoleAssignment>()
            .Where(a => a.MerchantUserId == merchantUserId && a.MerchantId == merchantId)
            .Join(
                _db.Set<Role>().Where(RoleVisibility.For(Scope.Merchant, merchantId)).Where(r => r.Status == RoleStatus.Active),
                a => a.RoleId, r => r.Id, (a, r) => r.Code)
            .Distinct()
            .ToListAsync(cancellationToken);
}
