using Microsoft.Extensions.DependencyInjection;
using Payments.Application.Confirmation;
using Payments.Application.Ports;
using Payments.Domain.Psp;
using Payments.Infrastructure.Psp;

namespace Payments.Infrastructure;

/// <summary>
/// Wires the Payments module's infrastructure into the host container: the PSP adapters and the
/// adapter factory. Handlers are NOT registered here — the source-generated Mediator in the host
/// discovers them from this module's Application assembly. The host registers this assembly in
/// <c>HostModuleAssemblies.All</c> so the EF configurations in this assembly are applied at
/// model-build time. The repositories moved to <c>Persistence.MerchantRuntime</c> (task 8.5.3) —
/// registered there via <c>AddMerchantRuntimePersistence</c>, not here.
/// </summary>
public static class PaymentsModuleRegistration
{
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services)
    {
        // Named pooled HttpClients (handler-rotated) so the singleton adapters can do real HTTP without
        // socket exhaustion. Per-call timeout only — charge-create never retries (single-shot in the
        // adapter so a timeout cannot double-charge); the fetch GET retries in the adapter. Keyed by the
        // PSP code string the adapter resolves via Psp.ToCode().
        services.AddHttpClient(Code.TwoCTwoP.ToCode(), c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(Code.Omise.ToCode(), c => c.Timeout = TimeSpan.FromSeconds(30));

        // Adapters are stateless (all per-call state is in method args) — safe as singletons consuming the
        // singleton IHttpClientFactory + IOptions<PspOptions>. Lifetime unchanged so PspAdapterFactory's
        // IEnumerable<IPspAdapter> wiring and the host ValidateOnBuild/ValidateScopes checks still pass.
        services.AddSingleton<IPspAdapter, TwoCTwoPAdapter>();
        services.AddSingleton<IPspAdapter, OmiseAdapter>();
        services.AddSingleton<IPspAdapterFactory, PspAdapterFactory>();

        // Owns the per-PSP secret envelope shape; stateless, consumed by merchant provisioning.
        services.AddSingleton<IPspSecretEnvelopeFactory, PspSecretEnvelopeFactory>();

        // The shared confirm line (webhook, payment-status, lazy expire, release). A concrete class, not a
        // port: it is application logic those handlers share, not a seam anything swaps. Scoped because it
        // rides the request's unit of work and repositories.
        services.AddScoped<PaymentConfirmationService>();

        return services;
    }
}
