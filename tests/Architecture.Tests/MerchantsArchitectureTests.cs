using System.Reflection;

using NetArchTest.Rules;

using DomainMerchant = Merchants.Domain.Merchant;

namespace Architecture.Tests;

/// <summary>
/// Merchants (rf1: the merged Tenant+Producer module) is a PROVISIONING / composition module that sits ABOVE the
/// five business modules: it is the one place allowed to reference another module's Application
/// (Payments.Application, ProvisionMerchantHandler) so provisioning can create a PspConnection + secret envelope.
/// It is therefore deliberately NOT in <see cref="ArchitectureBoundaryTests"/>'s <c>Modules</c> peer-ban set —
/// same standing exception the pre-merge Tenant module had. It is ALSO a control-plane actor module distinct
/// from Admin (the platform actor): the two must not depend on each other in either direction — the
/// IAdminMerchantDirectory port is implemented at the host, so Admin never references Merchants directly, and
/// Merchants never references Admin (mirrors <see cref="AdminArchitectureTests"/>, absorbs the pre-merge
/// Producer↔Admin boundary check). Domain stays pure (no EF Core, no Infrastructure); no layer reaches a Host.
/// </summary>
public class MerchantsArchitectureTests
{
    private static readonly Assembly Domain = typeof(DomainMerchant).Assembly;
    private static readonly Assembly Application = typeof(global::Merchants.Application.IMerchantRepository).Assembly;
    private static readonly Assembly Infrastructure = typeof(global::Merchants.Infrastructure.MerchantsModuleRegistration).Assembly;

    private static readonly Assembly AdminDomain = typeof(global::Admin.Domain.PlatformUser).Assembly;
    private static readonly Assembly AdminApplication = typeof(global::Admin.Application.IPlatformUserRepository).Assembly;
    private static readonly Assembly AdminInfrastructure = typeof(global::Admin.Infrastructure.AdminModuleRegistration).Assembly;

    [Fact]
    public void Merchants_Domain_does_not_depend_on_EntityFrameworkCore()
    {
        var result = Types.InAssembly(Domain)
            .Should().NotHaveDependencyOn("Microsoft.EntityFrameworkCore").GetResult();

        Assert.True(result.IsSuccessful, $"Merchants.Domain must not depend on EF Core. {Offenders(result)}");
    }

    [Fact]
    public void Merchants_Domain_does_not_depend_on_any_Infrastructure()
    {
        string[] forbidden =
        [
            "Products.Infrastructure", "Cart.Infrastructure", "Checkout.Infrastructure",
            "Orders.Infrastructure", "Payments.Infrastructure", "Merchants.Infrastructure",
            "Admin.Infrastructure", "BuildingBlocks.Infrastructure",
        ];

        var result = Types.InAssembly(Domain).Should().NotHaveDependencyOnAny(forbidden).GetResult();

        Assert.True(result.IsSuccessful, $"Merchants.Domain must not depend on any Infrastructure. {Offenders(result)}");
    }

    [Fact]
    public void Merchants_layers_do_not_depend_on_a_Host()
    {
        foreach (var assembly in new[] { Domain, Application, Infrastructure })
        {
            var result = Types.InAssembly(assembly).Should().NotHaveDependencyOn("Api").GetResult();
            Assert.True(result.IsSuccessful, $"{assembly.GetName().Name} must not depend on a Host. {Offenders(result)}");
        }
    }

    [Fact]
    public void Merchants_does_not_depend_on_the_Admin_module()
    {
        string[] forbidden = ["Admin.Domain", "Admin.Application", "Admin.Infrastructure"];

        foreach (var assembly in new[] { Domain, Application, Infrastructure })
        {
            var result = Types.InAssembly(assembly).Should().NotHaveDependencyOnAny(forbidden).GetResult();
            Assert.True(result.IsSuccessful,
                $"{assembly.GetName().Name} must not depend on the Admin module. {Offenders(result)}");
        }
    }

    [Fact]
    public void Admin_does_not_depend_on_the_Merchants_module()
    {
        string[] forbidden = ["Merchants.Domain", "Merchants.Application", "Merchants.Infrastructure"];

        foreach (var assembly in new[] { AdminDomain, AdminApplication, AdminInfrastructure })
        {
            var result = Types.InAssembly(assembly).Should().NotHaveDependencyOnAny(forbidden).GetResult();
            Assert.True(result.IsSuccessful,
                $"{assembly.GetName().Name} must not depend on the Merchants module. {Offenders(result)}");
        }
    }

    private static string Offenders(TestResult result) =>
        result.IsSuccessful ? "(none)" : "Offenders: " + string.Join(", ", result.FailingTypeNames ?? []);
}
