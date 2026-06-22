using System.Reflection;

using NetArchTest.Rules;

using DomainTenant = Tenant.Domain.Tenant;

namespace Architecture.Tests;

/// <summary>
/// Tenant is a PROVISIONING / composition module that sits ABOVE the five business modules: it is the one
/// place allowed to reference another module's Application (Payments.Application) so provisioning can create
/// a PspConnection + secret envelope. It is therefore deliberately NOT in
/// <see cref="ArchitectureBoundaryTests"/>'s <c>Modules</c> peer-ban set. The Clean-Architecture invariants
/// that STILL bind it are pinned here: its Domain stays pure (no EF Core, no Infrastructure) and no layer of
/// the module ever reaches up to a Host. (The admin-scoped keyed DI wiring is validated separately by the
/// host container boot in <c>HostContainerTests</c>.)
/// </summary>
public class TenantArchitectureTests
{
    private static readonly Assembly Domain = typeof(DomainTenant).Assembly;
    private static readonly Assembly Application = typeof(global::Tenant.Application.ITenantRepository).Assembly;
    private static readonly Assembly Infrastructure = typeof(global::Tenant.Infrastructure.TenantModuleRegistration).Assembly;

    [Fact]
    public void Tenant_Domain_does_not_depend_on_EntityFrameworkCore()
    {
        var result = Types.InAssembly(Domain)
            .Should().NotHaveDependencyOn("Microsoft.EntityFrameworkCore").GetResult();

        Assert.True(result.IsSuccessful, $"Tenant.Domain must not depend on EF Core. {Offenders(result)}");
    }

    [Fact]
    public void Tenant_Domain_does_not_depend_on_any_Infrastructure()
    {
        string[] forbidden =
        [
            "Products.Infrastructure", "Cart.Infrastructure", "Checkout.Infrastructure",
            "Orders.Infrastructure", "Payments.Infrastructure", "Tenant.Infrastructure",
            "BuildingBlocks.Infrastructure",
        ];

        var result = Types.InAssembly(Domain).Should().NotHaveDependencyOnAny(forbidden).GetResult();

        Assert.True(result.IsSuccessful, $"Tenant.Domain must not depend on any Infrastructure. {Offenders(result)}");
    }

    [Fact]
    public void Tenant_layers_do_not_depend_on_a_Host()
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
