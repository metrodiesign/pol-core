using Merchants.Domain.Users.Roles;

namespace Merchants.Application.Users.Roles;

/// <summary>
/// Assignment + per-request resolution for the merchant-user side of RBAC (REQ-4/6/7). Role/permission
/// CATALOG CRUD moved to <c>Iam.Application.Roles.IRoleStore</c> (rf2) — this interface keeps only what stays
/// merchant-owned: the <c>merch.RoleAssignments</c> table itself, and the "role code -> id within the
/// Merchant visible set" / "effective permission union" reads that query <c>iam.Roles</c> directly through
/// the published <c>Iam.Domain</c> types (a "resolution repository", design.md's confinement carve-out).
/// Every lookup here takes the TARGET merchant's id explicitly (REQ-3.5/3.6/3.9) — the old catalog's wart was
/// that these reads filtered nothing at all.
/// </summary>
public interface IRoleRepository
{
    void AddAssignment(RoleAssignment assignment);
    void RemoveAssignment(RoleAssignment assignment);

    /// <summary>Resolves role CODES to ids within <paramref name="merchantId"/>'s Merchant visible set
    /// (shared + own-merchant rows, any status — REQ-3.5). A code outside that set is absent from the result;
    /// the caller (<c>SetUserRoles</c>) treats a missing code as a 400.</summary>
    Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(
        Guid merchantId, IReadOnlyCollection<string> codes, CancellationToken cancellationToken);

    /// <summary>Same visible set as <see cref="GetRoleIdsByCodesAsync"/>, restricted to Status=Active
    /// (REQ-7.2/6.5 — approval only ever binds a usable role; "unknown or inactive" is one 409).</summary>
    Task<IReadOnlyDictionary<string, Guid>> GetActiveRoleIdsByCodesAsync(
        Guid merchantId, IReadOnlyCollection<string> codes, CancellationToken cancellationToken);

    Task<IReadOnlySet<Guid>> ListRoleIdsForUserAsync(Guid merchantUserId, CancellationToken cancellationToken);
    Task<RoleAssignment?> GetAssignmentAsync(Guid merchantUserId, Guid roleId, CancellationToken cancellationToken);
    Task<bool> AssignmentExistsAsync(Guid merchantUserId, Guid roleId, CancellationToken cancellationToken);

    /// <summary>Union of permission keys over the user's ACTIVE assigned roles, scoped to the merchant the
    /// user was approved into (REQ-4.2). Defense-in-depth: an assignment whose role belongs to a DIFFERENT
    /// merchant (scope right, merchant wrong — should never happen given write-path validation, but the FK
    /// alone does not constrain it) does not contribute, because the join filters roles through
    /// <see cref="Iam.Domain.Roles.RoleVisibility"/> for THIS merchant, not merely by assignment.MerchantId.</summary>
    Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(Guid merchantUserId, Guid merchantId, CancellationToken cancellationToken);

    /// <summary>The codes of the user's ACTIVE assigned roles in the given merchant — backs the <c>roles</c>
    /// field of <c>GET /merchants/users/me</c>. Empty when none.</summary>
    Task<IReadOnlyList<string>> ListActiveRoleCodesForUserAsync(Guid merchantUserId, Guid merchantId, CancellationToken cancellationToken);
}
