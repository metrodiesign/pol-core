using System.IO;
using Merchants.Application.Users;
using Microsoft.Extensions.DependencyInjection;

namespace Merchants.Infrastructure;

/// <summary>
/// Merchants module wiring (merges the former Tenant + Producer module registrations, REQ-1.3). Loading this
/// assembly lets <c>PolDbContext</c> (the migration-owner) discover its EF configurations at design time via
/// <c>HostModuleAssemblies.All</c>.
/// <para>
/// NO repository/session-store/role-repo/unit-of-work registered here (task 8.5.7): every one of those ports
/// now binds to <c>MerchantUserDbContext</c>/<c>MerchantRuntimeDbContext</c>, registered directly by
/// <c>Persistence.MerchantUsers.MerchantUserPersistenceRegistration.AddMerchantUserPersistence</c> /
/// <c>Persistence.MerchantRuntime.MerchantRuntimePersistenceRegistration.AddMerchantRuntimePersistence</c> —
/// both hosts (Api, Worker) call those directly, so the old "default-context stub for Worker's
/// ValidateOnBuild, host overrides for Api" two-layer pattern no longer applies: there is only ONE
/// registration per port now, unkeyed, shared by both hosts (1-principal collapse, task 8).
/// </para>
/// </summary>
public static class MerchantsModuleRegistration
{
    public static IServiceCollection AddMerchantsModule(this IServiceCollection services)
    {
        // Default photo store (worker/local). The API overrides with a config-rooted one in its own host wiring.
        services.AddSingleton<IPhotoStore>(_ =>
            new LocalPhotoStore(Path.Combine(Path.GetTempPath(), "pol-merchants-photos")));

        return services;
    }
}
