using Admins.Application.Permissions;
using Admins.Domain.Roles;
using BuildingBlocks.Application;

namespace Admins.Application.Roles;

/// <summary>
/// Persistence for the Admin Role RBAC realm. Bound by the host to the pol_admin (RLS-bypass) keyed context —
/// role/catalog tables are control-plane. Catalog reads back <c>GET /admin/permissions</c>; role + assignment
/// reads back the management endpoints; <see cref="ListEffectivePermissionsAsync"/> backs per-request resolution
/// (REQ-5). Commits run through the keyed <c>IUnitOfWork</c>, never a repository SaveChanges (S2).
/// </summary>
public interface IRoleRepository
{
    void Add(Role role);
    void Remove(Role role);
    void AddAssignment(RoleAssignment assignment);
    void RemoveAssignment(RoleAssignment assignment);

    Task<Role?> GetByCodeAsync(string code, CancellationToken cancellationToken);       // tracked, incl. permissions
    Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken);
    Task<int> CountAssignmentsForRoleAsync(Guid roleId, CancellationToken cancellationToken);

    /// <summary>The SFS-paged role list (REQ-2.4): filter/search/sort applied over the control-plane
    /// <c>Role</c> set, <c>Total</c> counted after filter/search but before paging, <c>UserCount</c>
    /// preserved (REQ-12.1).</summary>
    Task<PagedResult<RoleListItem>> ListAsync(PagedQuery query, CancellationToken cancellationToken);
    Task<RoleListItem?> GetListItemByCodeAsync(string code, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(
        IReadOnlyCollection<string> codes, CancellationToken cancellationToken);
    Task<IReadOnlySet<Guid>> ListRoleIdsForAdminAsync(Guid adminId, CancellationToken cancellationToken);
    Task<RoleAssignment?> GetAssignmentAsync(Guid adminId, Guid roleId, CancellationToken cancellationToken);
    Task<bool> AssignmentExistsAsync(Guid adminId, Guid roleId, CancellationToken cancellationToken);

    /// <summary>Catalog vocabulary used to validate role grants (REQ-3.3) — the live DB key set.</summary>
    Task<IReadOnlySet<string>> ListCatalogKeysAsync(CancellationToken cancellationToken);

    /// <summary>The full catalog (groups + permissions) for <c>GET /admin/permissions</c> (REQ-1.5).</summary>
    Task<PermissionCatalogResult> ListCatalogAsync(CancellationToken cancellationToken);

    /// <summary>Union of permission keys over the admin's ACTIVE assigned roles (REQ-5.1).</summary>
    Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(Guid adminId, CancellationToken cancellationToken);

    /// <summary>Every role CODE assigned to the admin, including roles whose status is Inactive — the detail
    /// view shows assignment truth, not enforcement effect (admin-account-management REQ-2.1). Ordered by code.</summary>
    Task<IReadOnlyList<string>> ListRoleCodesForAdminAsync(Guid adminId, CancellationToken cancellationToken);
}

/// <summary>A role as the management endpoints render it (REQ-2). <see cref="Status"/> is the enum; the host
/// projects it to <c>"active"/"inactive"</c> on the wire (B2).</summary>
public sealed record RoleListItem(
    string Code, string Name, string? Description, string? Color,
    RoleStatus Status, IReadOnlyList<string> PermissionKeys, int UserCount);
