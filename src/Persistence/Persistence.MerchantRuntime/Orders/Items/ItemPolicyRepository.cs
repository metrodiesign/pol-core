using Microsoft.EntityFrameworkCore;
using Orders.Application;
using Orders.Domain.Items;
using OrderItem = Orders.Domain.Items.Item;

namespace Persistence.MerchantRuntime.Orders.Items;

/// <summary>
/// EF Core implementation of <see cref="IItemPolicyRepository"/> over the MerchantRuntime data plane.
/// <see cref="ItemExistsAsync"/> queries <see cref="OrderItem"/> (not <see cref="ItemPolicy"/>) — the item's
/// own query filter is the merchant boundary (REQ-3.3), so another merchant's item reads as not-existing
/// regardless of whether a policy row exists for it. Scoped — depends on the Scoped DbContext.
/// </summary>
internal sealed class ItemPolicyRepository : IItemPolicyRepository
{
    private readonly MerchantRuntimeDbContext _db;

    public ItemPolicyRepository(MerchantRuntimeDbContext db) => _db = db;

    public Task<bool> ItemExistsAsync(Guid orderItemId, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<OrderItem>()
            .AnyAsync(i => i.Id == orderItemId, ct), cancellationToken);

    public Task<ItemPolicy?> GetPolicyByItemAsync(Guid orderItemId, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<ItemPolicy>()
            .FirstOrDefaultAsync(p => p.OrderItemId == orderItemId, ct), cancellationToken);

    public void Add(ItemPolicy policy) => _db.Set<ItemPolicy>().Add(policy);

    public void AddAudit(ItemPolicyAudit audit) => _db.Set<ItemPolicyAudit>().Add(audit);
}
