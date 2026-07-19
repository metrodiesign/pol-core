using Microsoft.Extensions.DependencyInjection;

namespace MasterData.Infrastructure;

/// <summary>
/// MasterData module wiring hook. Calling it forces this assembly to load so its EF configurations
/// (Positions, Offices, Levels, Divisions) are discovered at model-build time via
/// <c>HostModuleAssemblies.All</c> (the host adds this assembly).
/// <para>NO repository registered here (task 8.5.7): <c>IMasterDataStore</c> now binds to
/// <c>ControlPlaneDbContext</c> the same way every other control-plane seam does, registered directly by
/// <c>Persistence.ControlPlane.ControlPlanePersistenceRegistration.AddControlPlanePersistence</c> — mirrors
/// <c>Iam.Infrastructure.IamModuleRegistration</c>'s identical no-op shape.</para>
/// </summary>
public static class MasterDataModuleRegistration
{
    public static IServiceCollection AddMasterDataModule(this IServiceCollection services) => services;
}
