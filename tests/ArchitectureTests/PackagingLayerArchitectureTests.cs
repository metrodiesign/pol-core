using System.Reflection;
using NetArchTest.Rules;

namespace Architecture.Tests;

/// <summary>
/// REQ-1.7: compilation boundaries are reduced to four projects, so these checks protect the
/// two inward layers and the companion fixture proves the MSBuild rule rejects a bad reference.
/// </summary>
public sealed class PackagingLayerArchitectureTests
{
    [Fact]
    public void Domain_does_not_depend_on_infrastructure_or_http_host()
    {
        AssertNoOuterDependency(typeof(SharedKernel.Money).Assembly, "Domain");
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure_or_http_host()
    {
        AssertNoOuterDependency(typeof(BuildingBlocks.Application.PagedResult<>).Assembly, "Application");
    }

    [Fact]
    public void Guard_rejects_a_real_outer_assembly_reference()
    {
        var exception = Record.Exception(() =>
            AssertNoOuterDependency(
                typeof(BuildingBlocks.Infrastructure.Persistence.PolDbContext).Assembly,
                "known-bad fixture"));

        Assert.NotNull(exception);
    }

    private static void AssertNoOuterDependency(Assembly assembly, string layer)
    {
        var referencedAssemblies = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is "Infrastructure" or "Api")
            .ToArray();

        Assert.True(referencedAssemblies.Length == 0,
            $"{layer} must not reference outer layer assemblies. Offenders: {string.Join(", ", referencedAssemblies)}");

        var result = Types.InAssembly(assembly)
            .Should().NotHaveDependencyOnAny(["BuildingBlocks.Infrastructure", "Persistence", "Api"])
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"{layer} must not name infrastructure or HTTP host namespaces. Offenders: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }
}
