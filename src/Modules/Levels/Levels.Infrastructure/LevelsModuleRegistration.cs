using Microsoft.Extensions.DependencyInjection;

namespace Levels.Infrastructure;

/// <summary>
/// Levels module wiring hook. Calling it forces this assembly to load so its EF configuration
/// (<c>cfg.Levels</c>) is discovered at model-build time via <c>HostModuleAssemblies.All</c> (the host adds
/// this assembly). NO store registered here: <c>ILevelStore</c> binds to <c>ControlPlaneDbContext</c> in
/// <c>Persistence.ControlPlane.ControlPlanePersistenceRegistration.AddControlPlanePersistence</c> — mirrors
/// <c>Iam.Infrastructure.IamModuleRegistration</c>'s identical no-op shape.
/// </summary>
public static class LevelsModuleRegistration
{
    public static IServiceCollection AddLevelsModule(this IServiceCollection services) => services;
}
