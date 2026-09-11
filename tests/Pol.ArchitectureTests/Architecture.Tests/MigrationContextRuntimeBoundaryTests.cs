using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using BuildingBlocks.Application;
using Persistence.ControlPlane;
using Persistence.MerchantRuntime;
using Persistence.MerchantUsers;

namespace Architecture.Tests;

public sealed class MigrationContextRuntimeBoundaryTests
{
    [Fact]
    [Trait("Capability", "MigrationReadiness")]
    public void Api_host_does_not_register_or_construct_the_migration_context()
    {
        var program = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/Pol.Api/Api/Program.cs"));

        Assert.DoesNotContain("AddDbContext<PolDbContext>", program, StringComparison.Ordinal);
        Assert.DoesNotContain("new PolDbContext", program, StringComparison.Ordinal);
        Assert.DoesNotContain("MigrateAsync", program, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Capability", "MigrationReadiness")]
    public void Migration_context_construction_is_confined_to_the_design_time_factory()
    {
        var root = FindRepoRoot();
        var apiFiles = Directory.EnumerateFiles(Path.Combine(root, "src/Pol.Api"), "*.cs", System.IO.SearchOption.AllDirectories);
        var offenders = apiFiles
            .Where(path => !path.EndsWith("DesignTimeDbContextFactories.cs", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("new PolDbContext", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait("Capability", "MigrationReadiness")]
    public void Runtime_persistence_registers_exactly_control_plane_and_commerce_contexts()
    {
        var services = new ServiceCollection();
        services.AddControlPlanePersistence("Server=unused;Database=unused", _ => FakeWriteAuthorizer.AllowAll);
        services.AddMerchantUserPersistence("Server=unused;Database=unused", _ => FakeWriteAuthorizer.AllowAll);
        services.AddMerchantRuntimePersistence("Server=unused;Database=unused", _ => FakeWriteAuthorizer.AllowAll);

        var registered = services
            .Where(descriptor => descriptor.ServiceType == typeof(ControlPlaneDbContext)
                || descriptor.ServiceType == typeof(CommerceDbContext))
            .Select(descriptor => descriptor.ServiceType)
            .ToArray();

        Assert.Equal(
            [typeof(ControlPlaneDbContext), typeof(CommerceDbContext)],
            registered);
    }

    [Fact]
    [Trait("Capability", "MigrationReadiness")]
    public void Sqlite_model_aliases_schema_collisions_without_changing_sql_server_names()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var context = new ControlPlaneDbContext(
            new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite(connection).Options,
            FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

        Assert.Equal("MerchantUsers", context.Model.FindEntityType(typeof(Merchants.Domain.Users.User))!.GetTableName());
        Assert.Equal("MerchantUserSessions", context.Model.FindEntityType(typeof(Merchants.Domain.Users.Session))!.GetTableName());
        Assert.Equal("MerchantAuthAudits", context.Model.FindEntityType(typeof(Merchants.Domain.Users.AuthAudit))!.GetTableName());
        Assert.Equal("MerchantRoleAssignments", context.Model.FindEntityType(typeof(Merchants.Domain.Users.Roles.RoleAssignment))!.GetTableName());
        Assert.Equal("AdminMerchantAccess", context.Model.FindEntityType(typeof(Admins.Domain.Users.MerchantAccess))!.GetTableName());
        Assert.Equal("AccountMerchantAccess", context.Model.FindEntityType(typeof(Access.Domain.MerchantAccess))!.GetTableName());
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pol-core.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
