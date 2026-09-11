using BuildingBlocks.Application;
using Checkouts.Application;
using Merchants.Domain;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane;

namespace Persistence.ControlPlane.Orders;

/// <summary>
/// Resolves ownership from the authenticated actor and merchant master. Agent sale claims are authoritative
/// for Agent ownership; Employee/SYSTEM explicit IDs are checked against this Merchant's master rows.
/// Null/null remains the explicit Merchant-owned case and never fabricates a Sale or Branch.
/// </summary>
internal sealed class OrderOwnerResolver(ControlPlaneDbContext db, IActorContext actor) : IOrderOwnerResolver
{
    public async Task<ResolvedOrderOwner> ResolveAsync(
        Guid merchantId,
        Guid accountId,
        OrderOwnerRequest requested,
        CancellationToken cancellationToken)
    {
        if (!_actorBoundTo(merchantId, accountId))
            throw new AccessDeniedException("Order actor context is not verified.", "owner_context_denied");

        if (!string.IsNullOrWhiteSpace(actor.SaleCode))
        {
            var saleCode = actor.SaleCode.Trim().ToLowerInvariant();
            var sale = await PlatformReadGuard.ReadAsync(ct => db.Sales.AsNoTracking()
                .FirstOrDefaultAsync(x => x.MerchantId == merchantId && x.Code == saleCode, ct), cancellationToken)
                .ConfigureAwait(false);
            return sale is null
                ? throw new AccessDeniedException("The agent Sale owner could not be resolved.", "owner_sale_missing")
                : new ResolvedOrderOwner(sale.Id, sale.BranchId);
        }

        if (requested.OwnerSaleId is { } saleId)
        {
            var sale = await PlatformReadGuard.ReadAsync(ct => db.Sales.AsNoTracking()
                .FirstOrDefaultAsync(x => x.MerchantId == merchantId && x.Id == saleId, ct), cancellationToken)
                .ConfigureAwait(false);
            if (sale is null)
                throw new AccessDeniedException("The requested Sale is outside the Merchant.", "owner_sale_denied");
            if (requested.OwnerBranchId is { } branchId && branchId != sale.BranchId)
                throw new InvalidRequestException("Owner Branch does not match the Sale.", "owner_branch_mismatch");
            return new ResolvedOrderOwner(sale.Id, sale.BranchId);
        }

        if (requested.OwnerBranchId is { } requestedBranchId)
        {
            var branchExists = await PlatformReadGuard.ReadAsync(ct => db.Branches.AsNoTracking()
                .AnyAsync(x => x.MerchantId == merchantId && x.Id == requestedBranchId, ct), cancellationToken)
                .ConfigureAwait(false);
            if (!branchExists)
                throw new AccessDeniedException("The requested Branch is outside the Merchant.", "owner_branch_denied");
            return new ResolvedOrderOwner(null, requestedBranchId);
        }

        return new ResolvedOrderOwner(null, null);
    }

    private bool _actorBoundTo(Guid merchantId, Guid accountId) =>
        actor.HasActor && actor.MerchantId == merchantId
        && (actor.UserId is null || actor.UserId == accountId);
}
