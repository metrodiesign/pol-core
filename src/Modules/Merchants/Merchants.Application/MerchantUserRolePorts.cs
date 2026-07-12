using Merchants.Domain;

namespace Merchants.Application;

/// <summary>
/// Persistence for the Merchant-User Role RBAC realm. Bound by the host to the pol_admin (RLS-bypass) keyed
/// <c>PolDbContext</c> — role/catalog tables are control-plane. Catalog reads back <c>GET /merchant-users/permissions</c>;
/// role + assignment reads back the management endpoints; <see cref="ListEffectivePermissionsAsync"/> backs
/// per-request resolution (REQ-16.4/17.1). Commits run through the keyed <c>IUnitOfWork</c>, never a repository
/// SaveChanges.
/// </summary>
public interface IMerchantUserRoleRepository
{
    void Add(MerchantUserRoleDefinition role);
    void Remove(MerchantUserRoleDefinition role);
    void AddAssignment(MerchantUserRoleAssignment assignment);
    void RemoveAssignment(MerchantUserRoleAssignment assignment);

    Task<MerchantUserRoleDefinition?> GetByCodeAsync(string code, CancellationToken cancellationToken);       // tracked, incl. permissions
    Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken);
    Task<int> CountAssignmentsForRoleAsync(Guid roleId, CancellationToken cancellationToken);

    Task<IReadOnlyList<MerchantUserRoleListItem>> ListAsync(CancellationToken cancellationToken);
    Task<MerchantUserRoleListItem?> GetListItemByCodeAsync(string code, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(
        IReadOnlyCollection<string> codes, CancellationToken cancellationToken);
    Task<IReadOnlySet<Guid>> ListRoleIdsForUserAsync(Guid merchantUserId, CancellationToken cancellationToken);
    Task<MerchantUserRoleAssignment?> GetAssignmentAsync(Guid merchantUserId, Guid roleId, CancellationToken cancellationToken);
    Task<bool> AssignmentExistsAsync(Guid merchantUserId, Guid roleId, CancellationToken cancellationToken);

    /// <summary>Catalog vocabulary used to validate role grants (REQ-16.2) — the live DB key set.</summary>
    Task<IReadOnlySet<string>> ListCatalogKeysAsync(CancellationToken cancellationToken);

    /// <summary>The full catalog (groups + permissions) for <c>GET /merchant-users/permissions</c> (REQ-15.4).</summary>
    Task<MerchantUserPermissionCatalogResult> ListCatalogAsync(CancellationToken cancellationToken);

    /// <summary>Union of permission keys over the merchant-user's ACTIVE assigned roles, scoped to the merchant the user was
    /// approved into (REQ-16.4). An Inactive role contributes nothing; zero active roles = zero permissions.</summary>
    Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(Guid merchantUserId, Guid merchantId, CancellationToken cancellationToken);

    /// <summary>The codes of the merchant-user's ACTIVE assigned roles in the given merchant — backs the <c>role</c> field of
    /// <c>GET /merchant-users/me</c> (REQ-17.5). Empty when none.</summary>
    Task<IReadOnlyList<string>> ListActiveRoleCodesForUserAsync(Guid merchantUserId, Guid merchantId, CancellationToken cancellationToken);
}

/// <summary>A role as the management endpoints render it (REQ-16). <see cref="Status"/> is the enum; the host
/// projects it to <c>"active"/"inactive"</c> on the wire.</summary>
public sealed record MerchantUserRoleListItem(
    string Code, string Name, string? Description, string? Color,
    MerchantUserRoleStatus Status, IReadOnlyList<string> PermissionKeys, int UserCount);

/// <summary>The permission catalog (REQ-15.4). <see cref="MerchantUserPermissionItem.Resource"/> = the permission's group key.</summary>
public sealed record MerchantUserPermissionCatalogResult(
    IReadOnlyList<MerchantUserPermissionGroupItem> Groups, IReadOnlyList<MerchantUserPermissionItem> Permissions);

public sealed record MerchantUserPermissionGroupItem(string Key, string LabelTh);

public sealed record MerchantUserPermissionItem(string Key, string LabelTh, string Resource);
