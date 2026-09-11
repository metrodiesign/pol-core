using Accounts.Domain;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane;

namespace Architecture.Tests;

[Trait("Capability", "IdentityAccess")]
public sealed class IdentityAccessArchitectureTests
{
    [Fact]
    [Trait("Requirement", "REQ-2.11")]
    [Trait("Requirement", "REQ-3.1")]
    [Trait("Requirement", "REQ-3.2")]
    [Trait("Requirement", "REQ-3.5")]
    [Trait("Requirement", "REQ-3.12")]
    public void Account_access_and_oauth_have_one_owner_and_security_indexes()
    {
        using var db = new ControlPlaneDbContext(
            new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite("Data Source=:memory:").Options,
            AllowAll.Instance,
            NoOpSecurityTelemetry.Instance);

        var account = db.Model.FindEntityType(typeof(Account))!;
        var login = db.Model.FindEntityType(typeof(LoginAccount))!;
        var access = db.Model.FindEntityType(typeof(Access.Domain.MerchantAccess))!;
        var replay = db.Model.FindEntityType(typeof(AssertionReplay))!;
        var bff = db.Model.FindEntityType(typeof(BffSessionTicket))!;
        var role = db.Model.FindEntityType(typeof(Access.Domain.AccessRole))!;
        var branch = db.Model.FindEntityType(typeof(Access.Domain.BranchAccess))!;
        var platform = db.Model.FindEntityType(typeof(Access.Domain.PlatformAccess))!;
        var platformRole = db.Model.FindEntityType(typeof(Access.Domain.PlatformAccessRole))!;

        Assert.Equal(SchemaNames.Acct, account.GetSchema());
        Assert.Equal(SchemaNames.Acct, login.GetSchema());
        Assert.Equal(SchemaNames.Access, access.GetSchema());
        Assert.Equal(SchemaNames.OAuth, replay.GetSchema());
        Assert.Equal(SchemaNames.Acct, bff.GetSchema());
        Assert.Single(db.Model.GetEntityTypes(), entity => entity.ClrType == typeof(Account));
        Assert.Contains(login.GetIndexes(), index =>
            index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(LoginAccount.Provider), nameof(LoginAccount.TenantId), nameof(LoginAccount.ExternalUserId)]));
        Assert.Contains(access.GetIndexes(), index =>
            index.IsUnique && index.GetFilter() == "[Status] = 1");
        Assert.Contains(replay.GetIndexes(), index =>
            index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(AssertionReplay.ApplicationId), nameof(AssertionReplay.Jti)]));
        Assert.Contains(role.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(Access.Domain.AccessRole.MerchantAccessId), nameof(Access.Domain.AccessRole.MerchantId)]));
        Assert.Contains(branch.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(Access.Domain.BranchAccess.MerchantAccessId), nameof(Access.Domain.BranchAccess.MerchantId)]));
        Assert.Contains(platform.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(Employee));
        Assert.NotNull(platformRole.FindProperty(nameof(Access.Domain.PlatformAccessRole.RoleScope)));
    }

    [Fact]
    [Trait("Requirement", "REQ-1.7")]
    public void Domain_does_not_reference_infrastructure_or_api()
    {
        var references = typeof(Account).Assembly.GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("Pol.Infrastructure", references);
        Assert.DoesNotContain("Pol.Api", references);
    }

    private sealed class AllowAll : IWriteAuthorizer
    {
        public static readonly AllowAll Instance = new();

        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }
}
