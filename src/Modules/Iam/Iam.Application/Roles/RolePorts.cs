using Iam.Application.Permissions;
using Iam.Domain.Permissions;
using Iam.Domain.Roles;
using BuildingBlocks.Application;

namespace Iam.Application.Roles;

/// <summary>
/// Persistence for the central IAM Role RBAC catalog (REQ-1/2/3/6), replacing the two duplicated
/// <c>Admins.Application.Roles.IRoleRepository</c> / <c>Merchants.Application.Users.Roles.IRoleRepository</c>
/// CRUD surfaces. Every read/lookup is <see cref="RoleSideContext"/>-scoped (REQ-3.6/3.9) — there is no
/// unscoped "get by code" left anywhere in this store, closing the old merchant catalog's wart where
/// GetByCode/CodeExists/List bypassed visibility entirely. Bound by the host to the pol_admin (RLS-bypass)
/// keyed context — role/catalog tables are control-plane, same as before. Assignment CRUD and per-request
/// permission resolution stay OUTSIDE this store, on each side's own (now much smaller)
/// <c>IRoleRepository</c> in <c>Admins</c>/<c>Merchants.Application</c> — those query <c>iam.Roles</c>
/// directly via the published <c>Iam.Domain</c> types (design.md "resolution repositories").
/// </summary>
public interface IRoleStore
{
    void Add(Role role);
    void Remove(Role role);

    /// <summary>Tracked, including permissions, filtered to <paramref name="context"/>'s visible set
    /// (REQ-3.6/3.9). Invisible or absent -> null (no existence leak across sides/merchants).</summary>
    Task<Role?> GetByCodeAsync(RoleSideContext context, string code, CancellationToken cancellationToken);

    /// <summary>True if <paramref name="code"/> already exists anywhere <paramref name="context"/> can see
    /// (REQ-6.3) OR — when <paramref name="context"/> lands in the shared NULL bucket (Platform, or a shared
    /// Merchant role) — anywhere in that bucket regardless of <see cref="Scope"/> (critique P2-5: the DB
    /// UNIQUE(MerchantId, Code) buckets by MerchantId alone, so a Platform role and a shared Merchant seed
    /// role with the same code collide there even though neither is in the other's visible set; catching it
    /// here turns a real collision into a clean 409 instead of a raw unique-violation).</summary>
    Task<bool> CodeExistsAsync(RoleSideContext context, string code, CancellationToken cancellationToken);

    /// <summary>The SFS-paged role list (REQ-2.4/6.1), filtered to <paramref name="context"/>'s visible set
    /// first. <see cref="RoleListItem.UserCount"/> is always 0 here — this store owns only the <c>iam.*</c>
    /// tables; the caller composes the real count via <see cref="IRoleAssignmentCounter"/>. The admin console
    /// passes real paging/filter/sort/search; the merchant console (no SFS in its wire contract) passes a
    /// query with a large <c>Limit</c> and reads only <c>Items</c> back — one query type/handler/store method
    /// serves both wire shapes (design.md "handler เดียวต่อ operation").</summary>
    Task<PagedResult<RoleListItem>> ListAsync(RoleSideContext context, PagedQuery query, CancellationToken cancellationToken);

    /// <summary><see cref="RoleListItem.UserCount"/> is always 0 — see <see cref="ListAsync"/>.</summary>
    Task<RoleListItem?> GetListItemByCodeAsync(RoleSideContext context, string code, CancellationToken cancellationToken);

    /// <summary>The full catalog (groups + permissions) for <c>GET /permissions</c> (REQ-1.5/6.4), filtered to
    /// one side's <see cref="Scope"/> — a console never sees the other side's vocabulary.</summary>
    Task<PermissionCatalogResult> ListCatalogAsync(Scope scope, CancellationToken cancellationToken);
}

/// <summary>A role as the management endpoints render it (REQ-2/6). <see cref="Status"/> is the enum; the
/// host projects it to <c>"active"/"inactive"</c> on the wire (unchanged). <see cref="Shared"/> (new,
/// additive — REQ-3.8/design "wire contracts") is true for a NULL-<c>MerchantId</c> row: a seed/shared role
/// the caller can see but, on the merchant side, can never mutate (409). The admin wire projection ignores
/// it; only the merchant wire projection surfaces it, since only merchant roles can be merchant-owned.</summary>
public sealed record RoleListItem(
    Guid Id, string Code, string Name, string? Description, string? Color,
    RoleStatus Status, bool Shared, IReadOnlyList<string> PermissionKeys, int UserCount);
