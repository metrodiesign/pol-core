using Microsoft.Extensions.DependencyInjection;
using Persistence.MerchantRuntime.Notifications;
using Persistence.MerchantRuntime.Payments;
using Persistence.ControlPlane.Maintenance;

namespace Persistence.MerchantRuntime.Outbox;

/// <summary>
/// Adds the outbox dispatcher hosted service. Called ONLY by the Worker host, whose principal holds
/// the cross-merchant outbox lease grant — deliberately separate from
/// <see cref="MerchantRuntimePersistenceRegistration.AddMerchantRuntimePersistence"/> so the Api host
/// (which calls that method too) never also runs the dispatcher.
/// </summary>
public static class OutboxDispatcherRegistration
{
    public static IServiceCollection AddMerchantRuntimeOutboxDispatcher(this IServiceCollection services)
    {
        services.AddHostedService<OutboxDispatcher>();
        services.AddHostedService<AdminControlMaintenanceService>();
        services.AddHostedService<TransactionInquiryWorker>();
        services.AddHostedService<NotificationDeliveryDispatcher>();
        return services;
    }
}
