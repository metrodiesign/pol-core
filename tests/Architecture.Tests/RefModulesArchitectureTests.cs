using System.Reflection;

using NetArchTest.Rules;

namespace Architecture.Tests;

/// <summary>
/// masterdata-split (design.md): the four reference modules (Divisions/Levels/Offices/Positions) are fully
/// independent leaves — no shared base, no references among themselves, no knowledge of any caller
/// (REQ-5.1/5.2/5.3). Admins couples to them at ONE layer only: Admins.Infrastructure may reference each
/// module's Domain (the User -> cfg.* FK configs), never its Application/Infrastructure; Admins.Domain and
/// Admins.Application reference none of the twelve new assemblies at all — the profile-FK port is enum-keyed
/// (REQ-4.3/4.4). One Theory file instead of four clones: the invariants are identical per module, and the
/// fail-closed assembly-name pinning below keeps a stale key from turning any guard vacuous (REQ-5.4).
/// </summary>
public class RefModulesArchitectureTests
{
    private sealed record ModuleLayers(Assembly Domain, Assembly Application, Assembly Infrastructure);

    private static readonly IReadOnlyDictionary<string, ModuleLayers> Modules = new Dictionary<string, ModuleLayers>
    {
        ["Divisions"] = new(
            typeof(global::Divisions.Domain.Division).Assembly,
            typeof(global::Divisions.Application.IDivisionStore).Assembly,
            typeof(global::Divisions.Infrastructure.DivisionsModuleRegistration).Assembly),
        ["Levels"] = new(
            typeof(global::Levels.Domain.Level).Assembly,
            typeof(global::Levels.Application.ILevelStore).Assembly,
            typeof(global::Levels.Infrastructure.LevelsModuleRegistration).Assembly),
        ["Offices"] = new(
            typeof(global::Offices.Domain.Office).Assembly,
            typeof(global::Offices.Application.IOfficeStore).Assembly,
            typeof(global::Offices.Infrastructure.OfficesModuleRegistration).Assembly),
        ["Positions"] = new(
            typeof(global::Positions.Domain.Position).Assembly,
            typeof(global::Positions.Application.IPositionStore).Assembly,
            typeof(global::Positions.Infrastructure.PositionsModuleRegistration).Assembly),
    };

    private static readonly Assembly AdminsDomain = typeof(global::Admins.Domain.Users.User).Assembly;
    private static readonly Assembly AdminsApplication = typeof(global::Admins.Application.Users.IUserRepository).Assembly;
    private static readonly Assembly AdminsInfrastructure = typeof(global::Admins.Infrastructure.AdminModuleRegistration).Assembly;

    public static TheoryData<string> ModuleNames => new() { "Divisions", "Levels", "Offices", "Positions" };

    // REQ-15.2 precedent (ArchitectureBoundaryTests and the per-module files): every forbidden-namespace
    // string below is derived from a key, and NotHaveDependencyOnAny passes vacuously on a name that
    // resolves to NO assembly — a stale or mistyped key would silently kill the guard.
    [Theory]
    [MemberData(nameof(ModuleNames))]
    public void Layer_keys_match_their_real_assembly_names(string module)
    {
        var layers = Modules[module];
        Assert.Equal($"{module}.Domain", layers.Domain.GetName().Name);
        Assert.Equal($"{module}.Application", layers.Application.GetName().Name);
        Assert.Equal($"{module}.Infrastructure", layers.Infrastructure.GetName().Name);
    }

    [Theory]
    [MemberData(nameof(ModuleNames))]
    public void Domain_does_not_depend_on_EntityFrameworkCore(string module)
    {
        var result = Types.InAssembly(Modules[module].Domain)
            .Should().NotHaveDependencyOn("Microsoft.EntityFrameworkCore").GetResult();

        Assert.True(result.IsSuccessful, $"{module}.Domain must not depend on EF Core. {Offenders(result)}");
    }

    [Theory]
    [MemberData(nameof(ModuleNames))]
    public void Domain_does_not_depend_on_any_Infrastructure(string module)
    {
        string[] forbidden =
        [
            "Products.Infrastructure", "Carts.Infrastructure", "Checkouts.Infrastructure",
            "Orders.Infrastructure", "Payments.Infrastructure", "Admins.Infrastructure",
            "Merchants.Infrastructure", "Iam.Infrastructure",
            "Divisions.Infrastructure", "Levels.Infrastructure", "Offices.Infrastructure",
            "Positions.Infrastructure", "BuildingBlocks.Infrastructure",
        ];
        AssertAllResolveToARealAssembly(forbidden);

        var result = Types.InAssembly(Modules[module].Domain).Should().NotHaveDependencyOnAny(forbidden).GetResult();

        Assert.True(result.IsSuccessful, $"{module}.Domain must not depend on any Infrastructure. {Offenders(result)}");
    }

    [Theory]
    [MemberData(nameof(ModuleNames))]
    public void Module_does_not_depend_on_its_siblings_or_Admins(string module)
    {
        // REQ-5.2/5.3: every layer of this module must be blind to the other three reference modules AND to
        // Admins — the four lists are independent leaves that do not know their caller.
        string[] forbidden =
        [
            .. Modules.Where(m => m.Key != module).SelectMany(m => new[]
            {
                $"{m.Key}.Domain", $"{m.Key}.Application", $"{m.Key}.Infrastructure",
            }),
            "Admins.Domain", "Admins.Application", "Admins.Infrastructure",
        ];
        AssertAllResolveToARealAssembly(forbidden);

        var layers = Modules[module];
        foreach (var assembly in new[] { layers.Domain, layers.Application, layers.Infrastructure })
        {
            var result = Types.InAssembly(assembly).Should().NotHaveDependencyOnAny(forbidden).GetResult();
            Assert.True(result.IsSuccessful,
                $"{assembly.GetName().Name} must not depend on sibling reference modules or Admins. {Offenders(result)}");
        }
    }

    [Fact]
    public void Admins_Domain_and_Application_reference_no_reference_module()
    {
        // REQ-4.3: the enum-keyed profile port keeps both layers free of every reference-module assembly.
        string[] forbidden =
        [
            .. Modules.Keys.SelectMany(m => new[] { $"{m}.Domain", $"{m}.Application", $"{m}.Infrastructure" }),
        ];
        AssertAllResolveToARealAssembly(forbidden);

        foreach (var assembly in new[] { AdminsDomain, AdminsApplication })
        {
            var result = Types.InAssembly(assembly).Should().NotHaveDependencyOnAny(forbidden).GetResult();
            Assert.True(result.IsSuccessful,
                $"{assembly.GetName().Name} must not reference any reference module. {Offenders(result)}");
        }
    }

    [Fact]
    public void Admins_Infrastructure_references_only_the_Domains()
    {
        // REQ-4.4: the User -> cfg.* FK configs are the one sanctioned coupling — Domain only.
        string[] forbidden =
        [
            .. Modules.Keys.SelectMany(m => new[] { $"{m}.Application", $"{m}.Infrastructure" }),
        ];
        AssertAllResolveToARealAssembly(forbidden);

        var result = Types.InAssembly(AdminsInfrastructure).Should().NotHaveDependencyOnAny(forbidden).GetResult();
        Assert.True(result.IsSuccessful,
            $"Admins.Infrastructure may reference only the reference modules' Domain layers. {Offenders(result)}");
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
