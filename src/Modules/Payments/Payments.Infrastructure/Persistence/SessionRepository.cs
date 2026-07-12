using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;

namespace Payments.Infrastructure.Persistence;

/// <summary>EF Core repository for <see cref="Session"/> over the shared txn data plane.</summary>
public sealed class SessionRepository : ISessionRepository
{
    private readonly PolDbContext _db;

    public SessionRepository(PolDbContext db) => _db = db;

    public void Add(Session session) => _db.Set<Session>().Add(session);

    public Task<Session?> GetByIdAsync(Guid paymentSessionId, CancellationToken cancellationToken) =>
        _db.Set<Session>()
            .FirstOrDefaultAsync(x => x.Id == paymentSessionId, cancellationToken);

    public Task<Session?> GetByExternalChargeAsync(
        Code psp,
        string externalChargeId,
        CancellationToken cancellationToken) =>
        _db.Set<Session>()
            .FirstOrDefaultAsync(
                x => x.Psp == psp && x.PspExternalChargeId == externalChargeId,
                cancellationToken);
}
