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
    /// connection interceptor, the idempotency/outbox/vault stores (all Scoped — they depend on the
    /// Scoped <see cref="ProducerDbContext"/>, per PLAN decision #7) and the outbox dispatcher.
    /// Hosts still register the DbContexts and bind <see cref="VaultOptions"/>.
    /// </summary>
    public static IServiceCollection AddBuildingBlocksInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<SessionContextConnectionInterceptor>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();
        services.AddScoped<IVaultSecretStore, LocalEnvelopeVaultStore>();
        services.AddScoped<IOutbox, EfOutbox>();
        services.AddHostedService<OutboxDispatcher>();
        return services;
    }
}
