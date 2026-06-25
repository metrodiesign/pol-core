using Admin.Domain;

namespace Admin.Application;

/// <summary>
/// Persistence for the Admin Role RBAC realm. Bound by the host to the pol_admin (RLS-bypass) keyed context —
/// role/catalog tables are control-plane. Catalog reads back <c>GET /admin/permissions</c>; role + assignment
/// reads back the management endpoints; <see cref="ListEffectivePermissionsAsync"/> backs per-request resolution
/// (REQ-5). Commits run through the keyed <c>IUnitOfWork</c>, never a repository SaveChanges (S2).
/// </summary>
public interface IAdminRoleRepository
{
    void Add(AdminRole role);
    void Remove(AdminRole role);
    void AddAssignment(AdminRoleAssignment assignment);
    void RemoveAssignment(AdminRoleAssignment assignment);

    Task<AdminRole?> GetByCodeAsync(string code, CancellationToken cancellationToken);       // tracked, incl. permissions
    Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken);
    Task<int> CountAssignmentsForRoleAsync(Guid roleId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AdminRoleListItem>> ListAsync(CancellationToken cancellationToken);
    Task<AdminRoleListItem?> GetListItemByCodeAsync(string code, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(
        IReadOnlyCollection<string> codes, CancellationToken cancellationToken);
    Task<IReadOnlySet<Guid>> ListRoleIdsForAdminAsync(Guid adminId, CancellationToken cancellationToken);
    Task<AdminRoleAssignment?> GetAssignmentAsync(Guid adminId, Guid roleId, CancellationToken cancellationToken);
    Task<bool> AssignmentExistsAsync(Guid adminId, Guid roleId, CancellationToken cancellationToken);

    /// <summary>Catalog vocabulary used to validate role grants (REQ-3.3) — the live DB key set.</summary>
    Task<IReadOnlySet<string>> ListCatalogKeysAsync(CancellationToken cancellationToken);

    /// <summary>The full catalog (groups + permissions) for <c>GET /admin/permissions</c> (REQ-1.5).</summary>
    Task<PermissionCatalogResult> ListCatalogAsync(CancellationToken cancellationToken);

    /// <summary>Union of permission keys over the admin's ACTIVE assigned roles (REQ-5.1).</summary>
    Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(Guid adminId, CancellationToken cancellationToken);
}

/// <summary>A role as the management endpoints render it (REQ-2). <see cref="Status"/> is the enum; the host
/// projects it to <c>"active"/"inactive"</c> on the wire (B2).</summary>
public sealed record AdminRoleListItem(
    string Code, string Name, string? Description, string? Color,
    AdminRoleStatus Status, IReadOnlyList<string> PermissionKeys, int UserCount);

/// <summary>The permission catalog (REQ-1.5). <see cref="PermissionItem.Resource"/> = the permission's group key.</summary>
public sealed record PermissionCatalogResult(
    IReadOnlyList<PermissionGroupItem> Groups, IReadOnlyList<PermissionItem> Permissions);

public sealed record PermissionGroupItem(string Key, string LabelTh);

public sealed record PermissionItem(string Key, string LabelTh, string Resource);
