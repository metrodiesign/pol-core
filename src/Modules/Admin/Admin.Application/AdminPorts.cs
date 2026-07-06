using Admin.Domain;
using BuildingBlocks.Application;

namespace Admin.Application;

/// <summary>
/// Persistence for the admin realm. Bound by the host to the pol_admin (RLS-bypass) connection — admin tables
/// are control-plane (no per-tenant predicate), and resolution/provisioning run cross-tenant. Lookups are by
/// subject (resolution), email (invite binding) and id (Super-only management); assignments back the
/// accessible-tenant set (REQ-6). The SFS-paged directory backs the account-management console
/// (admin-account-management REQ-1).
/// </summary>
public interface IAdminAccountRepository
{
    void Add(AdminAccount account);
    void AddAssignment(AdminTenantAssignment assignment);
    void RemoveAssignment(AdminTenantAssignment assignment);

    Task<AdminAccount?> GetBySubjectAsync(string subject, CancellationToken cancellationToken);
    Task<AdminAccount?> GetByEmailAsync(string email, CancellationToken cancellationToken);
    Task<AdminAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Cheap existence probe (<c>SELECT … EXISTS</c>) for the 404 gate on read/session handlers that
    /// need only to know the account exists, not its columns — avoids a full tracked-entity load.</summary>
    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlySet<Guid>> ListAssignedTenantIdsAsync(Guid adminAccountId, CancellationToken cancellationToken);
    Task<AdminTenantAssignment?> GetAssignmentAsync(Guid adminAccountId, Guid tenantId, CancellationToken cancellationToken);

    /// <summary>The SFS-paged admin directory (admin-account-management REQ-1): filter/search/sort applied over
    /// the control-plane <c>AdminAccount</c> set, <c>Total</c> counted after filter/search but before paging.</summary>
    Task<PagedResult<AdminAccountListItem>> ListAsync(PagedQuery query, CancellationToken cancellationToken);
}

/// <summary>One admin directory row (admin-account-management REQ-1.2). <see cref="Tier"/>/<see cref="Status"/>
/// are the enums; the host projects them to lowercase wire strings (no global enum converter, B2).
/// <see cref="SubjectBound"/> = the invite has been claimed (Subject != null, REQ-1.2).</summary>
public sealed record AdminAccountListItem(
    Guid AdminId, string Email, AdminTier Tier, AdminStatus Status, DateTime CreatedAt, bool SubjectBound);

/// <summary>Stages an append-only <see cref="AdminAccountAudit"/> in the current transaction (REQ-10.2).</summary>
public interface IAdminAccountAuditWriter
{
    void Append(AdminAccountAudit entry);
}

/// <summary>
/// Read-only tenant checks the admin module needs, implemented in the HOST over the pol_admin (bypass)
/// connection so Admin.Application stays free of a Tenant-module dependency (mirrors Identity's
/// <c>ITenantDirectory</c>). <see cref="IsActiveTenantAsync"/> validates a tenant at assignment time (REQ-4.3);
/// <see cref="GetCodesByIdsAsync"/> maps a Scoped admin's assigned ids to SPA-friendly codes for <c>GET
/// /admin/me</c> (REQ-13.3); <see cref="GetIdByCodeAsync"/> is the cheap code-&gt;id lookup the cross-tenant
/// read seam uses to apply the accessible-tenant floor BEFORE loading a full tenant projection (REQ-7.1).
/// </summary>
public interface IAdminTenantDirectory
{
    Task<bool> IsActiveTenantAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, string>> GetCodesByIdsAsync(IReadOnlySet<Guid> tenantIds, CancellationToken cancellationToken);
    Task<Guid?> GetIdByCodeAsync(string code, CancellationToken cancellationToken);
}
