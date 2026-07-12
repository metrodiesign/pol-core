using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Infrastructure.Notifications;

/// <summary>
/// Default <see cref="INotificationSender"/>: logs that a summary link was dispatched, WITHOUT the
/// recipient handle or any PII (REQ-3.5) — only the order id, a non-secret identifier. A real email/SMS
/// provider replaces this via DI without touching the modules. Stateless, so a singleton.
/// </summary>
public sealed class LoggingNotificationSender : INotificationSender
{
    private readonly ILogger<LoggingNotificationSender> _logger;

    public LoggingNotificationSender(ILogger<LoggingNotificationSender> logger) => _logger = logger;

    public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Customer summary-link notification dispatched for order {OrderId} (merchant {MerchantId}).",
            message.OrderId, message.MerchantId);
        return Task.CompletedTask;
    }
}
