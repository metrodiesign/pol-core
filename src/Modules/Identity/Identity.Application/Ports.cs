using Identity.Domain;

namespace Identity.Application;

/// <summary>
/// Persistence for the TenantUser realm. Bound by the host to the pol_admin (RLS-bypass) connection:
/// registration, approval AND the runtime resolve all run BEFORE a tenant is bound, so they need bypass
/// (mirrors the webhook tenant resolver). Lookup is by the stable Google subject.
/// </summary>
public interface ITenantUserRepository
{
    void Add(TenantUser user);
    void AddExternalLogin(ExternalLogin login);
    void AddProfile(TenantUserProfile profile);

    Task<TenantUser?> GetBySubjectAsync(string subject, CancellationToken cancellationToken);
    Task<bool> ExistsBySubjectAsync(string subject, CancellationToken cancellationToken);
}

/// <summary>Store for single-use registration tickets (admin-scoped). Consume mutates the tracked entity.</summary>
public interface IRegistrationTicketStore
{
    void Add(RegistrationTicket ticket);
    Task<RegistrationTicket?> GetAsync(Guid ticketId, CancellationToken cancellationToken);
}

/// <summary>Stages a <see cref="RegistrationAudit"/> in the current transaction (admin-scoped).</summary>
public interface IRegistrationAuditWriter
{
    void Append(RegistrationAudit entry);
}

/// <summary>
/// Read-only check that a tenant the admin selected at approval exists AND is active. Implemented in the
/// host over the Tenant module's admin repository, so Identity.Application stays free of a Tenant dependency.
/// </summary>
public interface ITenantDirectory
{
    Task<bool> IsActiveTenantAsync(Guid tenantId, CancellationToken cancellationToken);
}
