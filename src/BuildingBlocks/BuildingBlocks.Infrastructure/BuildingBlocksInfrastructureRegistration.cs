using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Infrastructure;

public static class BuildingBlocksInfrastructureRegistration
{
    /// <summary>
    /// Registers the cross-cutting infrastructure shared by all modules: the UTC clock and the
    /// ambient-actor binder. The idempotency/outbox/vault stores and
    /// the unit of work have moved to each runtime context's own <c>Persistence.*</c> registration
    /// extension (rls-to-query-filter task 8.5 — those adapters now depend on the internal runtime
    /// DbContexts, not the shared <see cref="PolDbContext"/>). Hosts still register the DbContexts and
    /// bind <see cref="VaultOptions"/>.
    /// </summary>
    public static IServiceCollection AddBuildingBlocksInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<AmbientActor>();
        services.AddScoped<IActorScope>(sp => sp.GetRequiredService<AmbientActor>());

        // Customer notification delivery. Default logs (no PII); a real email/SMS provider replaces it via
        // DI. Registered for every host so the outbox consumer resolves it (and Api's ValidateOnBuild passes).
        services.AddSingleton<INotificationSender, Notifications.LoggingNotificationSender>();

        // The keyring is immutable + host-lifetime: built and validated ONCE. The request-serving consoles
        // resolve it right after Build() so a bad master key crash-loops the host at boot (fail-fast); other
        // hosts (and that first resolve) build it lazily. Either way a bad key never reaches a successful
        // request — RevealAsync fails CLOSED and /health/ready reports not-ready. NB: ValidateOnBuild does
        // NOT execute a factory-registered singleton, so the explicit host resolve is what fails fast at boot.
        services.AddSingleton(sp => VaultKeyringFactory.Build(sp.GetRequiredService<IOptions<VaultOptions>>().Value));

        return services;
    }
}
