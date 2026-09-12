using Admins.Domain.Roles;

namespace Admins.Application.Roles;

/// <summary>
/// Assignment + per-request resolution for the admin side of RBAC (REQ-4/5). Role/permission CATALOG CRUD
/// moved to <c>Iam.Application.Roles.IRoleStore</c> (rf2) — this interface keeps only what stays
/// admin-owned: the <c>admin.RoleAssignments</c> table itself, and the "role code -> id within the Platform
/// visible set" / "effective permission union" reads that query <c>iam.Roles</c> directly through the
/// published <c>Iam.Domain</c> types (a "resolution repository", design.md's confinement carve-out).
/// </summary>
public interface IRoleRepository
{
    void AddAssignment(RoleAssignment assignment);
    void RemoveAssignment(RoleAssignment assignment);

    /// <summary>Resolves role CODES to ids within the Platform visible set (Scope=Platform, MerchantId=NULL —
    /// REQ-3.4/3.9). A code outside that set (unknown, or a Merchant-scope role) is simply absent from the
    /// result — the caller (<c>SetAdminRoles</c>) treats a missing code as a 400.</summary>
    Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(
        IReadOnlyCollection<string> codes, CancellationToken cancellationToken);

    Task<IReadOnlySet<Guid>> ListRoleIdsForAdminAsync(Guid adminId, CancellationToken cancellationToken);
    Task<RoleAssignment?> GetAssignmentAsync(Guid adminId, Guid roleId, CancellationToken cancellationToken);
    Task<bool> AssignmentExistsAsync(Guid adminId, Guid roleId, CancellationToken cancellationToken);

    /// <summary>Union of permission keys over the admin's ACTIVE assigned roles (REQ-4.2/4.6).</summary>
    Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(Guid adminId, CancellationToken cancellationToken);

    /// <summary>Every role CODE assigned to the admin, including roles whose status is Inactive — the detail
    /// view shows assignment truth, not enforcement effect (admin-account-management REQ-2.1). Ordered by code.</summary>
    Task<IReadOnlyList<string>> ListRoleCodesForAdminAsync(Guid adminId, CancellationToken cancellationToken);
}
