using System.Reflection;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// The module marker types whose namespaces select <c>IEntityTypeConfiguration</c> classes for
/// the shared DbContext. Source packaging now combines module infrastructure into one assembly,
/// so assembly identity cannot select a module without broadening the EF model.
/// </summary>
public sealed class ModuleAssemblies
{
    public ModuleAssemblies(IReadOnlyList<Type> moduleMarkers)
    {
        ArgumentNullException.ThrowIfNull(moduleMarkers);
        ModuleNamespaces = moduleMarkers
            .Select(marker => marker.Namespace ?? throw new ArgumentException("Module marker must have a namespace."))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<string> ModuleNamespaces { get; }
}
