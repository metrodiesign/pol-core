using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Payments.Application.Ports.Psp;
using Payments.Domain.Psp;

namespace Payments.Infrastructure.Persistence.Psp;

/// <summary>EF Core repository for <see cref="Connection"/> over the shared txn data plane.</summary>
public sealed class ConnectionRepository : IConnectionRepository
{
    private readonly PolDbContext _db;

    public ConnectionRepository(PolDbContext db) => _db = db;

    public Task<Connection?> GetAsync(Guid merchantId, Code psp, CancellationToken cancellationToken) =>
        _db.Set<Connection>()
            .FirstOrDefaultAsync(x => x.MerchantId == merchantId && x.Psp == psp, cancellationToken);

    public Task<Connection?> GetByIdAsync(Guid pspConnectionId, CancellationToken cancellationToken) =>
        _db.Set<Connection>()
            .FirstOrDefaultAsync(x => x.Id == pspConnectionId, cancellationToken);

    public void Add(Connection connection) => _db.Set<Connection>().Add(connection);

    public async Task<IReadOnlyList<Connection>> ListByTenantAsync(Guid merchantId, CancellationToken cancellationToken) =>
        await _db.Set<Connection>()
            .Where(x => x.MerchantId == merchantId)
            .ToListAsync(cancellationToken);
}
