using Microsoft.Extensions.DependencyInjection;
using Payments.Application.Ports;
using Payments.Infrastructure.Persistence;
using Payments.Infrastructure.Psp;

namespace Payments.Infrastructure;

/// <summary>
/// Wires the Payments module's infrastructure into the host container: repositories over the shared
/// producer data plane, the PSP adapters, and the adapter factory. Handlers are NOT registered here —
/// the source-generated Mediator in the host discovers them from this module's Application assembly.
/// The host registers <c>ModuleAssemblies.Producer</c> so the EF configurations in this assembly are
/// applied at model-build time.
/// </summary>
public static class PaymentsModuleRegistration
{
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services)
    {
        // Repositories depend on the Scoped ProducerDbContext, so they are Scoped too.
        services.AddScoped<IPaymentSessionRepository, PaymentSessionRepository>();
        services.AddScoped<IPspConnectionRepository, PspConnectionRepository>();

        // Adapters are stateless and make no real HTTP yet — safe as singletons.
        services.AddSingleton<IPspAdapter, TwoCTwoPAdapter>();
        services.AddSingleton<IPspAdapter, OmiseAdapter>();
        services.AddSingleton<IPspAdapterFactory, PspAdapterFactory>();

        return services;
    }
}
