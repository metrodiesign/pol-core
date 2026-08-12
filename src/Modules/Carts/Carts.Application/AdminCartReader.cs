namespace Carts.Application;

public sealed record AdminCartResource(
    Guid CartId, Guid MerchantId, Guid? OriginatorId, string? SaleCode, int Version);

/// <summary>Narrow pre-bind lookup used only to derive and authorize an Admin cart's tenant.</summary>
public interface IAdminCartReader
{
    Task<AdminCartResource?> ResolveAsync(
        Guid cartId,
        bool unrestricted,
        IReadOnlySet<Guid> accessibleMerchantIds,
        CancellationToken cancellationToken);
}
