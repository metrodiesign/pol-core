using System.Reflection;
using NetArchTest.Rules;
using Products.Application;
using Products.Application.Ports;

namespace Hosts.Tests;

/// <summary>
/// The VCentralPay wire contract stays in Products.Application and Products.Infrastructure.
/// Packaging merges assemblies, so this test filters violating types by their retained namespaces.
/// </summary>
public sealed class SpInsulationTests
{
    private static readonly (Assembly Assembly, string? OwnerNamespace)[] ProductionAssemblies =
    [
        (typeof(SharedKernel.Money).Assembly, null),
        (typeof(ProductPage).Assembly, "Products.Application"),
        (typeof(Products.Infrastructure.ProductsModuleRegistration).Assembly, "Products.Infrastructure"),
        (typeof(Api.PolDbContextFactory).Assembly, null),
    ];

    [Fact]
    public void The_wire_contract_stays_inside_the_two_assemblies_that_own_it()
    {
        var wireNamespace = typeof(ISpDocumentGateway).Namespace!;
        foreach (var (assembly, ownerNamespace) in ProductionAssemblies)
        {
            var violations = Types.InAssembly(assembly)
                .That().HaveDependencyOn(wireNamespace).GetTypes()
                .Where(type => ownerNamespace is null
                    || !(type.Namespace?.StartsWith(ownerNamespace, StringComparison.Ordinal) == true))
                .Select(type => type.FullName ?? type.Name)
                .ToArray();

            Assert.True(violations.Length == 0,
                $"{wireNamespace} must stay inside Products.Application/Products.Infrastructure. "
                + $"Assembly {assembly.GetName().Name} offenders: {string.Join(", ", violations)}");
        }
    }

    [Theory]
    [InlineData(typeof(ProductPage))]
    [InlineData(typeof(ProductListItem))]
    public void The_answered_types_carry_no_wire_type_in_their_signature(Type type)
    {
        var signatureTypes = type.GetProperties()
            .Select(property => property.PropertyType)
            .Concat(type.GetConstructors().SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.ParameterType))
            .SelectMany(candidate => candidate.IsGenericType ? candidate.GetGenericArguments().Append(candidate) : [candidate])
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
