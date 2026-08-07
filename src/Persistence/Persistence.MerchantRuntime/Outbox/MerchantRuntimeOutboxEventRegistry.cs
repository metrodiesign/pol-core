using System.Text.Json;
using System.Text.Json.Serialization;
using BuildingBlocks.Infrastructure.Outbox;
using Contracts;
using Mediator;

namespace Persistence.MerchantRuntime.Outbox;

/// <summary>Closed, versioned registry for every MerchantRuntime outbox contract.</summary>
internal static class MerchantRuntimeOutboxEventRegistry
{
    private sealed record Descriptor(string EventType, string SchemaVersion, Type ClrType);

    private static readonly Descriptor[] Descriptors =
    [
        new(PaymentPaid.EventType, PaymentPaid.SchemaVersion, typeof(PaymentPaid)),
        new(PaymentFailed.EventType, PaymentFailed.SchemaVersion, typeof(PaymentFailed)),
        new(PaymentExpired.EventType, PaymentExpired.SchemaVersion, typeof(PaymentExpired)),
        new(nameof(CustomerOrderNotification), CustomerOrderNotification.SchemaVersion, typeof(CustomerOrderNotification)),
    ];

    private static readonly IReadOnlyDictionary<Type, Descriptor> ByClrType =
        Descriptors.ToDictionary(x => x.ClrType);
    private static readonly IReadOnlyDictionary<string, Descriptor> ByEventType =
        Descriptors.ToDictionary(x => x.EventType, StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Options = NewOptions();

    public static (Guid EventId, string EventType, string SchemaVersion, DateTime OccurredAt, string Payload) Serialize(
        INotification notification,
        DateTime fallbackOccurredAt)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!ByClrType.TryGetValue(notification.GetType(), out var descriptor))
            throw new InvalidOperationException("Notification type is not registered for MerchantRuntime outbox.");

        var (eventId, occurredAt) = notification switch
        {
            PaymentPaid x => (x.EventId, x.OccurredAt),
            PaymentFailed x => (x.EventId, x.OccurredAt),
            PaymentExpired x => (x.EventId, x.OccurredAt),
            CustomerOrderNotification x => (Guid.CreateVersion7(), x.OccurredAt),
            _ => (Guid.CreateVersion7(), fallbackOccurredAt),
        };

        return (
            eventId,
            descriptor.EventType,
            descriptor.SchemaVersion,
            occurredAt,
            JsonSerializer.Serialize(notification, descriptor.ClrType, Options));
    }

    public static INotification Deserialize(string eventType, string schemaVersion, string payload)
    {
        if (!ByEventType.TryGetValue(eventType, out var descriptor)
            || !string.Equals(descriptor.SchemaVersion, schemaVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("MerchantRuntime outbox event type/version is not registered.");

        return (INotification)(JsonSerializer.Deserialize(payload, descriptor.ClrType, Options)
            ?? throw new JsonException("MerchantRuntime outbox payload cannot be null."));
    }

    private static JsonSerializerOptions NewOptions() => new(OutboxSerializer.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}
