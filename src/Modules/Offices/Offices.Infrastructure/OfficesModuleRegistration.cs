using Microsoft.Extensions.DependencyInjection;

namespace Offices.Infrastructure;

/// <summary>
/// Offices module wiring hook. Calling it forces this assembly to load so its EF configuration
/// (<c>cfg.Offices</c>) is discovered at model-build time via <c>HostModuleAssemblies.All</c> (the host adds
/// this assembly). NO store registered here: <c>IOfficeStore</c> binds to <c>ControlPlaneDbContext</c> in
/// <c>Persistence.ControlPlane.ControlPlanePersistenceRegistration.AddControlPlanePersistence</c> — mirrors
/// <c>Iam.Infrastructure.IamModuleRegistration</c>'s identical no-op shape.
/// </summary>
public static class OfficesModuleRegistration
{
    public static IServiceCollection AddOfficesModule(this IServiceCollection services) => services;
}
