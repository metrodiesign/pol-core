using System.Reflection;

using NetArchTest.Rules;

namespace Architecture.Tests;

/// <summary>
/// Identity (the TenantUser realm, reference 2.5) is a control-plane module. Like Tenant it is NOT in
/// <see cref="ArchitectureBoundaryTests"/>'s peer-ban set, but it is actually stricter: its Application
/// depends on NO other module — the <c>ITenantDirectory</c> port is implemented in the host, so Identity
/// never references Tenant. The Clean-Architecture invariants are pinned here: pure Domain (no EF Core, no
/// Infrastructure), no layer reaches a Host, and the Application stays free of any sibling module.
/// </summary>
public class IdentityArchitectureTests
{
    private static readonly Assembly Domain = typeof(global::Identity.Domain.TenantUser).Assembly;
    private static readonly Assembly Application = typeof(global::Identity.Application.ITenantUserRepository).Assembly;
    private static readonly Assembly Infrastructure = typeof(global::Identity.Infrastructure.IdentityModuleRegistration).Assembly;

    [Fact]
    public void Identity_Domain_does_not_depend_on_EntityFrameworkCore()
    {
        var result = Types.InAssembly(Domain)
            .Should().NotHaveDependencyOn("Microsoft.EntityFrameworkCore").GetResult();

        Assert.True(result.IsSuccessful, $"Identity.Domain must not depend on EF Core. {Offenders(result)}");
    }

    [Fact]
    public void Identity_Domain_does_not_depend_on_any_Infrastructure()
    {
        string[] forbidden =
        [
            "Products.Infrastructure", "Cart.Infrastructure", "Checkout.Infrastructure",
            "Orders.Infrastructure", "Payments.Infrastructure", "Tenant.Infrastructure",
            "Identity.Infrastructure", "BuildingBlocks.Infrastructure",
        ];

        var result = Types.InAssembly(Domain).Should().NotHaveDependencyOnAny(forbidden).GetResult();

        Assert.True(result.IsSuccessful, $"Identity.Domain must not depend on any Infrastructure. {Offenders(result)}");
    }

    [Fact]
    public void Identity_Application_does_not_depend_on_the_Tenant_module()
    {
        // The ITenantDirectory port is implemented in the host, so Identity stays decoupled from Tenant.
        string[] forbidden = ["Tenant.Domain", "Tenant.Application", "Tenant.Infrastructure"];

        var result = Types.InAssembly(Application).Should().NotHaveDependencyOnAny(forbidden).GetResult();

        Assert.True(result.IsSuccessful, $"Identity.Application must not depend on Tenant. {Offenders(result)}");
    }

    [Fact]
    public void Identity_layers_do_not_depend_on_a_Host()
    {
        foreach (var assembly in new[] { Domain, Application, Infrastructure })
        {
            var result = Types.InAssembly(assembly).Should().NotHaveDependencyOn("Api").GetResult();
            Assert.True(result.IsSuccessful, $"{assembly.GetName().Name} must not depend on a Host. {Offenders(result)}");
        }
    }

    private static string Offenders(TestResult result) =>
        result.IsSuccessful ? "(none)" : "Offenders: " + string.Join(", ", result.FailingTypeNames ?? []);
}
