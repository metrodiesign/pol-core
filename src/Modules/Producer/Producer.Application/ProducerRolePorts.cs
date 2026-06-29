using Producer.Domain;

namespace Producer.Application;

/// <summary>
/// Persistence for the Producer Role RBAC realm. Bound by the host to the pol_admin (RLS-bypass) keyed
/// <c>ProducerDbContext</c> — role/catalog tables are control-plane. Catalog reads back <c>GET /producer/permissions</c>;
/// role + assignment reads back the management endpoints; <see cref="ListEffectivePermissionsAsync"/> backs
/// per-request resolution (REQ-16.4/17.1). Commits run through the keyed <c>IUnitOfWork</c>, never a repository
/// SaveChanges.
/// </summary>
// ponytail: DUPLICATE of Admin.Application.IAdminRoleRepository — deliberate debt, do not refactor into a shared base.
public interface IProducerRoleRepository
{
    void Add(ProducerRole role);
    void Remove(ProducerRole role);
    void AddAssignment(ProducerRoleAssignment assignment);
    void RemoveAssignment(ProducerRoleAssignment assignment);

    Task<ProducerRole?> GetByCodeAsync(string code, CancellationToken cancellationToken);       // tracked, incl. permissions
    Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken);
    Task<int> CountAssignmentsForRoleAsync(Guid roleId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProducerRoleListItem>> ListAsync(CancellationToken cancellationToken);
    Task<ProducerRoleListItem?> GetListItemByCodeAsync(string code, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(
        IReadOnlyCollection<string> codes, CancellationToken cancellationToken);
    Task<IReadOnlySet<Guid>> ListRoleIdsForUserAsync(Guid tenantUserId, CancellationToken cancellationToken);
    Task<ProducerRoleAssignment?> GetAssignmentAsync(Guid tenantUserId, Guid roleId, CancellationToken cancellationToken);
    Task<bool> AssignmentExistsAsync(Guid tenantUserId, Guid roleId, CancellationToken cancellationToken);

    /// <summary>Catalog vocabulary used to validate role grants (REQ-16.2) — the live DB key set.</summary>
    Task<IReadOnlySet<string>> ListCatalogKeysAsync(CancellationToken cancellationToken);

    /// <summary>The full catalog (groups + permissions) for <c>GET /producer/permissions</c> (REQ-15.4).</summary>
    Task<ProducerPermissionCatalogResult> ListCatalogAsync(CancellationToken cancellationToken);

    /// <summary>Union of permission keys over the producer's ACTIVE assigned roles, scoped to the tenant the user was
    /// approved into (REQ-16.4). An Inactive role contributes nothing; zero active roles = zero permissions.</summary>
    Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(Guid tenantUserId, Guid tenantId, CancellationToken cancellationToken);

    /// <summary>The codes of the producer's ACTIVE assigned roles in the given tenant — backs the <c>role</c> field of
    /// <c>GET /producer/me</c> (REQ-17.5). Empty when none.</summary>
    Task<IReadOnlyList<string>> ListActiveRoleCodesForUserAsync(Guid tenantUserId, Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>A role as the management endpoints render it (REQ-16). <see cref="Status"/> is the enum; the host
/// projects it to <c>"active"/"inactive"</c> on the wire.</summary>
public sealed record ProducerRoleListItem(
    string Code, string Name, string? Description, string? Color,
    ProducerRoleStatus Status, IReadOnlyList<string> PermissionKeys, int UserCount);

/// <summary>The permission catalog (REQ-15.4). <see cref="ProducerPermissionItem.Resource"/> = the permission's group key.</summary>
public sealed record ProducerPermissionCatalogResult(
    IReadOnlyList<ProducerPermissionGroupItem> Groups, IReadOnlyList<ProducerPermissionItem> Permissions);

public sealed record ProducerPermissionGroupItem(string Key, string LabelTh);

public sealed record ProducerPermissionItem(string Key, string LabelTh, string Resource);
