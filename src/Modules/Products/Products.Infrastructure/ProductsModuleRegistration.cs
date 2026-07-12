using Microsoft.Extensions.DependencyInjection;
using Products.Application;

namespace Products.Infrastructure;

public static class ProductsModuleRegistration
{
    /// <summary>
    /// Registers the Products module's infrastructure. Mediator handlers are source-generated and
    /// auto-discovered from the Application assembly; the host adds this module's Infrastructure
    /// assembly to <c>HostModuleAssemblies.All</c> so <c>ProductConfiguration</c> is picked up at
    /// model-build time. Repository is Scoped (depends on the Scoped <c>PolDbContext</c>).
    /// </summary>
    public static IServiceCollection AddProductsModule(this IServiceCollection services)
    {
        services.AddScoped<IProductRepository, ProductRepository>();
        return services;
    }
}
