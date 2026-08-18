namespace Payments.Application.Ports;

/// <summary>
/// Transaction-owned authorization serialization. Implementations always acquire the global lock before
/// a Merchant lock, so cutover and capability/status/payment writers cannot interleave.
/// </summary>
public interface IPaymentAuthorizationLockManager
{
    Task AcquireGlobalExclusiveAsync(CancellationToken cancellationToken);
    Task AcquireMerchantSharedAsync(Guid merchantId, CancellationToken cancellationToken);
    Task AcquireMerchantExclusiveAsync(Guid merchantId, CancellationToken cancellationToken);
}
