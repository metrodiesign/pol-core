namespace Contracts;

using Mediator;
using Notifications.Application;

/// <summary>Control-plane registration decision event. Notification materialization owns delivery state.</summary>
public sealed record AgentRegistrationDecidedV1(
    Guid EventId,
    Guid RegistrationId,
    Guid AttemptId,
    Guid MerchantId,
    string Decision,
    string Email,
    string PhoneNumber,
    string? RejectionReason,
    DateTime OccurredAt) : INotification
{
    public const string EventType = "AgentRegistrationDecidedV1";
    public const string SchemaVersion = "1";
}

/// <summary>Task4 sink keeps the event publishable until Notification materialization is delivered in Task7.</summary>
public sealed class AgentRegistrationDecidedV1Handler(INotificationMaterializer materializer)
    : INotificationHandler<AgentRegistrationDecidedV1>
{
    public async ValueTask Handle(AgentRegistrationDecidedV1 notification, CancellationToken cancellationToken) =>
        await materializer.MaterializeAsync(new NotificationEvent(
            notification.EventId,
            notification.MerchantId,
            AgentRegistrationDecidedV1.EventType,
            System.Text.Json.JsonSerializer.Serialize(notification),
            notification.OccurredAt,
            notification.Email,
            notification.PhoneNumber,
            notification.RegistrationId,
            notification.AttemptId), cancellationToken);
}
