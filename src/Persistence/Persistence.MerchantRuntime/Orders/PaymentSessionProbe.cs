using Microsoft.EntityFrameworkCore;
using Orders.Application;
using Payments.Domain;

namespace Persistence.MerchantRuntime.Orders;

/// <summary>
/// EF Core implementation of <see cref="IPaymentSessionProbe"/> over the MerchantRuntime data plane. Lives
/// here (not in Orders) because this assembly already maps both clusters — the mirror of
/// <see cref="Payments.PayableOrderReader"/>. Runs inside cancel's transaction, AFTER the order row's
/// write lock is taken, which is what makes its answer good until commit.
/// </summary>
internal sealed class PaymentSessionProbe : IPaymentSessionProbe
{
    private readonly MerchantRuntimeDbContext _db;

    public PaymentSessionProbe(MerchantRuntimeDbContext db) => _db = db;

    public Task<bool> HasBlockingSessionAsync(Guid orderId, CancellationToken cancellationToken) =>
        _db.Set<Session>()
            .AnyAsync(
                x => x.OrderId == orderId
                    && (x.Status == SessionStatus.Created
                        || x.Status == SessionStatus.Redirected
                        || x.Status == SessionStatus.Paid),
                cancellationToken);
}
