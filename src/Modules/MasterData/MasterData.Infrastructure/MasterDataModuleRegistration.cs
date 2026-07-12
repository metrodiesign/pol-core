using MasterData.Application;
using MasterData.Infrastructure.Persistence;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace MasterData.Infrastructure;

/// <summary>
/// MasterData module wiring hook. Registers <see cref="IMasterDataStore"/> against the <see cref="PolDbContext"/>
/// the caller resolves (Admins uses the keyed "admin" bypass context) plus the keyed <c>"admin"</c>
/// <see cref="IUnitOfWork"/> the store commits through (S2) — that key is fixed today, not caller-configurable,
/// because only Admins uses this store. Calling it also forces this assembly to load so its EF configurations
/// (Positions, Offices, Levels, Divisions) are discovered at model-build time via
/// <c>HostModuleAssemblies.All</c> (the host adds this assembly).
/// </summary>
public static class MasterDataModuleRegistration
{
    public static IServiceCollection AddMasterDataModule(
        this IServiceCollection services, Func<IServiceProvider, PolDbContext> resolveDb)
    {
        services.AddScoped<IMasterDataStore>(sp =>
            new MasterDataStore(resolveDb(sp), sp.GetRequiredKeyedService<IUnitOfWork>("admin")));
        return services;
    }
}
