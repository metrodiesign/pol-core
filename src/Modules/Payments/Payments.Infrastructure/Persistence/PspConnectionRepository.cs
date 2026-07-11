using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Payments.Application.Ports;
using Payments.Domain;

namespace Payments.Infrastructure.Persistence;

/// <summary>EF Core repository for <see cref="PspConnection"/> over the shared producer data plane.</summary>
public sealed class PspConnectionRepository : IPspConnectionRepository
{
    private readonly PolDbContext _db;

    public PspConnectionRepository(PolDbContext db) => _db = db;

    public Task<PspConnection?> GetAsync(Guid merchantId, PspCode psp, CancellationToken cancellationToken) =>
        _db.Set<PspConnection>()
            .FirstOrDefaultAsync(x => x.MerchantId == merchantId && x.Psp == psp, cancellationToken);

    public Task<PspConnection?> GetByIdAsync(Guid pspConnectionId, CancellationToken cancellationToken) =>
        _db.Set<PspConnection>()
            .FirstOrDefaultAsync(x => x.Id == pspConnectionId, cancellationToken);

    public void Add(PspConnection connection) => _db.Set<PspConnection>().Add(connection);

    public async Task<IReadOnlyList<PspConnection>> ListByTenantAsync(Guid merchantId, CancellationToken cancellationToken) =>
        await _db.Set<PspConnection>()
            .Where(x => x.MerchantId == merchantId)
            .ToListAsync(cancellationToken);
}
