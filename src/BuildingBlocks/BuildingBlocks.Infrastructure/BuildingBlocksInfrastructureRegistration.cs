using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Idempotency;
using BuildingBlocks.Infrastructure.Outbox;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Infrastructure;

public static class BuildingBlocksInfrastructureRegistration
{
    /// <summary>
    /// Registers the cross-cutting infrastructure shared by all modules: the UTC clock, the RLS
    /// connection interceptor, the ambient-tenant binder, and the idempotency/outbox/vault stores
    /// (all Scoped — they depend on the Scoped <see cref="ProducerDbContext"/>, per PLAN decision #7).
    /// Hosts still register the DbContexts and bind <see cref="VaultOptions"/>. The outbox dispatcher
    /// is NOT registered here — it runs in the dedicated Worker host under its own DB principal so a
    /// tenant-facing process never holds the cross-tenant outbox read grant.
    /// </summary>
    public static IServiceCollection AddBuildingBlocksInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<SessionContextConnectionInterceptor>();
        services.AddScoped<AmbientTenant>();
        services.AddScoped<ITenantScope>(sp => sp.GetRequiredService<AmbientTenant>());
        services.AddScoped<IWebhookTenantResolver, WebhookTenantResolver>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();
        services.AddScoped<IVaultSecretStore, LocalEnvelopeVaultStore>();
        services.AddScoped<IOutbox, EfOutbox>();
        return services;
    }

    /// <summary>
    /// Adds the outbox dispatcher hosted service. Called ONLY by the Worker host, whose DbContext
    /// uses the <c>pol_worker</c> principal (cross-tenant outbox read, RLS-scoped consumer writes).
    /// </summary>
    public static IServiceCollection AddOutboxDispatcher(this IServiceCollection services)
    {
        services.AddHostedService<OutboxDispatcher>();
        return services;
    }
}
