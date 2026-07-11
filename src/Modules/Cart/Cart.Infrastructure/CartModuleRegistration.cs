using Cart.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Cart.Infrastructure;

/// <summary>
/// Composition entry point for the Cart module. The host calls this and adds this assembly to
/// <c>ModuleAssemblies.Producer</c> so the shared <c>PolDbContext</c> discovers the cart entity
/// configurations at model-build time. Mediator handlers are auto-registered by the host's generator.
/// </summary>
public static class CartModuleRegistration
{
    /// <summary>Registers the Cart module's persistence adapters (Scoped — they depend on the
    /// Scoped <c>PolDbContext</c>).</summary>
    public static IServiceCollection AddCartModule(this IServiceCollection services)
    {
        services.AddScoped<ICartRepository, CartRepository>();
        return services;
    }
}
