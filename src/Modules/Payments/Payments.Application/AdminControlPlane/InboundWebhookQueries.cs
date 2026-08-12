using BuildingBlocks.Application;

namespace Payments.Application.AdminControlPlane;

public sealed record InboundWebhookAccess(bool IsUnrestricted, IReadOnlySet<Guid> MerchantIds);
public sealed record InboundWebhookQuery(int Page, int Limit, Guid? MerchantId, string? Psp,
    string? Status, string? Search, DateTime? From, DateTime? To, InboundWebhookAccess Access);
public sealed record InboundWebhookEventView(Guid Id, Guid PspConnectionId, Guid MerchantId,
    Guid? PaymentSessionId, Guid? OrderId, string Psp, string EventId, string PayloadFingerprint,
    bool SignatureValid, string Status, string? FailureCode, DateTime ReceivedAt, DateTime? ProcessedAt);

public interface IAdminInboundWebhookReader
{
    Task<PagedResult<InboundWebhookEventView>> ListAsync(
        InboundWebhookQuery query, CancellationToken cancellationToken);
    Task<InboundWebhookEventView?> GetAsync(
        Guid id, InboundWebhookAccess access, CancellationToken cancellationToken);
}
