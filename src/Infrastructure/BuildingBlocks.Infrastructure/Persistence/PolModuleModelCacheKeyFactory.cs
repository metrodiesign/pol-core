using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Includes the migration composition's selected module namespaces in EF's model cache key. The migration
/// owner is deliberately composed with different module sets by design-time tooling and architecture tests;
/// caching only by context type would reuse a partial model and can surface an unmapped entity property.
/// </summary>
internal sealed class PolModuleModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) => context is PolDbContext pol
        ? (context.GetType(), pol.ModuleCacheKey, designTime)
        : (context.GetType(), designTime);
}
