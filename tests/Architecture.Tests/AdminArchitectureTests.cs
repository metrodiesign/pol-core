using System.Reflection;

using NetArchTest.Rules;

namespace Architecture.Tests;

/// <summary>
/// Admin (the control-plane actor) is a module distinct from Identity (the producer-side actor / data plane).
/// Its Application depends on NO other module — the <c>IAdminTenantDirectory</c> port is implemented in the
/// host, so Admin never references Tenant, and it must never reference Identity either (control plane and data
/// plane are separate by design). Pure Domain (no EF Core, no Infrastructure); no layer reaches a Host.
/// </summary>
public class AdminArchitectureTests
{
    private static readonly Assembly Domain = typeof(global::Admin.Domain.AdminAccount).Assembly;
    private static readonly Assembly Application = typeof(global::Admin.Application.IAdminAccountRepository).Assembly;
    private static readonly Assembly Infrastructure = typeof(global::Admin.Infrastructure.AdminModuleRegistration).Assembly;

    [Fact]
    public void Admin_Domain_does_not_depend_on_EntityFrameworkCore()
    {
        var result = Types.InAssembly(Domain)
            .Should().NotHaveDependencyOn("Microsoft.EntityFrameworkCore").GetResult();

        Assert.True(result.IsSuccessful, $"Admin.Domain must not depend on EF Core. {Offenders(result)}");
    }

    [Fact]
    public void Admin_Domain_does_not_depend_on_any_Infrastructure()
    {
        string[] forbidden =
        [
            "Products.Infrastructure", "Cart.Infrastructure", "Checkout.Infrastructure",
            "Orders.Infrastructure", "Payments.Infrastructure", "Tenant.Infrastructure",
            "Identity.Infrastructure", "Admin.Infrastructure", "BuildingBlocks.Infrastructure",
        ];

        var result = Types.InAssembly(Domain).Should().NotHaveDependencyOnAny(forbidden).GetResult();

        Assert.True(result.IsSuccessful, $"Admin.Domain must not depend on any Infrastructure. {Offenders(result)}");
    }

    [Fact]
    public void Admin_Application_does_not_depend_on_the_Tenant_or_Identity_modules()
    {
        // Control plane (Admin) is decoupled from the data plane (Identity/Tenant): the IAdminTenantDirectory
        // port is implemented in the host, so Admin.Application references neither module.
        string[] forbidden =
        [
            "Tenant.Domain", "Tenant.Application", "Tenant.Infrastructure",
            "Identity.Domain", "Identity.Application", "Identity.Infrastructure",
        ];

        var result = Types.InAssembly(Application).Should().NotHaveDependencyOnAny(forbidden).GetResult();

        Assert.True(result.IsSuccessful, $"Admin.Application must not depend on Tenant or Identity. {Offenders(result)}");
    }

    [Fact]
    public void Admin_layers_do_not_depend_on_a_Host()
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
