using Checkout.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Checkout.Infrastructure;

/// <summary>
/// Wires the Checkout module's Infrastructure adapters. Handlers are auto-registered by the
/// Mediator source generator in the host; this only registers the repository port. Scoped, since
/// it depends on the Scoped <c>ProducerDbContext</c>.
/// </summary>
public static class CheckoutModuleRegistration
{
    public static IServiceCollection AddCheckoutModule(this IServiceCollection services)
    {
        services.AddScoped<ICheckoutRepository, CheckoutRepository>();
        return services;
    }
}
