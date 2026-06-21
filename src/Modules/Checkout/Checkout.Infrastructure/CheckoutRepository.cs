using BuildingBlocks.Infrastructure.Persistence;
using Checkout.Application;
using Checkout.Domain;
using Microsoft.EntityFrameworkCore;

namespace Checkout.Infrastructure;

/// <summary>
/// EF Core adapter for <see cref="ICheckoutRepository"/> over the shared <see cref="ProducerDbContext"/>.
/// Scoped — it depends on the Scoped DbContext. Tenant isolation is enforced by the data-layer RLS
/// floor, so queries here are deliberately not re-scoped in SQL.
/// </summary>
public sealed class CheckoutRepository : ICheckoutRepository
{
    private readonly ProducerDbContext _db;

    public CheckoutRepository(ProducerDbContext db) => _db = db;

    public void Add(CheckoutSession session) => _db.Set<CheckoutSession>().Add(session);

    public Task<CheckoutSession?> GetByIdAsync(Guid checkoutSessionId, CancellationToken cancellationToken) =>
        _db.Set<CheckoutSession>().FirstOrDefaultAsync(x => x.Id == checkoutSessionId, cancellationToken);
}
