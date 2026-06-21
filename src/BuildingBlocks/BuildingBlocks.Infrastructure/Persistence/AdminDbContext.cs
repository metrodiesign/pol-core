using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// The admin control plane (schema <c>admin</c>). Reached only through the Admin console's separate
/// DB principal, which RLS policy permits to cross tenant boundaries (PLAN decision #3). Module
/// entity mappings come from <see cref="ModuleAssemblies.Admin"/>.
/// </summary>
public sealed class AdminDbContext : DbContext
{
    public const string Schema = "admin";

    private readonly ModuleAssemblies _modules;

    public AdminDbContext(DbContextOptions<AdminDbContext> options, ModuleAssemblies modules)
        : base(options)
        => _modules = modules;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        foreach (var assembly in _modules.Admin)
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);

        base.OnModelCreating(modelBuilder);
    }
}
