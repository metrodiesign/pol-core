using System.Reflection;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// The module assemblies whose <c>IEntityTypeConfiguration</c> classes the producer DbContext
/// applies at model-build time. Registered as a singleton by the host (the composition root —
/// the only place that may reference every module), so BuildingBlocks never takes a compile-time
/// dependency on a module.
/// </summary>
public sealed class ModuleAssemblies
{
    public ModuleAssemblies(IReadOnlyList<Assembly> modules) => Modules = modules;

    public IReadOnlyList<Assembly> Modules { get; }
}
