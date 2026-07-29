using Microsoft.EntityFrameworkCore;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;

namespace Persistence.MerchantRuntime.Payments;

/// <summary>EF Core repository for <see cref="Session"/> over the MerchantRuntime data plane.</summary>
internal sealed class SessionRepository : ISessionRepository
{
    private readonly MerchantRuntimeDbContext _db;

    public SessionRepository(MerchantRuntimeDbContext db) => _db = db;

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

    // `||` rather than an `is ... or` pattern: an expression tree cannot contain pattern matching.
    public Task<Session?> GetOpenForOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        _db.Set<Session>()
            .FirstOrDefaultAsync(
                x => x.OrderId == orderId
                    && (x.Status == SessionStatus.Created || x.Status == SessionStatus.Redirected),
                cancellationToken);
}
