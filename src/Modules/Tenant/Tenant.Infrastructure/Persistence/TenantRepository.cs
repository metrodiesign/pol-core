using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Tenant.Application;
using DomainTenant = Tenant.Domain.Tenant;

namespace Tenant.Infrastructure.Persistence;

/// <summary>EF Core repository for <see cref="DomainTenant"/> over the shared producer data plane. The
/// host binds it to the pol_admin (RLS-bypass) connection so provisioning + read-back see every tenant.</summary>
public sealed class TenantRepository : ITenantRepository
{
    private readonly ProducerDbContext _db;

    public TenantRepository(ProducerDbContext db) => _db = db;

    public void Add(DomainTenant tenant) => _db.Set<DomainTenant>().Add(tenant);

    public Task<DomainTenant?> GetByCodeAsync(string normalizedCode, CancellationToken cancellationToken) =>
        _db.Set<DomainTenant>().FirstOrDefaultAsync(x => x.Code == normalizedCode, cancellationToken);

    public Task<bool> ExistsByCodeAsync(string normalizedCode, CancellationToken cancellationToken) =>
        _db.Set<DomainTenant>().AnyAsync(x => x.Code == normalizedCode, cancellationToken);
}
