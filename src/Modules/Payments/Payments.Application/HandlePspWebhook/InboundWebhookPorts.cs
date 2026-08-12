using Payments.Domain;

namespace Payments.Application.HandlePspWebhook;

public sealed record InboundWebhookClaim(
    Guid EventId,
    InboundWebhookStatus Status,
    string PayloadFingerprint);

public interface IInboundWebhookRecorder
{
    Task RecordRejectedAsync(Guid connectionId, Guid merchantId, string pspCode,
        string payloadFingerprint, bool signatureValid, string failureCode, CancellationToken cancellationToken);
    Task<InboundWebhookClaim> ClaimAsync(Guid connectionId, Guid merchantId, string pspCode,
        string externalEventId, string payloadFingerprint, CancellationToken cancellationToken);
    Task<InboundWebhookEvent> LoadAsync(Guid eventId, CancellationToken cancellationToken);
}
