using System.Reflection;

using NetArchTest.Rules;

namespace Architecture.Tests;

/// <summary>
/// The Producer module (tenant-facing data plane) is built by DUPLICATING the Admin auth/RBAC stacks (copy-rename)
/// so the freshly-shipped Admin code stays untouched (plan D5). That separation is only real if a boundary test
/// enforces it: BEFORE this suite the Architecture.Tests did NOT forbid a Producer↔Admin dependency, so REQ-23.3
/// would be vacuous. These tests assert the OBSERVABLE assembly boundary in BOTH directions — <c>Producer.* ⇏
/// Admin.*</c> AND <c>Admin.* ⇏ Producer.*</c> — plus the inner-ring rules (Domain has no EF Core / Infrastructure,
/// no layer reaches a Host), mirroring <see cref="AdminArchitectureTests"/>.
/// </summary>
public class ProducerArchitectureTests
{
    // Anchor each assembly through a real type so it is guaranteed loaded. Producer.Application has no public types
    // in this slice (it lands in later tasks), so the boundary is asserted over the Domain + Infrastructure
    // assemblies that carry code; NetArchTest matches the "Producer."/"Admin." namespace prefixes either way.
    private static readonly Assembly ProducerDomain = typeof(global::Producer.Domain.TenantUser).Assembly;
    private static readonly Assembly ProducerInfrastructure = typeof(global::Producer.Infrastructure.ProducerModuleRegistration).Assembly;

    private static readonly Assembly AdminDomain = typeof(global::Admin.Domain.AdminAccount).Assembly;
    private static readonly Assembly AdminApplication = typeof(global::Admin.Application.IAdminAccountRepository).Assembly;
    private static readonly Assembly AdminInfrastructure = typeof(global::Admin.Infrastructure.AdminModuleRegistration).Assembly;

    [Fact]
    public void Producer_does_not_depend_on_the_Admin_module()
    {
        string[] forbidden = ["Admin.Domain", "Admin.Application", "Admin.Infrastructure"];

        foreach (var assembly in new[] { ProducerDomain, ProducerInfrastructure })
        {
            var result = Types.InAssembly(assembly).Should().NotHaveDependencyOnAny(forbidden).GetResult();
            Assert.True(result.IsSuccessful,
                $"{assembly.GetName().Name} must not depend on the Admin module (REQ-23.3). {Offenders(result)}");
        }
    }

    [Fact]
    public void Admin_does_not_depend_on_the_Producer_module()
    {
        string[] forbidden = ["Producer.Domain", "Producer.Application", "Producer.Infrastructure"];

        foreach (var assembly in new[] { AdminDomain, AdminApplication, AdminInfrastructure })
        {
            var result = Types.InAssembly(assembly).Should().NotHaveDependencyOnAny(forbidden).GetResult();
            Assert.True(result.IsSuccessful,
                $"{assembly.GetName().Name} must not depend on the Producer module (REQ-23.3). {Offenders(result)}");
        }
    }

    [Fact]
    public void Producer_Domain_does_not_depend_on_EntityFrameworkCore()
    {
        var result = Types.InAssembly(ProducerDomain)
            .Should().NotHaveDependencyOn("Microsoft.EntityFrameworkCore").GetResult();

        Assert.True(result.IsSuccessful, $"Producer.Domain must not depend on EF Core. {Offenders(result)}");
    }

    [Fact]
    public void Producer_Domain_does_not_depend_on_any_Infrastructure()
    {
        string[] forbidden =
        [
            "Products.Infrastructure", "Cart.Infrastructure", "Checkout.Infrastructure",
            "Orders.Infrastructure", "Payments.Infrastructure", "Tenant.Infrastructure",
            "Admin.Infrastructure", "Producer.Infrastructure", "BuildingBlocks.Infrastructure",
        ];

        var result = Types.InAssembly(ProducerDomain).Should().NotHaveDependencyOnAny(forbidden).GetResult();

        Assert.True(result.IsSuccessful, $"Producer.Domain must not depend on any Infrastructure. {Offenders(result)}");
    }

    [Fact]
    public void Producer_layers_do_not_depend_on_a_Host()
    {
        foreach (var assembly in new[] { ProducerDomain, ProducerInfrastructure })
        {
            var result = Types.InAssembly(assembly).Should().NotHaveDependencyOn("Api").GetResult();
            Assert.True(result.IsSuccessful, $"{assembly.GetName().Name} must not depend on a Host. {Offenders(result)}");
        }
    }

    private static string Offenders(TestResult result) =>
        result.IsSuccessful ? "(none)" : "Offenders: " + string.Join(", ", result.FailingTypeNames ?? []);
}
