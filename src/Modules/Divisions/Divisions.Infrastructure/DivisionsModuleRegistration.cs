using Microsoft.Extensions.DependencyInjection;

namespace Divisions.Infrastructure;

/// <summary>
/// Divisions module wiring hook. Calling it forces this assembly to load so its EF configuration
/// (<c>cfg.Divisions</c>) is discovered at model-build time via <c>HostModuleAssemblies.All</c> (the host adds
/// this assembly). NO store registered here: <c>IDivisionStore</c> binds to <c>ControlPlaneDbContext</c> in
/// <c>Persistence.ControlPlane.ControlPlanePersistenceRegistration.AddControlPlanePersistence</c> — mirrors
/// <c>Iam.Infrastructure.IamModuleRegistration</c>'s identical no-op shape.
/// </summary>
public static class DivisionsModuleRegistration
{
    public static IServiceCollection AddDivisionsModule(this IServiceCollection services) => services;
}
