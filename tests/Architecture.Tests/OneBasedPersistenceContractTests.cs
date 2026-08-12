using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Application;
using Iam.Domain.Roles;
using Merchants.Domain.Users;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Persistence.ControlPlane;
using Persistence.MerchantUsers;

namespace Architecture.Tests;

public sealed class OneBasedPersistenceContractTests : IDisposable
{
    private readonly SqliteConnection _ownerConnection = OpenSqlite();
    private readonly SqliteConnection _controlPlaneConnection = OpenSqlite();
    private readonly SqliteConnection _merchantUserConnection = OpenSqlite();
    private readonly PolDbContext _owner;
    private readonly ControlPlaneDbContext _controlPlane;
    private readonly MerchantUserDbContext _merchantUser;

    public OneBasedPersistenceContractTests()
    {
        _owner = new PolDbContext(
            new DbContextOptionsBuilder<PolDbContext>().UseSqlite(_ownerConnection)
                .EnableServiceProviderCaching(false).Options,
            new ModuleAssemblies([
                typeof(Merchants.Infrastructure.MerchantsModuleRegistration).Assembly,
                typeof(Admins.Infrastructure.AdminModuleRegistration).Assembly,
                typeof(Iam.Infrastructure.IamModuleRegistration).Assembly,
                typeof(Offices.Infrastructure.OfficesModuleRegistration).Assembly,
                typeof(Divisions.Infrastructure.DivisionsModuleRegistration).Assembly,
                typeof(Positions.Infrastructure.PositionsModuleRegistration).Assembly,
                typeof(Levels.Infrastructure.LevelsModuleRegistration).Assembly,
            ]));
        _controlPlane = new ControlPlaneDbContext(
            new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite(_controlPlaneConnection)
                .EnableServiceProviderCaching(false).Options,
            FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
        _merchantUser = new MerchantUserDbContext(
            new DbContextOptionsBuilder<MerchantUserDbContext>().UseSqlite(_merchantUserConnection)
                .EnableServiceProviderCaching(false).Options,
            FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
    }

    [Fact]
    public void IdentityType_is_required_in_migration_owner_and_runtime_merchant_context()
    {
        foreach (var db in new DbContext[] { _owner, _merchantUser })
        {
            Assert.False(Property(db, typeof(User), nameof(User.IdentityType)).IsNullable);
            Assert.False(Property(db, typeof(RegistrationAttempt), nameof(RegistrationAttempt.IdentityType)).IsNullable);
        }
    }

    [Fact]
    public void Role_scope_check_uses_platform_one_and_merchant_two_in_both_contexts()
    {
        const string expected = "([Scope] = 1 AND [MerchantId] IS NULL) OR [Scope] = 2";
        foreach (var db in new DbContext[] { _owner, _controlPlane })
        {
            var role = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Role));
            Assert.NotNull(role);
            var check = role!.GetCheckConstraints().Single(c => c.Name == "CK_Roles_ScopeMerchant");
            Assert.Equal(expected, check.Sql);
        }
    }

    [Fact]
    public void Admin_mutable_resources_use_separate_one_based_concurrency_versions_in_both_contexts()
    {
        Type[] resources =
        [
            typeof(Admins.Domain.Users.User),
            typeof(Role),
            typeof(Offices.Domain.Office),
            typeof(Divisions.Domain.Division),
            typeof(Positions.Domain.Position),
            typeof(Levels.Domain.Level),
        ];

        foreach (var db in new DbContext[] { _owner, _controlPlane })
            foreach (var resource in resources)
            {
                var version = Property(db, resource, "Version");
                Assert.True(version.IsConcurrencyToken, $"{resource.Name}.Version must be a concurrency token.");
                Assert.Equal(1L, version.GetDefaultValue());
            }
    }

    private static IProperty Property(DbContext db, Type entityType, string propertyName) =>
        db.Model.FindEntityType(entityType)?.FindProperty(propertyName)
        ?? throw new InvalidOperationException($"{entityType.Name}.{propertyName} is not mapped.");

    private static SqliteConnection OpenSqlite()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        _merchantUser.Dispose();
        _controlPlane.Dispose();
        _owner.Dispose();
        _merchantUserConnection.Dispose();
        _controlPlaneConnection.Dispose();
        _ownerConnection.Dispose();
    }
}
