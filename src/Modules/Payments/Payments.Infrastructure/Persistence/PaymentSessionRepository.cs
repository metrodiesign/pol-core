using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Payments.Application.Ports;
using Payments.Domain;

namespace Payments.Infrastructure.Persistence;

/// <summary>EF Core repository for <see cref="PaymentSession"/> over the shared producer data plane.</summary>
public sealed class PaymentSessionRepository : IPaymentSessionRepository
{
    private readonly ProducerDbContext _db;

    public PaymentSessionRepository(ProducerDbContext db) => _db = db;

    public void Add(PaymentSession session) => _db.Set<PaymentSession>().Add(session);

    public Task<PaymentSession?> GetByIdAsync(Guid paymentSessionId, CancellationToken cancellationToken) =>
        _db.Set<PaymentSession>()
            .FirstOrDefaultAsync(x => x.Id == paymentSessionId, cancellationToken);

    public Task<PaymentSession?> GetByExternalChargeAsync(
        PspCode psp,
        string externalChargeId,
        CancellationToken cancellationToken) =>
        _db.Set<PaymentSession>()
            .FirstOrDefaultAsync(
                x => x.Psp == psp && x.PspExternalChargeId == externalChargeId,
                cancellationToken);
}
