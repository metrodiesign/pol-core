using Microsoft.Extensions.DependencyInjection;
using Orders.Application;

namespace Orders.Infrastructure;

/// <summary>
/// Composition root for the Orders module. Registers the module's infrastructure adapters; Mediator
/// handlers (the <see cref="CreateOrderHandler"/> and the PaymentPaid consumer) are auto-discovered
/// by the source generator in the host, so they are not registered here. The host also adds this
/// assembly to <c>HostModuleAssemblies.All</c> so <c>PolDbContext</c> picks up
/// <see cref="OrderConfiguration"/> at model-build time.
/// </summary>
public static class OrdersModuleRegistration
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services)
    {
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IOrderSummaryReader, OrderSummaryReader>();
        return services;
    }
}
