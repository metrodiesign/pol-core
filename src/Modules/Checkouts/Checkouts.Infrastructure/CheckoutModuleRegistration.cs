using Microsoft.Extensions.DependencyInjection;

namespace Checkouts.Infrastructure;

/// <summary>
/// Wires the Checkout module's Infrastructure adapters. Handlers are auto-registered by the
/// Mediator source generator in the host. The repository moved to <c>Persistence.MerchantRuntime</c>
/// (task 8.5.3) — registered there via <c>AddMerchantRuntimePersistence</c>, not here.
/// </summary>
public static class CheckoutModuleRegistration
{
    public static IServiceCollection AddCheckoutModule(this IServiceCollection services) => services;
}
