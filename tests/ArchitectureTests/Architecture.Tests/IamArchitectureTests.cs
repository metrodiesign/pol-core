using System.Reflection;
using NetArchTest.Rules;

namespace Architecture.Tests;

/// <summary>
/// Iam remains published language only through Iam.Domain. Namespace-scoped checks preserve
/// that rule after all module layers share three physical assemblies.
/// </summary>
public class IamArchitectureTests
{
    private static readonly string[] BusinessModules =
        ["Products", "Carts", "Orders", "Payments", "Admins", "Merchants"];

    private static readonly string[] ConfinedNamespaces =
    [
        "Iam.Infrastructure.Persistence.Roles",
        "Admins.Infrastructure.Persistence.Roles",
        "Merchants.Infrastructure.Persistence.Users.Roles",
        "Persistence.ControlPlane.Iam",
        "Persistence.ControlPlane.Admins",
        "Persistence.ControlPlane",
    ];

    private const string RoleEntity = "Iam.Domain.Roles.Role";

    [Fact]
    public void Iam_layer_keys_match_their_real_assembly_names()
    {
        Assert.Equal("Domain", AssemblyFor("Domain").GetName().Name);
        Assert.Equal("Application", AssemblyFor("Application").GetName().Name);
        Assert.Equal("Infrastructure", AssemblyFor("Infrastructure").GetName().Name);
        AssertScopeHasTypes("Domain");
        AssertScopeHasTypes("Application");
        AssertScopeHasTypes("Infrastructure");
    }

    [Fact]
    public void Iam_does_not_depend_on_any_business_module()
    {
        var forbidden = BusinessModules.SelectMany(module =>
            new[] { $"{module}.Domain", $"{module}.Application", $"{module}.Infrastructure" }).ToArray();
        foreach (var layer in new[] { "Domain", "Application", "Infrastructure" })
        {
            var result = TypesIn("Iam", layer).Should().NotHaveDependencyOnAny(forbidden).GetResult();
            Assert.True(result.IsSuccessful, $"Iam.{layer} must not depend on a business module. {Offenders(result)}");
        }
    }

    [Fact]
    public void Iam_layers_do_not_depend_on_a_Host()
    {
        foreach (var layer in new[] { "Domain", "Application", "Infrastructure" })
        {
            var result = TypesIn("Iam", layer).Should().NotHaveDependencyOnAny(["Api", "Api"]).GetResult();
            Assert.True(result.IsSuccessful, $"Iam.{layer} must not depend on a host. {Offenders(result)}");
        }
    }

    [Fact]
    public void Other_modules_reference_only_Iam_Domain_not_Application_or_Infrastructure()
    {
        foreach (var module in BusinessModules)
        foreach (var layer in new[] { "Domain", "Application", "Infrastructure" })
        {
            var result = TypesIn(module, layer).Should()
                .NotHaveDependencyOnAny(["Iam.Application", "Iam.Infrastructure"]).GetResult();
            Assert.True(result.IsSuccessful,
                $"{module}.{layer} may reference only Iam.Domain. {Offenders(result)}");
        }
    }

    [Fact]
    public void Only_the_store_and_resolution_repositories_query_iam_Roles()
    {
        var dependents = Types.InAssembly(AssemblyFor("Infrastructure"))
            .That().HaveDependencyOn(RoleEntity).GetTypes()
            .Where(type => IsProductionInfrastructure(type.Namespace))
            .ToList();

        Assert.Contains(dependents, _ => true);

        var offenders = dependents
            .Where(type => !ConfinedNamespaces.Any(ns => type.Namespace == ns))
            .Select(type => type.FullName ?? type.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"'{RoleEntity}' may be queried only from Iam store and resolution repositories. Offenders: {string.Join(", ", offenders)}");
    }

    private static bool IsProductionInfrastructure(string? typeNamespace) =>
        typeNamespace?.StartsWith("Persistence.", StringComparison.Ordinal) == true
        || BusinessModules.Append("Iam").Any(module =>
            typeNamespace?.StartsWith($"{module}.Infrastructure", StringComparison.Ordinal) == true);

    private static PredicateList TypesIn(string module, string layer) => Types.InAssembly(AssemblyFor(layer))
        .That().ResideInNamespaceStartingWith($"{module}.{layer}");

    private static Assembly AssemblyFor(string layer) => layer switch
    {
        "Domain" => typeof(global::Iam.Domain.Roles.Role).Assembly,
        "Application" => typeof(global::Iam.Application.Roles.RoleSideContext).Assembly,
        "Infrastructure" => typeof(global::Iam.Infrastructure.IamModuleRegistration).Assembly,
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, "Unknown layer"),
    };

    private static void AssertScopeHasTypes(string layer) => Assert.Contains(
        AssemblyFor(layer).GetTypes(),
        type => type.Namespace?.StartsWith($"Iam.{layer}", StringComparison.Ordinal) == true);

    private static string Offenders(TestResult result) =>
        result.IsSuccessful ? "(none)" : "Offenders: " + string.Join(", ", result.FailingTypeNames ?? []);
}
