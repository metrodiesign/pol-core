namespace Payments.Application;

public sealed record AdminPaymentSessionResource(
    Guid PaymentSessionId, Guid MerchantId, Guid OrderId, long Version);

public interface IAdminPaymentSessionReader
{
    Task<AdminPaymentSessionResource?> ResolveAsync(
        Guid paymentSessionId,
        bool unrestricted,
        IReadOnlySet<Guid> accessibleMerchantIds,
        CancellationToken cancellationToken);
}
