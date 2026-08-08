using System.Reflection;

using NetArchTest.Rules;

namespace Architecture.Tests;

/// <summary>
/// Admin (the platform control-plane actor) is a module distinct from Merchants (the merchant-side actor / data
/// plane, rf1: the merged Tenant+Producer module). Its Application depends on NO other module — the
/// <c>IAdminMerchantDirectory</c> port is implemented in the host, so Admin never references Merchants directly
/// (control plane and data plane are separate by design; the reverse direction is asserted in
/// <see cref="MerchantsArchitectureTests"/>). Pure Domain (no EF Core, no Infrastructure); no layer reaches a Host.
/// </summary>
public class AdminArchitectureTests
{
    private static readonly Assembly Domain = typeof(global::Admins.Domain.Users.User).Assembly;
    private static readonly Assembly Application = typeof(global::Admins.Application.Users.IUserRepository).Assembly;
    private static readonly Assembly Infrastructure = typeof(global::Admins.Infrastructure.AdminModuleRegistration).Assembly;

    [Fact]
    public void Admin_Domain_does_not_depend_on_EntityFrameworkCore()
    {
        var result = Types.InAssembly(Domain)
            .Should().NotHaveDependencyOn("Microsoft.EntityFrameworkCore").GetResult();

        Assert.True(result.IsSuccessful, $"Admins.Domain must not depend on EF Core. {Offenders(result)}");
    }

    [Fact]
    public void Admin_Domain_does_not_depend_on_any_Infrastructure()
    {
        string[] forbidden =
        [
            "Products.Infrastructure", "Carts.Infrastructure",
            "Orders.Infrastructure", "Payments.Infrastructure", "Merchants.Infrastructure",
            "Admins.Infrastructure", "BuildingBlocks.Infrastructure",
        ];
        AssertAllResolveToARealAssembly(forbidden);

        var result = Types.InAssembly(Domain).Should().NotHaveDependencyOnAny(forbidden).GetResult();

        Assert.True(result.IsSuccessful, $"Admins.Domain must not depend on any Infrastructure. {Offenders(result)}");
    }

    [Fact]
    public void Admin_Application_does_not_depend_on_the_Merchants_module()
    {
        // Control plane (Admin) is decoupled from the data plane (Merchants): the IAdminMerchantDirectory
        // port is implemented in the host, so Admins.Application references neither module.
        string[] forbidden = ["Merchants.Domain", "Merchants.Application", "Merchants.Infrastructure"];
        AssertAllResolveToARealAssembly(forbidden);

        var result = Types.InAssembly(Application).Should().NotHaveDependencyOnAny(forbidden).GetResult();

        Assert.True(result.IsSuccessful, $"Admins.Application must not depend on Merchants. {Offenders(result)}");
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

    // REQ-15.2: NotHaveDependencyOnAny only checks dependencies that exist; a name that resolves to NO assembly
    // at all (e.g. stale after a rename) makes the guard above pass vacuously instead of catching a real crossing.
    private static void AssertAllResolveToARealAssembly(string[] names)
    {
        foreach (var name in names)
        {
            Exception? failure = null;
            try { Assembly.Load(name); }
            catch (Exception ex) { failure = ex; }

            Assert.True(failure is null,
                $"'{name}' does not resolve to any loaded assembly ({failure?.Message}) — a stale or " +
                "mistyped name here would make NotHaveDependencyOnAny pass vacuously instead of guarding anything.");
        }
    }
}
