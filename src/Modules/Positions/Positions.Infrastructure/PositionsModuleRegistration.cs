using Microsoft.Extensions.DependencyInjection;

namespace Positions.Infrastructure;

/// <summary>
/// Positions module wiring hook. Calling it forces this assembly to load so its EF configuration
/// (<c>cfg.Positions</c>) is discovered at model-build time via <c>HostModuleAssemblies.All</c> (the host adds
/// this assembly). NO store registered here: <c>IPositionStore</c> binds to <c>ControlPlaneDbContext</c> in
/// <c>Persistence.ControlPlane.ControlPlanePersistenceRegistration.AddControlPlanePersistence</c> — mirrors
/// <c>Iam.Infrastructure.IamModuleRegistration</c>'s identical no-op shape.
/// </summary>
public static class PositionsModuleRegistration
{
    public static IServiceCollection AddPositionsModule(this IServiceCollection services) => services;
}
