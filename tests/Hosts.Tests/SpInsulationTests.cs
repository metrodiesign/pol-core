using System.Reflection;
using NetArchTest.Rules;
using Products.Application;
using Products.Application.Ports;

namespace Hosts.Tests;

/// <summary>
/// The anti-corruption layer, enforced (products-sp-gateway REQ-4.7/4.8/10.5). The <c>SpDocument*</c> types
/// are the VCentralPay wire contract, not ours: they may live in <c>Products.Application</c> (port, mapper,
/// handler) and <c>Products.Infrastructure</c> (adapter) and NOWHERE else, so the day the upstream adds a
/// column or renames a parameter, the change stops at those two assemblies instead of walking into the
/// Domain, the API response or another module.
/// <para>
/// Lives in Hosts.Tests because only this project references <c>Api</c> — the assembly most likely to reach
/// for a wire type by accident, and one Architecture.Tests cannot see. The scan covers PRODUCTION assemblies
/// only: test projects name the port legitimately (this file does, so do
/// <c>ProblemDetailsExceptionHandlerTests</c>, <c>FakeSpDocumentGateway</c> and the adapter's integration
/// tests), and a rule that failed on its own test doubles would just be turned off.
/// </para>
/// <para>
/// Fail-closed: the assembly list is asserted complete before anything is checked, so a guard that silently
/// scanned nothing (a renamed project, a dropped reference, an empty output directory) fails instead of
/// passing. Adding a project to the solution means adding its name here.
/// </para>
/// </summary>
public sealed class SpInsulationTests
{
    /// <summary>Every assembly built from <c>src/</c>. The two that own the wire contract are marked; the rest
    /// must not know it exists.</summary>
    private static readonly string[] ProductionAssemblies =
    [
        "Api",
        "Contracts",
        "SharedKernel",
        "BuildingBlocks.Application",
        "BuildingBlocks.Infrastructure",
        "BuildingBlocks.Web",
        "Persistence.ControlPlane",
        "Persistence.MerchantRuntime",
        "Persistence.MerchantUsers",
        "Persistence.Provisioning",
        "Admins.Application", "Admins.Domain", "Admins.Infrastructure",
        "Carts.Application", "Carts.Domain", "Carts.Infrastructure",
        "Checkouts.Application", "Checkouts.Domain", "Checkouts.Infrastructure",
        "Divisions.Application", "Divisions.Domain", "Divisions.Infrastructure",
        "Iam.Application", "Iam.Domain", "Iam.Infrastructure",
        "Levels.Application", "Levels.Domain", "Levels.Infrastructure",
        "Merchants.Application", "Merchants.Domain", "Merchants.Infrastructure",
        "Offices.Application", "Offices.Domain", "Offices.Infrastructure",
        "Orders.Application", "Orders.Domain", "Orders.Infrastructure",
        "Payments.Application", "Payments.Domain", "Payments.Infrastructure",
        "Positions.Application", "Positions.Domain", "Positions.Infrastructure",
        "Products.Application",      // owns the port + contracts + mapper
        "Products.Domain",
        "Products.Infrastructure",   // owns the adapter
    ];

    private static readonly string[] WireContractOwners = ["Products.Application", "Products.Infrastructure"];

    private static Assembly Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name + ".dll");
        Assert.True(File.Exists(path),
            $"{name}.dll is not in the test output directory — the insulation guard would scan nothing. " +
            "Either the project was renamed/removed (update this list) or a reference was dropped.");
        return Assembly.LoadFrom(path);
    }

    [Fact]
    public void The_wire_contract_stays_inside_the_two_assemblies_that_own_it()
    {
        var outsiders = ProductionAssemblies
            .Where(name => !WireContractOwners.Contains(name))
            .Select(Load)
            .ToArray();

        Assert.Equal(ProductionAssemblies.Length - WireContractOwners.Length, outsiders.Length);

        // IL-level (NetArchTest reads the bodies too), so a wire type used only inside a method still counts.
        var result = Types.InAssemblies(outsiders)
            .ShouldNot()
            .HaveDependencyOn(typeof(ISpDocumentGateway).Namespace)
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"{typeof(ISpDocumentGateway).Namespace} is the VCentralPay wire contract and must not leave " +
            "Products.Application/Products.Infrastructure (REQ-4.7). Offenders: " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    // REQ-4.7 again, from the other side: the two types the API answers with are ours end to end, so an
    // upstream column can never become part of the JSON contract by accident.
    [Theory]
    [InlineData(typeof(ProductPage))]
    [InlineData(typeof(ProductListItem))]
    public void The_answered_types_carry_no_wire_type_in_their_signature(Type type)
    {
        var signatureTypes = type.GetProperties()
            .Select(p => p.PropertyType)
            .Concat(type.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType))
            .SelectMany(t => t.IsGenericType ? t.GetGenericArguments().Append(t) : [t])
            .Distinct();

        foreach (var member in signatureTypes)
        {
            Assert.False(member.Namespace == typeof(ISpDocumentGateway).Namespace,
                $"{type.Name} exposes {member.Name} from the wire contract namespace.");
            Assert.False(member.Name.StartsWith("SpDocument", StringComparison.Ordinal),
                $"{type.Name} exposes the wire type {member.Name}.");
        }
    }
}
