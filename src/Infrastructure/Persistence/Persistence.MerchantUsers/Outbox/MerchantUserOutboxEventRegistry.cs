using System.Text.Json;
using System.Text.Json.Serialization;
using BuildingBlocks.Infrastructure.Outbox;
using Contracts;
using Mediator;

namespace Persistence.MerchantUsers.Outbox;

/// <summary>Closed event registry for native <c>merch.UserOutbox.Payload</c>.</summary>
internal static class MerchantUserOutboxEventRegistry
{
    private static readonly IReadOnlyDictionary<string, Type> EventTypes =
        new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            [nameof(MerchantUserRegistrationSubmitted)] = typeof(MerchantUserRegistrationSubmitted),
            [nameof(KycPhotoLifecycleRequested)] = typeof(KycPhotoLifecycleRequested),
            [nameof(MerchantUserInvitationDeliveryRequested)] = typeof(MerchantUserInvitationDeliveryRequested),
        };

    private static readonly JsonSerializerOptions Options = NewOptions();

    public static (string Type, string Payload) Serialize(INotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var type = notification.GetType();
        if (!EventTypes.TryGetValue(type.Name, out var registered) || registered != type)
            throw new InvalidOperationException("Notification type is not registered for the merchant-user outbox.");
        return (type.Name, JsonSerializer.Serialize(notification, type, Options));
    }

    public static INotification Deserialize(string typeName, string payload)
    {
        if (!EventTypes.TryGetValue(typeName, out var eventType))
            throw new InvalidOperationException("Merchant-user outbox type is not registered.");
        return (INotification)(JsonSerializer.Deserialize(payload, eventType, Options)
            ?? throw new JsonException("Merchant-user outbox payload cannot be null."));
    }

    private static JsonSerializerOptions NewOptions()
    {
        var options = new JsonSerializerOptions(OutboxSerializer.Options)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        return options;
    }
}
