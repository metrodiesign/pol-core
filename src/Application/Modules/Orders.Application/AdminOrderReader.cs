using BuildingBlocks.Application;

namespace Orders.Application;

public sealed record AdminOrderAccess(bool IsUnrestricted, IReadOnlySet<Guid> MerchantIds)
{
    public bool Allows(Guid merchantId) => IsUnrestricted || MerchantIds.Contains(merchantId);
}

public sealed record AdminOrderResource(
    Guid OrderId, Guid MerchantId, Guid? OriginatorId, Guid? PaymentSessionId, long Version);

public sealed record AdminOrderQuery(Guid? MerchantId, AdminOrderAccess Access) : PagedQuery;

/// <summary>Explicit-scope Admin order reads. Detail still dispatches the existing audited owner handler.</summary>
public interface IAdminOrderReader
{
    Task<PagedResult<OrderListItem>> ListAsync(AdminOrderQuery query, CancellationToken cancellationToken);
    Task<AdminOrderResource?> ResolveAsync(
        Guid orderId, AdminOrderAccess access, CancellationToken cancellationToken);
    Task<IReadOnlyList<OrderStatusTotal>> ReconciliationAsync(
        AdminOrderAccess access,
        Guid? merchantId,
        CancellationToken cancellationToken);
}
