using System.Reflection;

using NetArchTest.Rules;

namespace Architecture.Tests;

/// <summary>
/// masterdata-module (design.md §1 "การตัดสินที่สำคัญ"): <c>MasterData.Domain</c> is a PUBLISHED LANGUAGE —
/// <c>Admins.Application</c> may reference it directly (Position/Office/Level/Division for the profile-FK
/// lookup port), like SharedKernel — but never <c>MasterData.Application</c>/<c>MasterData.Infrastructure</c>
/// (REQ-4.1). The reverse direction is a hard leaf: MasterData does not know its caller at all — neither
/// Domain nor Application may reference any layer of <c>Admins</c> (REQ-4.2). Domain itself stays pure:
/// no EF Core, no Infrastructure of any module (REQ-4.3).
/// </summary>
public class MasterDataArchitectureTests
{
    private static readonly Assembly MasterDataDomain = typeof(global::MasterData.Domain.Positions.Position).Assembly;
    private static readonly Assembly MasterDataApplication = typeof(global::MasterData.Application.IMasterDataStore).Assembly;
    private static readonly Assembly MasterDataInfrastructure = typeof(global::MasterData.Infrastructure.MasterDataModuleRegistration).Assembly;

    private static readonly Assembly AdminsDomain = typeof(global::Admins.Domain.Users.User).Assembly;
    private static readonly Assembly AdminsApplication = typeof(global::Admins.Application.Users.IUserRepository).Assembly;

    // REQ-15.2 precedent (ArchitectureBoundaryTests/AdminArchitectureTests/IamArchitectureTests): every
    // forbidden-namespace string below is derived from a key, and NotHaveDependencyOnAny passes vacuously
    // on a name that resolves to NO assembly — a stale or mistyped key would silently kill the guard.
    [Fact]
    public void MasterData_layer_keys_match_their_real_assembly_names()
    {
        Assert.Equal("MasterData.Domain", MasterDataDomain.GetName().Name);
        Assert.Equal("MasterData.Application", MasterDataApplication.GetName().Name);
        Assert.Equal("MasterData.Infrastructure", MasterDataInfrastructure.GetName().Name);
    }

    [Fact]
    public void MasterData_Domain_does_not_depend_on_EntityFrameworkCore()
    {
        var result = Types.InAssembly(MasterDataDomain)
            .Should().NotHaveDependencyOn("Microsoft.EntityFrameworkCore").GetResult();

        Assert.True(result.IsSuccessful, $"MasterData.Domain must not depend on EF Core. {Offenders(result)}");
    }

    [Fact]
    public void MasterData_Domain_does_not_depend_on_any_Infrastructure()
    {
        string[] forbidden =
        [
            "Products.Infrastructure", "Carts.Infrastructure", "Checkouts.Infrastructure",
            "Orders.Infrastructure", "Payments.Infrastructure", "Admins.Infrastructure",
            "Merchants.Infrastructure", "Iam.Infrastructure", "MasterData.Infrastructure",
            "BuildingBlocks.Infrastructure",
        ];
        AssertAllResolveToARealAssembly(forbidden);

        var result = Types.InAssembly(MasterDataDomain).Should().NotHaveDependencyOnAny(forbidden).GetResult();

        Assert.True(result.IsSuccessful, $"MasterData.Domain must not depend on any Infrastructure. {Offenders(result)}");
    }

    [Fact]
    public void MasterData_Domain_and_Application_do_not_depend_on_Admins()
    {
        // REQ-4.2: MasterData does not know its caller — neither layer may reference Admins at all.
        string[] forbidden = ["Admins.Domain", "Admins.Application", "Admins.Infrastructure"];
        AssertAllResolveToARealAssembly(forbidden);

        foreach (var assembly in new[] { MasterDataDomain, MasterDataApplication })
        {
            var result = Types.InAssembly(assembly).Should().NotHaveDependencyOnAny(forbidden).GetResult();
            Assert.True(result.IsSuccessful,
                $"{assembly.GetName().Name} must not depend on Admins. {Offenders(result)}");
        }
    }

    [Fact]
    public void Admins_Domain_and_Application_reference_only_MasterData_Domain_not_Application_or_Infrastructure()
    {
        // REQ-4.1: MasterData.Domain is the published surface (like SharedKernel); Admins.Application already
        // references it directly (the profile-FK lookup port). Application/Infrastructure stay internal.
        string[] forbidden = ["MasterData.Application", "MasterData.Infrastructure"];
        AssertAllResolveToARealAssembly(forbidden);

        foreach (var assembly in new[] { AdminsDomain, AdminsApplication })
        {
            var result = Types.InAssembly(assembly).Should().NotHaveDependencyOnAny(forbidden).GetResult();
            Assert.True(result.IsSuccessful,
                $"{assembly.GetName().Name} may reference only MasterData.Domain, not MasterData.Application/Infrastructure. {Offenders(result)}");
        }
    }

    private static string Offenders(TestResult result) =>
        result.IsSuccessful ? "(none)" : "Offenders: " + string.Join(", ", result.FailingTypeNames ?? []);

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
