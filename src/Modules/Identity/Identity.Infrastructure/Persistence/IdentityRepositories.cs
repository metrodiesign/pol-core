using BuildingBlocks.Infrastructure.Persistence;
using Identity.Application;
using Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Identity.Infrastructure.Persistence;

/// <summary>TenantUser realm persistence over the shared producer data plane. The host binds these to the
/// pol_admin (RLS-bypass) connection (registration/approval/resolve run before a tenant is bound).</summary>
public sealed class TenantUserRepository : ITenantUserRepository
{
    private readonly ProducerDbContext _db;

    public TenantUserRepository(ProducerDbContext db) => _db = db;

    public void Add(TenantUser user) => _db.Set<TenantUser>().Add(user);
    public void AddExternalLogin(ExternalLogin login) => _db.Set<ExternalLogin>().Add(login);
    public void AddProfile(TenantUserProfile profile) => _db.Set<TenantUserProfile>().Add(profile);

    public Task<TenantUser?> GetBySubjectAsync(string subject, CancellationToken cancellationToken) =>
        _db.Set<TenantUser>().FirstOrDefaultAsync(x => x.Subject == subject, cancellationToken);

    public Task<bool> ExistsBySubjectAsync(string subject, CancellationToken cancellationToken) =>
        _db.Set<TenantUser>().AnyAsync(x => x.Subject == subject, cancellationToken);
}

public sealed class RegistrationTicketStore : IRegistrationTicketStore
{
    private readonly ProducerDbContext _db;

    public RegistrationTicketStore(ProducerDbContext db) => _db = db;

    public void Add(RegistrationTicket ticket) => _db.Set<RegistrationTicket>().Add(ticket);

    public Task<RegistrationTicket?> GetAsync(Guid ticketId, CancellationToken cancellationToken) =>
        _db.Set<RegistrationTicket>().FirstOrDefaultAsync(x => x.Id == ticketId, cancellationToken);
}

public sealed class RegistrationAuditWriter : IRegistrationAuditWriter
{
    private readonly ProducerDbContext _db;

    public RegistrationAuditWriter(ProducerDbContext db) => _db = db;

    public void Append(RegistrationAudit entry) => _db.Set<RegistrationAudit>().Add(entry);
}
