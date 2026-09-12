using Checkouts.Domain;
using Orders.Domain;
using SharedKernel;

namespace Checkouts.Application;

public sealed record PaymentLinkToken(string RawToken, byte[] Hash);

public interface IPaymentLinkTokenService
{
    PaymentLinkToken Mint();
    byte[] Hash(string rawToken);
}

public sealed record CheckoutCapability(
    Guid OrderId,
    Guid LinkId,
    long OrderVersion,
    string Proof,
    string CsrfToken,
    DateTime ExpiresAt,
    string BrowserBindingId = "");

public interface ICheckoutCapabilityService
{
    CheckoutCapability Issue(Guid orderId, Guid linkId, long orderVersion, DateTime linkExpiresAt, DateTime now);
    bool TryRead(string proof, out CheckoutCapability capability);
}

public interface IPaymentLinkReplayProtector
{
    string Protect(string rawToken, DateTime expiresAt);
    string? Unprotect(string protectedToken);
}

public sealed record CheckoutLinkSnapshot(
    Guid LinkId,
    Guid MerchantId,
    Guid OrderId,
    PaymentLinkStatus LinkStatus,
    DateTime LinkCreatedAt,
    DateTime LinkExpiresAt,
    DateTime? LinkRevokedAt,
    long LinkVersion,
    OrderStatus OrderStatus,
    PaymentStatus PaymentStatus,
    long OrderVersion);

public sealed record CheckoutSummaryLine(
    string ProductCode,
    string VariantCode,
    string? VariantName,
    int Quantity,
    Money LineAmount);

public sealed record CheckoutOrderSnapshot(
    Guid OrderId,
    Guid LinkId,
    long OrderVersion,
    PaymentLinkStatus LinkStatus,
    DateTime LinkExpiresAt,
    string OrderNo,
    string? MerchantName,
    OrderStatus OrderStatus,
    PaymentStatus PaymentStatus,
    Money TotalAmount,
    IReadOnlyList<CheckoutSummaryLine> Lines,
    Guid MerchantId = default);

public interface ICustomerCheckoutReader
{
    Task<CheckoutLinkSnapshot?> FindByHashAsync(byte[] tokenHash, CancellationToken cancellationToken);
    Task<CheckoutOrderSnapshot?> GetSummaryAsync(Guid orderId, Guid linkId, CancellationToken cancellationToken);
}

public interface IPaymentLinkStore
{
    Task<PaymentLink?> GetByHashAsync(Guid merchantId, byte[] tokenHash, CancellationToken cancellationToken);
    Task<PaymentLink?> GetLinkAsync(Guid merchantId, Guid linkId, CancellationToken cancellationToken);
    Task<PaymentLink?> GetActiveForOrderAsync(Guid merchantId, Guid orderId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PaymentLink>> ListForOrderAsync(Guid merchantId, Guid orderId, CancellationToken cancellationToken);
    void Add(PaymentLink link);
}

public interface IPaymentLinkReplayStore
{
    Task<PaymentLinkReplay?> FindReplayAsync(Guid merchantId, string operation, string idempotencyKey,
        CancellationToken cancellationToken);
    void Add(PaymentLinkReplay replay);
}

public interface IOrderWorkflowStore
{
    Task<Order?> GetAsync(Guid merchantId, Guid orderId, CancellationToken cancellationToken);
    Task<Order?> GetForUpdateAsync(Guid merchantId, Guid orderId, CancellationToken cancellationToken);
    void Add(Order order);
}

public sealed record OrderOwnerRequest(Guid? OwnerSaleId, Guid? OwnerBranchId);

public sealed record ResolvedOrderOwner(Guid? OwnerSaleId, Guid? OwnerBranchId);

public interface IOrderOwnerResolver
{
    Task<ResolvedOrderOwner> ResolveAsync(Guid merchantId, Guid accountId, OrderOwnerRequest requested,
        CancellationToken cancellationToken);
}
