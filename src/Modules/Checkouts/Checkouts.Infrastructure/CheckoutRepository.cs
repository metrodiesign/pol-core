using BuildingBlocks.Infrastructure.Persistence;
using Checkouts.Application;
using Checkouts.Domain;
using Microsoft.EntityFrameworkCore;

namespace Checkouts.Infrastructure;

/// <summary>
/// EF Core adapter for <see cref="ICheckoutRepository"/> over the shared <see cref="PolDbContext"/>.
/// Scoped — it depends on the Scoped DbContext. Merchant isolation is enforced by the data-layer RLS
/// floor, so queries here are deliberately not re-scoped in SQL.
/// </summary>
public sealed class CheckoutRepository : ICheckoutRepository
{
    private readonly PolDbContext _db;

    public CheckoutRepository(PolDbContext db) => _db = db;

    public void Add(Session session) => _db.Set<Session>().Add(session);

    public Task<Session?> GetByIdAsync(Guid checkoutSessionId, CancellationToken cancellationToken) =>
        _db.Set<Session>().FirstOrDefaultAsync(x => x.Id == checkoutSessionId, cancellationToken);
}
