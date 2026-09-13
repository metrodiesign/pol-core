using NetArchTest.Rules;

namespace Architecture.Tests;

public class AdminArchitectureTests
{
    [Fact]
    public void Admin_Domain_does_not_depend_on_EntityFrameworkCore()
    {
        var result = TypesIn("Domain").Should().NotHaveDependencyOn("Microsoft.EntityFrameworkCore").GetResult();
        Assert.True(result.IsSuccessful, $"Admins.Domain must not depend on EF Core. {Offenders(result)}");
    }

    [Fact]
    public void Admin_Domain_does_not_depend_on_any_Infrastructure()
    {
        var result = TypesIn("Domain").Should().NotHaveDependencyOnAny(
            ["Products.Infrastructure", "Carts.Infrastructure", "Orders.Infrastructure", "Payments.Infrastructure",
             "Merchants.Infrastructure", "Admins.Infrastructure", "BuildingBlocks.Infrastructure", "Persistence"]).GetResult();
        Assert.True(result.IsSuccessful, $"Admins.Domain must not depend on infrastructure. {Offenders(result)}");
    }

    [Fact]
    public void Admin_Application_does_not_depend_on_the_Merchants_module()
    {
        var result = TypesIn("Application").Should().NotHaveDependencyOnAny(
            ["Merchants.Domain", "Merchants.Application", "Merchants.Infrastructure"]).GetResult();
        Assert.True(result.IsSuccessful, $"Admins.Application must not depend on Merchants. {Offenders(result)}");
    }

    [Fact]
    public void Admin_layers_do_not_depend_on_a_Host()
    {
        foreach (var layer in new[] { "Domain", "Application", "Infrastructure" })
        {
            var result = TypesIn(layer).Should().NotHaveDependencyOnAny(["Api", "Api"]).GetResult();
            Assert.True(result.IsSuccessful, $"Admins.{layer} must not depend on a host. {Offenders(result)}");
        }
    }

    private static PredicateList TypesIn(string layer) => Types.InAssembly(AssemblyFor(layer))
        .That().ResideInNamespaceStartingWith($"Admins.{layer}");

    private static System.Reflection.Assembly AssemblyFor(string layer) => layer switch
    {
        "Domain" => typeof(global::Admins.Domain.Users.User).Assembly,
        "Application" => typeof(global::Admins.Application.Users.IUserRepository).Assembly,
        "Infrastructure" => typeof(global::Admins.Infrastructure.AdminModuleRegistration).Assembly,
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, "Unknown layer"),
    };

    private static string Offenders(TestResult result) =>
        result.IsSuccessful ? "(none)" : "Offenders: " + string.Join(", ", result.FailingTypeNames ?? []);
}
