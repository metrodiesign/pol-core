using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Payments.Application.Ports;
using Payments.Domain;

namespace Payments.Infrastructure.Persistence;

/// <summary>EF Core repository for <see cref="PspConnection"/> over the shared producer data plane.</summary>
public sealed class PspConnectionRepository : IPspConnectionRepository
{
    private readonly ProducerDbContext _db;

    public PspConnectionRepository(ProducerDbContext db) => _db = db;

    public Task<PspConnection?> GetAsync(Guid tenantId, PspCode psp, CancellationToken cancellationToken) =>
        _db.Set<PspConnection>()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Psp == psp, cancellationToken);

    public Task<PspConnection?> GetByIdAsync(Guid pspConnectionId, CancellationToken cancellationToken) =>
        _db.Set<PspConnection>()
            .FirstOrDefaultAsync(x => x.Id == pspConnectionId, cancellationToken);
}
