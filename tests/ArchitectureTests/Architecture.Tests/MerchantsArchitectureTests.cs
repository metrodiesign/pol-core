using NetArchTest.Rules;
using DomainMerchant = Merchants.Domain.Merchant;

namespace Architecture.Tests;

public class MerchantsArchitectureTests
{
    [Fact]
    public void Merchants_Domain_does_not_depend_on_EntityFrameworkCore()
    {
        var result = TypesIn("Merchants", "Domain").Should().NotHaveDependencyOn("Microsoft.EntityFrameworkCore").GetResult();
        Assert.True(result.IsSuccessful, $"Merchants.Domain must not depend on EF Core. {Offenders(result)}");
    }

    [Fact]
    public void Merchants_Domain_does_not_depend_on_any_Infrastructure()
    {
        var result = TypesIn("Merchants", "Domain").Should().NotHaveDependencyOnAny(
            ["Products.Infrastructure", "Carts.Infrastructure", "Orders.Infrastructure", "Payments.Infrastructure",
             "Merchants.Infrastructure", "Admins.Infrastructure", "BuildingBlocks.Infrastructure", "Persistence"]).GetResult();
        Assert.True(result.IsSuccessful, $"Merchants.Domain must not depend on infrastructure. {Offenders(result)}");
    }

    [Fact]
    public void Merchants_layers_do_not_depend_on_a_Host()
    {
        AssertNoHostDependency("Merchants");
    }

    [Fact]
    public void Merchants_does_not_depend_on_the_Admin_module()
    {
        AssertNoModuleDependency("Merchants", "Admins");
    }

    [Fact]
    public void Admin_does_not_depend_on_the_Merchants_module()
    {
        AssertNoModuleDependency("Admins", "Merchants");
    }

    private static void AssertNoHostDependency(string module)
    {
        foreach (var layer in new[] { "Domain", "Application", "Infrastructure" })
        {
            var result = TypesIn(module, layer).Should().NotHaveDependencyOnAny(["Api", "Api"]).GetResult();
            Assert.True(result.IsSuccessful, $"{module}.{layer} must not depend on a host. {Offenders(result)}");
        }
    }

    private static void AssertNoModuleDependency(string module, string forbiddenModule)
    {
        foreach (var layer in new[] { "Domain", "Application", "Infrastructure" })
        {
            var result = TypesIn(module, layer).Should().NotHaveDependencyOnAny(
                [$"{forbiddenModule}.Domain", $"{forbiddenModule}.Application", $"{forbiddenModule}.Infrastructure"]).GetResult();
            Assert.True(result.IsSuccessful, $"{module}.{layer} must not depend on {forbiddenModule}. {Offenders(result)}");
        }
    }

    private static PredicateList TypesIn(string module, string layer) => Types.InAssembly(AssemblyFor(layer))
        .That().ResideInNamespaceStartingWith($"{module}.{layer}");

    private static System.Reflection.Assembly AssemblyFor(string layer) => layer switch
    {
        "Domain" => typeof(DomainMerchant).Assembly,
        "Application" => typeof(global::Merchants.Application.IMerchantRepository).Assembly,
        "Infrastructure" => typeof(global::Merchants.Infrastructure.MerchantsModuleRegistration).Assembly,
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, "Unknown layer"),
    };

    private static string Offenders(TestResult result) =>
        result.IsSuccessful ? "(none)" : "Offenders: " + string.Join(", ", result.FailingTypeNames ?? []);
}
