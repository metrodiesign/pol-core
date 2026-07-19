using Microsoft.Extensions.DependencyInjection;

namespace Orders.Infrastructure;

/// <summary>
/// Composition root for the Orders module. Mediator handlers (the <see cref="CreateOrderHandler"/> and
/// the PaymentPaid consumer) are auto-discovered by the source generator in the host, so they are not
/// registered here. The host also adds this assembly to <c>HostModuleAssemblies.All</c> so
/// <c>PolDbContext</c> picks up <see cref="OrderConfiguration"/> at model-build time. The repository and
/// summary reader moved to <c>Persistence.MerchantRuntime</c> (task 8.5.3) — registered there via
/// <c>AddMerchantRuntimePersistence</c>, not here.
/// </summary>
public static class OrdersModuleRegistration
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services) => services;
}
