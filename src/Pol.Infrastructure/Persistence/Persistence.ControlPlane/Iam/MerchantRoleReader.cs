using Iam.Domain.Permissions;
using Iam.Domain.Roles;
using Microsoft.EntityFrameworkCore;

namespace Persistence.ControlPlane.Iam;

/// <summary>
/// Resolves <c>iam.Roles</c>/<c>iam.RolePermissions</c> for a MERCHANT-scoped caller (task 8.5.7's
/// cross-context fix for <c>Merchants.Application.Users.Roles.IRoleRepository</c>'s 4 members the
/// MerchantUser cluster cannot implement — <c>merch.RoleAssignments</c> lives in a DIFFERENT runtime
/// context). A host-level composition combines this with <c>Persistence.MerchantUsers</c>'s own
/// <c>IMerchantRoleAssignmentReader</c> — same pattern as <c>IAdminRoleAssignmentCountReader</c> /
/// <c>IMerchantRoleAssignmentCountReader</c> composing into <c>HostRoleAssignmentCounter</c>. Matches
/// design.md's cross-cutting section, which names exactly this shape <c>IMerchantRoleReader</c>.
/// </summary>
public interface IMerchantRoleReader
{
    /// <summary>Resolves role CODES to ids within the merchant's visible set (shared + own-merchant, any
    /// status) — mirrors the retired Merchants.Infrastructure RoleRepository.GetRoleIdsByCodesAsync.</summary>
    Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(
        Guid merchantId, IReadOnlyCollection<string> codes, CancellationToken cancellationToken);

    /// <summary>Same visible set, restricted to Status=Active.</summary>
    Task<IReadOnlyDictionary<string, Guid>> GetActiveRoleIdsByCodesAsync(
        Guid merchantId, IReadOnlyCollection<string> codes, CancellationToken cancellationToken);

    /// <summary>Union of permission keys over the GIVEN role ids, filtered to Active + visible to the
    /// merchant (defense-in-depth: a role id outside the merchant's visible set contributes nothing even if
    /// the caller already believes it is assigned).</summary>
    Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(
        Guid merchantId, IReadOnlySet<Guid> roleIds, CancellationToken cancellationToken);

    /// <summary>The codes of the GIVEN role ids, filtered to Active + visible to the merchant.</summary>
    Task<IReadOnlyList<string>> ListActiveRoleCodesAsync(
        Guid merchantId, IReadOnlySet<Guid> roleIds, CancellationToken cancellationToken);
}

internal sealed class MerchantRoleReader : IMerchantRoleReader
{
    private readonly ControlPlaneDbContext _db;
    public MerchantRoleReader(ControlPlaneDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(
        Guid merchantId, IReadOnlyCollection<string> codes, CancellationToken cancellationToken)
    {
        if (codes.Count == 0)
            return new Dictionary<string, Guid>();
        return await _db.Roles
            .Where(RoleVisibility.For(Scope.Merchant, merchantId))
            .Where(r => codes.Contains(r.Code))
            .ToDictionaryAsync(r => r.Code, r => r.Id, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, Guid>> GetActiveRoleIdsByCodesAsync(
        Guid merchantId, IReadOnlyCollection<string> codes, CancellationToken cancellationToken)
    {
        if (codes.Count == 0)
            return new Dictionary<string, Guid>();
        return await _db.Roles
            .Where(RoleVisibility.For(Scope.Merchant, merchantId))
            .Where(r => r.Status == RoleStatus.Active && codes.Contains(r.Code))
            .ToDictionaryAsync(r => r.Code, r => r.Id, cancellationToken);
    }

    public async Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(
        Guid merchantId, IReadOnlySet<Guid> roleIds, CancellationToken cancellationToken)
    {
        if (roleIds.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);
        var keys = await _db.Roles
            .Where(RoleVisibility.For(Scope.Merchant, merchantId))
            .Where(r => r.Status == RoleStatus.Active && roleIds.Contains(r.Id))
            .Join(_db.RolePermissions, r => r.Id, p => p.RoleId, (r, p) => p.PermissionKey)
            .Join(_db.Permissions.Where(p => p.Status == PermissionStatus.Active), key => key, p => p.Key,
                (key, p) => new { key, p.GroupKey })
            .Join(_db.PermissionGroups.Where(g => g.Status == PermissionStatus.Active), x => x.GroupKey, g => g.Key,
                (x, g) => x.key)
            .Distinct()
            .ToListAsync(cancellationToken);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<string>> ListActiveRoleCodesAsync(
        Guid merchantId, IReadOnlySet<Guid> roleIds, CancellationToken cancellationToken)
    {
        if (roleIds.Count == 0)
            return [];
        return await _db.Roles
            .Where(RoleVisibility.For(Scope.Merchant, merchantId))
            .Where(r => r.Status == RoleStatus.Active && roleIds.Contains(r.Id))
            .Select(r => r.Code)
            .Distinct()
            .ToListAsync(cancellationToken);
    }
}
