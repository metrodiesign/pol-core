using System.Text.Json.Serialization;
using Payments.Application.Ports;

namespace Payments.Infrastructure.Psp;

/// <summary>
/// The minimal shape the stub adapters parse out of a webhook body. Both stub PSPs accept the same
/// envelope so tests and the integration host can drive the path deterministically. The real adapters
/// will replace this with each PSP's verbatim payload shape.
/// </summary>
internal sealed record PspWebhookPayload(
    [property: JsonPropertyName("eventId")] string EventId,
    [property: JsonPropertyName("externalChargeId")] string ExternalChargeId,
    [property: JsonPropertyName("status")] string Status)
{
    /// <summary>Maps the verbatim status string to the normalized <see cref="PspChargeStatus"/>.</summary>
    public PspChargeStatus NormalizedStatus => Status switch
    {
        "paid" => PspChargeStatus.Paid,
        "failed" => PspChargeStatus.Failed,
        _ => PspChargeStatus.Pending,
    };
}
