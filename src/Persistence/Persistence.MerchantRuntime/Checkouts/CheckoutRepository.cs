using Checkouts.Application;
using Checkouts.Domain;
using Microsoft.EntityFrameworkCore;

namespace Persistence.MerchantRuntime.Checkouts;

/// <summary>
/// EF Core adapter for <see cref="ICheckoutRepository"/> over the MerchantRuntime data plane. Scoped —
/// it depends on the Scoped DbContext. Merchant isolation is enforced by the global query filter, so
/// queries here are deliberately not re-scoped.
/// </summary>
internal sealed class CheckoutRepository : ICheckoutRepository
{
    private readonly MerchantRuntimeDbContext _db;

    public CheckoutRepository(MerchantRuntimeDbContext db) => _db = db;

    public void Add(Session session) => _db.Set<Session>().Add(session);

    public Task<Session?> GetByIdAsync(Guid checkoutSessionId, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<Session>()
            .Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == checkoutSessionId, ct), cancellationToken);

    // Items are not needed to decide "is this cart already checking out", so this read stays narrow.
    public Task<Session?> GetOpenForCartAsync(Guid cartId, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<Session>().FirstOrDefaultAsync(
            x => x.CartId == cartId
                && (x.Status == SessionStatus.Started || x.Status == SessionStatus.Confirmed),
            ct), cancellationToken);
}
