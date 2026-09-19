using Notifications.Application;

namespace Api.Notifications;

/// <summary>Development/Testing only. Never register this sender outside a local test environment.</summary>
internal sealed class LoggingSmsSender(ILogger<LoggingSmsSender> logger) : ISmsSenderPort
{
    public bool IsConfigured => true;

    public Task<DeliveryProviderResult> SendAsync(SmsDeliveryRequest request, CancellationToken cancellationToken)
    {
        logger.LogInformation("DEV contact verification {DeliveryId}: {Body}", request.DeliveryId, request.Body);
        return Task.FromResult(new DeliveryProviderResult(DeliveryProviderOutcome.Accepted, $"dev-{request.DeliveryId:N}"));
    }
}
