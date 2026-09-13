using Accounts.Application;
using Accounts.Domain;
using Access.Domain;
using BuildingBlocks.Application;
using Checkouts.Application;
using Merchants.Domain;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane;

namespace Persistence.ControlPlane.Orders;

/// <summary>
/// Resolves ownership from the authenticated actor and merchant master. Agent sale claims are authoritative
/// for Agent ownership; Employee/SYSTEM explicit IDs are checked against this Merchant's master rows and
/// the Account's active branch scope. Null/null remains the explicit Merchant-owned case and never fabricates
/// a Sale or Branch.
/// </summary>
internal sealed class OrderOwnerResolver(
    ControlPlaneDbContext db,
    IActorContext actor,
    IIdentityAccessQuery identities) : IOrderOwnerResolver
{
    public async Task<ResolvedOrderOwner> ResolveAsync(
        Guid merchantId,
        Guid accountId,
        OrderOwnerRequest requested,
        CancellationToken cancellationToken)
    {
        if (!_actorBoundTo(merchantId, accountId))
            throw new AccessDeniedException("Order actor context is not verified.", "owner_context_denied");

        if (actor.UserId is { } identityAccount)
        {
            var authorization = await identities.ResolveAuthorizationAsync(
                identityAccount, merchantId, null, cancellationToken).ConfigureAwait(false);
            if (authorization is null
                || authorization.AccountStatus != AccountStatus.Active
                || authorization.MerchantId != merchantId
                || authorization.DataScope is null)
                throw new AccessDeniedException(
                    "The Account has no active authorization for this Merchant.", "owner_context_denied");

            if (authorization.AccountType == AccountType.Agent)
            {
                if (authorization.AgentSaleId is not { } agentSaleId)
                    throw new AccessDeniedException(
                        "The agent Sale owner could not be resolved.", "owner_sale_missing");

                var sale = await LoadSaleAsync(merchantId, agentSaleId, cancellationToken)
                    ?? throw new AccessDeniedException(
                        "The agent Sale owner could not be resolved.", "owner_sale_missing");
                if (requested.OwnerSaleId is { } requestedSale && requestedSale != sale.Id)
                    throw new AccessDeniedException(
                        "The requested Sale conflicts with the Account owner.", "owner_sale_conflict");
                if (requested.OwnerBranchId is { } requestedBranch && requestedBranch != sale.BranchId)
                    throw new AccessDeniedException(
                        "The requested Branch conflicts with the Account owner.", "owner_branch_conflict");
                return new ResolvedOrderOwner(sale.Id, sale.BranchId);
            }

            if (authorization.DataScope == DataScope.Self)
                throw new AccessDeniedException(
                    "This Account scope cannot assign an Order owner.", "owner_scope_denied");

            if (requested.OwnerSaleId is { } identitySaleId)
            {
                var sale = await LoadSaleAsync(merchantId, identitySaleId, cancellationToken)
                    ?? throw new AccessDeniedException(
                        "The requested Sale is outside the Merchant.", "owner_sale_denied");
                if (requested.OwnerBranchId is { } branchId && branchId != sale.BranchId)
                    throw new InvalidRequestException(
                        "Owner Branch does not match the Sale.", "owner_branch_mismatch");
                EnsureBranchScope(authorization, sale.BranchId);
                return new ResolvedOrderOwner(sale.Id, sale.BranchId);
            }

            if (requested.OwnerBranchId is { } identityBranchId)
            {
                var branchExists = await PlatformReadGuard.ReadAsync(ct => db.Branches.AsNoTracking()
                    .AnyAsync(x => x.MerchantId == merchantId && x.Id == identityBranchId, ct), cancellationToken)
                    .ConfigureAwait(false);
                if (!branchExists)
                    throw new AccessDeniedException(
                        "The requested Branch is outside the Merchant.", "owner_branch_denied");
                EnsureBranchScope(authorization, identityBranchId);
                return new ResolvedOrderOwner(null, identityBranchId);
            }

            return new ResolvedOrderOwner(null, null);
        }

        // Legacy Admin commerce calls have no Account identity. Their existing merchant-scope gate remains
        // authoritative, and the identity query is intentionally not consulted on this branch.
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
            var sale = await LoadSaleAsync(merchantId, saleId, cancellationToken)
                ?? throw new AccessDeniedException(
                    "The requested Sale is outside the Merchant.", "owner_sale_denied");
            if (requested.OwnerBranchId is { } branchId && branchId != sale.BranchId)
                throw new InvalidRequestException(
                    "Owner Branch does not match the Sale.", "owner_branch_mismatch");
            return new ResolvedOrderOwner(sale.Id, sale.BranchId);
        }

        if (requested.OwnerBranchId is { } requestedBranchId)
        {
            var branchExists = await PlatformReadGuard.ReadAsync(ct => db.Branches.AsNoTracking()
                .AnyAsync(x => x.MerchantId == merchantId && x.Id == requestedBranchId, ct), cancellationToken)
                .ConfigureAwait(false);
            if (!branchExists)
                throw new AccessDeniedException(
                    "The requested Branch is outside the Merchant.", "owner_branch_denied");
            return new ResolvedOrderOwner(null, requestedBranchId);
        }

        return new ResolvedOrderOwner(null, null);
    }

    private async Task<Sale?> LoadSaleAsync(
        Guid merchantId,
        Guid saleId,
        CancellationToken cancellationToken) =>
        await PlatformReadGuard.ReadAsync(ct => db.Sales.AsNoTracking()
            .FirstOrDefaultAsync(x => x.MerchantId == merchantId && x.Id == saleId, ct), cancellationToken)
            .ConfigureAwait(false);

    private static void EnsureBranchScope(AuthorizationSnapshot authorization, Guid branchId)
    {
        var allowed = authorization.DataScope switch
        {
            DataScope.Merchant => true,
            DataScope.Branch => authorization.BranchIds.Count == 1 && authorization.BranchIds.Contains(branchId),
            DataScope.AssignedBranches => authorization.BranchIds.Contains(branchId),
            _ => false,
        };
        if (!allowed)
            throw new AccessDeniedException(
                "The requested owner is outside the Account scope.", "owner_scope_denied");
    }

    private bool _actorBoundTo(Guid merchantId, Guid accountId) =>
        actor.HasActor && actor.MerchantId == merchantId
        && (actor.UserId is null || actor.UserId == accountId);
}
