using Admin.Domain;

namespace Admin.Application;

/// <summary>
/// Persistence for the admin realm. Bound by the host to the pol_admin (RLS-bypass) connection — admin tables
/// are control-plane (no per-tenant predicate), and resolution/provisioning run cross-tenant. Lookups are by
/// subject (resolution), email (invite binding) and id (Super-only management); assignments back the
/// accessible-tenant set (REQ-6).
/// </summary>
public interface IAdminAccountRepository
{
    void Add(AdminAccount account);
    void AddAssignment(AdminTenantAssignment assignment);
    void RemoveAssignment(AdminTenantAssignment assignment);

    Task<AdminAccount?> GetBySubjectAsync(string subject, CancellationToken cancellationToken);
    Task<AdminAccount?> GetByEmailAsync(string email, CancellationToken cancellationToken);
    Task<AdminAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlySet<Guid>> ListAssignedTenantIdsAsync(Guid adminAccountId, CancellationToken cancellationToken);
    Task<AdminTenantAssignment?> GetAssignmentAsync(Guid adminAccountId, Guid tenantId, CancellationToken cancellationToken);
}

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
/// /admin/me</c> (REQ-13.3).
/// </summary>
public interface IAdminTenantDirectory
{
    Task<bool> IsActiveTenantAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, string>> GetCodesByIdsAsync(IReadOnlySet<Guid> tenantIds, CancellationToken cancellationToken);
}
