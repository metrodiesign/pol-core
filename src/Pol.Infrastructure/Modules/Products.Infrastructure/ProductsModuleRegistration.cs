using Microsoft.Extensions.DependencyInjection;
using Products.Application.Ports;
using Products.Infrastructure.Sp;

namespace Products.Infrastructure;

public static class ProductsModuleRegistration
{
    /// <summary>
    /// Registers the Products module's infrastructure. Mediator handlers are source-generated and
    /// auto-discovered from the Application assembly; the host adds this module's Infrastructure
    /// assembly to <c>HostModuleAssemblies.All</c> so <c>ProductConfiguration</c> is picked up at
    /// model-build time. The repository moved to <c>Persistence.MerchantRuntime</c> (task 8.5.3) —
    /// registered there via <c>AddMerchantRuntimePersistence</c>, not here.
    /// <para>The document-search gateway is a singleton: it holds nothing but its options and logger, and
    /// opens a pooled connection per call. Its two connection strings are filled in by the host
    /// (<c>PostConfigure&lt;SpDocumentOptions&gt;</c>).</para>
    /// </summary>
    public static IServiceCollection AddProductsModule(this IServiceCollection services) =>
        services.AddSingleton<ISpDocumentGateway, SpDocumentGateway>();
}
