using Admin.Application;
using Admin.Domain;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Admin.Infrastructure.Persistence;

/// <summary>Admin realm persistence over the shared producer data plane. The host binds these to the
/// pol_admin (RLS-bypass) connection — admin tables are control-plane (no per-tenant predicate) and
/// resolution/provisioning run cross-tenant.</summary>
public sealed class AdminAccountRepository : IAdminAccountRepository
{
    private readonly ProducerDbContext _db;

    public AdminAccountRepository(ProducerDbContext db) => _db = db;

    public void Add(AdminAccount account) => _db.Set<AdminAccount>().Add(account);
    public void AddAssignment(AdminTenantAssignment assignment) => _db.Set<AdminTenantAssignment>().Add(assignment);
    public void RemoveAssignment(AdminTenantAssignment assignment) => _db.Set<AdminTenantAssignment>().Remove(assignment);

    public Task<AdminAccount?> GetBySubjectAsync(string subject, CancellationToken cancellationToken) =>
        _db.Set<AdminAccount>().FirstOrDefaultAsync(x => x.Subject == subject, cancellationToken);

    public Task<AdminAccount?> GetByEmailAsync(string email, CancellationToken cancellationToken) =>
        _db.Set<AdminAccount>().FirstOrDefaultAsync(x => x.Email == email, cancellationToken);

    public Task<AdminAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Set<AdminAccount>().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlySet<Guid>> ListAssignedTenantIdsAsync(Guid adminAccountId, CancellationToken cancellationToken)
    {
        var ids = await _db.Set<AdminTenantAssignment>()
            .Where(x => x.AdminAccountId == adminAccountId)
            .Select(x => x.TenantId)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    public Task<AdminTenantAssignment?> GetAssignmentAsync(Guid adminAccountId, Guid tenantId, CancellationToken cancellationToken) =>
        _db.Set<AdminTenantAssignment>()
            .FirstOrDefaultAsync(x => x.AdminAccountId == adminAccountId && x.TenantId == tenantId, cancellationToken);
}

public sealed class AdminAccountAuditWriter : IAdminAccountAuditWriter
{
    private readonly ProducerDbContext _db;

    public AdminAccountAuditWriter(ProducerDbContext db) => _db = db;

    public void Append(AdminAccountAudit entry) => _db.Set<AdminAccountAudit>().Add(entry);
}
