using System.Reflection;
using NetArchTest.Rules;

namespace Architecture.Tests;

/// <summary>
/// Packaging keeps module namespaces while merging layer assemblies. These checks select a
/// module namespace before inspecting dependencies, so peer violations still fail after the merge.
/// </summary>
public class ArchitectureBoundaryTests
{
    private static readonly string[] Modules = ["Products", "Carts", "Orders", "Payments"];
    private static readonly string[] Layers = ["Domain", "Application"];

    public static TheoryData<string, string> CoreLayerAssemblies()
    {
        var data = new TheoryData<string, string>();
        foreach (var module in Modules)
        foreach (var layer in Layers)
            data.Add(module, layer);
        return data;
    }

    public static TheoryData<string> AllModules()
    {
        var data = new TheoryData<string>();
        foreach (var module in Modules)
            data.Add(module);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllModules))]
    public void Module_key_matches_its_real_assembly_names(string module)
    {
        Assert.Equal("Domain", AssemblyFor("Domain").GetName().Name);
        Assert.Equal("Application", AssemblyFor("Application").GetName().Name);
        Assert.Equal("Infrastructure", AssemblyFor("Infrastructure").GetName().Name);
        AssertScopeHasTypes(module, "Domain");
        AssertScopeHasTypes(module, "Application");
        AssertScopeHasTypes(module, "Infrastructure");
    }

    [Theory]
    [MemberData(nameof(CoreLayerAssemblies))]
    public void Module_core_assembly_does_not_depend_on_another_module(string module, string layer)
    {
        var forbidden = Modules.Where(other => other != module).ToArray();
        var result = TypesIn(module, layer).Should().NotHaveDependencyOnAny(forbidden).GetResult();
        Assert.True(result.IsSuccessful, $"{module}.{layer} must not depend on another module. {Describe(result)}");
    }

    [Theory]
    [MemberData(nameof(CoreLayerAssemblies))]
    public void Module_core_assembly_does_not_depend_on_another_modules_layers(string module, string layer)
    {
        var forbidden = Modules
            .Where(other => other != module)
            .SelectMany(other => new[] { $"{other}.Domain", $"{other}.Application", $"{other}.Infrastructure" })
            .ToArray();
        var result = TypesIn(module, layer).Should().NotHaveDependencyOnAny(forbidden).GetResult();
        Assert.True(result.IsSuccessful, $"{module}.{layer} crossed a module layer boundary. {Describe(result)}");
    }

    [Theory]
    [MemberData(nameof(AllModules))]
    public void Domain_does_not_depend_on_EntityFrameworkCore(string module)
    {
        var result = TypesIn(module, "Domain").Should().NotHaveDependencyOn("Microsoft.EntityFrameworkCore").GetResult();
        Assert.True(result.IsSuccessful, $"{module}.Domain must not depend on EF Core. {Describe(result)}");
    }

    [Theory]
    [MemberData(nameof(AllModules))]
    public void Domain_does_not_depend_on_any_Infrastructure(string module)
    {
        var forbidden = Modules.Select(other => $"{other}.Infrastructure")
            .Append("BuildingBlocks.Infrastructure")
            .Append("Persistence")
            .ToArray();
        var result = TypesIn(module, "Domain").Should().NotHaveDependencyOnAny(forbidden).GetResult();
        Assert.True(result.IsSuccessful, $"{module}.Domain must not depend on infrastructure. {Describe(result)}");
    }

    [Theory]
    [MemberData(nameof(AllModules))]
    public void Module_does_not_depend_on_any_Host(string module)
    {
        foreach (var layer in new[] { "Domain", "Application", "Infrastructure" })
        {
            var result = TypesIn(module, layer).Should().NotHaveDependencyOnAny(["Api", "Api"]).GetResult();
            Assert.True(result.IsSuccessful, $"{module}.{layer} must not depend on a host. {Describe(result)}");
        }
    }

    private static PredicateList TypesIn(string module, string layer) => Types.InAssembly(AssemblyFor(layer))
        .That().ResideInNamespaceStartingWith($"{module}.{layer}");

    private static Assembly AssemblyFor(string layer) => layer switch
    {
        "Domain" => typeof(global::Products.Domain.ProductGroup).Assembly,
        "Application" => typeof(global::Products.Application.Ports.ISpDocumentGateway).Assembly,
        "Infrastructure" => typeof(global::Products.Infrastructure.ProductsModuleRegistration).Assembly,
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, "Unknown layer"),
    };

    private static void AssertScopeHasTypes(string module, string layer) => Assert.Contains(
        AssemblyFor(layer).GetTypes(),
        type => type.Namespace?.StartsWith($"{module}.{layer}", StringComparison.Ordinal) == true);

    private static string Describe(TestResult result) =>
        result.IsSuccessful ? "(none)" : "Offenders: " + string.Join(", ", result.FailingTypeNames ?? []);
}
