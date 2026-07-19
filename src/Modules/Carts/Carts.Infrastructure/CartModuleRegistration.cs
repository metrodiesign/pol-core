using Microsoft.Extensions.DependencyInjection;

namespace Carts.Infrastructure;

/// <summary>
/// Composition entry point for the Cart module. The host calls this and adds this assembly to
/// <c>HostModuleAssemblies.All</c> so the shared <c>PolDbContext</c> discovers the cart entity
/// configurations at model-build time. Mediator handlers are auto-registered by the host's generator.
/// </summary>
public static class CartModuleRegistration
{
    /// <summary>The repository moved to <c>Persistence.MerchantRuntime</c> (task 8.5.3) — registered
    /// there via <c>AddMerchantRuntimePersistence</c>, not here.</summary>
    public static IServiceCollection AddCartModule(this IServiceCollection services) => services;
}
