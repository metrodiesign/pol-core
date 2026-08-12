using Carts.Application;
using Microsoft.EntityFrameworkCore;
using CartAggregate = Carts.Domain.Cart;

namespace Persistence.MerchantRuntime.Carts;

internal sealed class AdminCartReader(MerchantRuntimeDbContext db) : IAdminCartReader
{
    public async Task<AdminCartResource?> ResolveAsync(
        Guid cartId,
        bool unrestricted,
        IReadOnlySet<Guid> accessibleMerchantIds,
        CancellationToken cancellationToken)
    {
        var row = await PlatformReadGuard.ReadAsync(ct => db.Set<CartAggregate>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.Id == cartId)
            .Select(x => new AdminCartResource(x.Id, x.MerchantId, x.OriginatorId, x.SaleCode, x.Version))
            .SingleOrDefaultAsync(ct), cancellationToken);
        return row is not null && (unrestricted || accessibleMerchantIds.Contains(row.MerchantId)) ? row : null;
    }
}
