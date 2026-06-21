using System.Reflection;

using NetArchTest.Rules;

namespace Architecture.Tests;

/// <summary>
/// PLAN #1 enforcement: Clean Architecture + Modular Monolith boundaries.
/// These tests assert OBSERVABLE assembly dependencies, not internals:
///   - a module's Domain+Application must NOT depend on another module's Domain/Application/Infrastructure;
///   - *.Domain must NOT depend on EF Core or on its own (or any) *.Infrastructure;
///   - no module assembly may depend on a Host.
/// Assemblies are resolved through a known type per module/layer/host (assembly name == project name).
/// </summary>
public class ArchitectureBoundaryTests
{
    // The five business modules. Each name is also the root namespace prefix for that module's
    // three layer assemblies (e.g. "Payments" -> Payments.Domain / .Application / .Infrastructure),
    // so a namespace-prefix dependency check on the bare module name covers all three layers.
    private static readonly string[] Modules =
    [
        "Products",
        "Cart",
        "Checkout",
        "Orders",
        "Payments",
    ];

    private static readonly string[] Hosts =
    [
        "TenantConsole",
        "AdminConsole",
    ];

    // One anchor type per module per layer. Loading the assembly via a real type is robust:
    // it guarantees the assembly is loaded and avoids brittle string-based Assembly.Load.
    private static Assembly DomainAssembly(string module) => module switch
    {
        "Products" => typeof(global::Products.Domain.Product).Assembly,
        "Cart" => typeof(global::Cart.Domain.Cart).Assembly,
        "Checkout" => typeof(global::Checkout.Domain.CheckoutSession).Assembly,
        "Orders" => typeof(global::Orders.Domain.Order).Assembly,
        "Payments" => typeof(global::Payments.Domain.PaymentSession).Assembly,
        _ => throw new ArgumentOutOfRangeException(nameof(module), module, "Unknown module"),
    };

    private static Assembly ApplicationAssembly(string module) => module switch
    {
        "Products" => typeof(global::Products.Application.IProductRepository).Assembly,
        "Cart" => typeof(global::Cart.Application.ICartRepository).Assembly,
        "Checkout" => typeof(global::Checkout.Application.ICheckoutRepository).Assembly,
        "Orders" => typeof(global::Orders.Application.IOrderRepository).Assembly,
        "Payments" => typeof(global::Payments.Application.Ports.IPspAdapter).Assembly,
        _ => throw new ArgumentOutOfRangeException(nameof(module), module, "Unknown module"),
    };

    private static Assembly InfrastructureAssembly(string module) => module switch
    {
        "Products" => typeof(global::Products.Infrastructure.ProductRepository).Assembly,
        "Cart" => typeof(global::Cart.Infrastructure.CartRepository).Assembly,
        "Checkout" => typeof(global::Checkout.Infrastructure.CheckoutRepository).Assembly,
        "Orders" => typeof(global::Orders.Infrastructure.OrderRepository).Assembly,
        "Payments" => typeof(global::Payments.Infrastructure.Psp.StubPspAdapter).Assembly,
        _ => throw new ArgumentOutOfRangeException(nameof(module), module, "Unknown module"),
    };

    // Every (Domain, Application) layer assembly of every module, with a label for failure messages.
    public static TheoryData<string, string> CoreLayerAssemblies()
    {
        var data = new TheoryData<string, string>();
        foreach (var module in Modules)
        {
            data.Add(module, "Domain");
            data.Add(module, "Application");
        }

        return data;
    }

    public static TheoryData<string> AllModules()
    {
        var data = new TheoryData<string>();
        foreach (var module in Modules)
        {
            data.Add(module);
        }

        return data;
    }

    private static Assembly CoreLayerAssembly(string module, string layer) => layer switch
    {
        "Domain" => DomainAssembly(module),
        "Application" => ApplicationAssembly(module),
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, "Unknown layer"),
    };

    /// <summary>
    /// Modules communicate ONLY through Contracts. A module's Domain or Application assembly must not
    /// take a dependency on ANY namespace belonging to a different module (any of its three layers).
    /// </summary>
    [Theory]
    [MemberData(nameof(CoreLayerAssemblies))]
    public void Module_core_assembly_does_not_depend_on_another_module(string module, string layer)
    {
        var assembly = CoreLayerAssembly(module, layer);

        var forbidden = Modules
            .Where(other => other != module)
            .ToArray();

        var result = Types.InAssembly(assembly)
            .Should()
            .NotHaveDependencyOnAny(forbidden)
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"{module}.{layer} must not depend on another module. Offenders: {Describe(result)}");
    }

    /// <summary>
    /// Belt-and-braces, layer-explicit form: a module's Domain+Application must not depend on another
    /// module's Domain, Application, OR Infrastructure namespace specifically.
    /// </summary>
    [Theory]
    [MemberData(nameof(CoreLayerAssemblies))]
    public void Module_core_assembly_does_not_depend_on_another_modules_layers(string module, string layer)
    {
        var assembly = CoreLayerAssembly(module, layer);

        var forbidden = Modules
            .Where(other => other != module)
            .SelectMany(other => new[] { $"{other}.Domain", $"{other}.Application", $"{other}.Infrastructure" })
            .ToArray();

        var result = Types.InAssembly(assembly)
            .Should()
            .NotHaveDependencyOnAny(forbidden)
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"{module}.{layer} crossed a module layer boundary. Offenders: {Describe(result)}");
    }

    /// <summary>
    /// Domain is the innermost ring: no persistence concerns. It must not reference EF Core.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllModules))]
    public void Domain_does_not_depend_on_EntityFrameworkCore(string module)
    {
        var result = Types.InAssembly(DomainAssembly(module))
            .Should()
            .NotHaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"{module}.Domain must not depend on Microsoft.EntityFrameworkCore. Offenders: {Describe(result)}");
    }

    /// <summary>
    /// Dependency direction Domain &lt;- Application &lt;- Infrastructure: Domain must not reach outward to
    /// ANY module's Infrastructure (including its own).
    /// </summary>
    [Theory]
    [MemberData(nameof(AllModules))]
    public void Domain_does_not_depend_on_any_Infrastructure(string module)
    {
        var forbidden = Modules
            .Select(other => $"{other}.Infrastructure")
            .ToArray();

        var result = Types.InAssembly(DomainAssembly(module))
            .Should()
            .NotHaveDependencyOnAny(forbidden)
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"{module}.Domain must not depend on any *.Infrastructure. Offenders: {Describe(result)}");
    }

    /// <summary>
    /// Hosts compose modules; modules never reference a Host. Checks all three layers of every module.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllModules))]
    public void Module_does_not_depend_on_any_Host(string module)
    {
        var assemblies = new[]
        {
            DomainAssembly(module),
            ApplicationAssembly(module),
            InfrastructureAssembly(module),
        };

        foreach (var assembly in assemblies)
        {
            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOnAny(Hosts)
                .GetResult();

            Assert.True(
                result.IsSuccessful,
                $"{assembly.GetName().Name} must not depend on a Host. Offenders: {Describe(result)}");
        }
    }

    private static string Describe(TestResult result)
    {
        if (result.IsSuccessful || result.FailingTypeNames is null)
        {
            return "(none)";
        }

        return string.Join(", ", result.FailingTypeNames);
    }
}
