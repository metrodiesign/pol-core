using Admins.Domain.Users;
using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Admins;
using Persistence.ControlPlane.Governance;

namespace Architecture.Tests;

public sealed class WorkforceTenantBindingStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public WorkforceTenantBindingStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = NewContext();
        setup.Database.EnsureCreated();
    }

    [Fact]
    public void Runtime_model_has_nullable_contact_and_tenant_tuple_without_workforce_email_key()
    {
        using var db = NewContext();
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(User))!;

        Assert.True(entity.FindProperty(nameof(User.Email))!.IsNullable);
        Assert.True(entity.FindProperty(nameof(User.TenantId))!.IsNullable);
        Assert.Null(entity.FindProperty("WorkforceEmailKey"));
        var identityIndex = Assert.Single(entity.GetIndexes(), index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(User.Provider), nameof(User.TenantId), nameof(User.Subject)]));
        Assert.True(identityIndex.IsUnique);
        Assert.Equal("[Subject] IS NOT NULL", identityIndex.GetFilter());
        Assert.Contains(entity.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Single().Name == nameof(User.TenantId)
            && foreignKey.PrincipalKey.Properties.Single().Name == nameof(WorkforceTenantBinding.TenantId));
        Assert.Contains(entity.GetCheckConstraints(), constraint =>
            constraint.Name == "CK_Users_TenantId_MicrosoftProvider");
    }

    [Fact]
    public async Task Ensure_initializes_once_accepts_same_tenant_and_rejects_drift_without_value_echo()
    {
        var tenantId = Guid.NewGuid();
        using var db = NewContext();
        var store = Store(db);

        await store.EnsureAsync(tenantId, CancellationToken.None);
        await store.EnsureAsync(tenantId, CancellationToken.None);

        Assert.Equal(1, await db.WorkforceTenantBindings.CountAsync());
        Assert.Equal(tenantId, (await db.WorkforceTenantBindings.SingleAsync()).TenantId);

        var otherTenant = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.EnsureAsync(otherTenant, CancellationToken.None));
        Assert.DoesNotContain(tenantId.ToString("D"), error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(otherTenant.ToString("D"), error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Runtime_guard_rejects_update_and_delete_to_persisted_binding()
    {
        using (var seed = NewContext())
            await Store(seed).EnsureAsync(Guid.NewGuid(), CancellationToken.None);

        using (var update = NewContext())
        {
            var binding = await update.WorkforceTenantBindings.SingleAsync();
            Assert.Throws<InvalidOperationException>(() =>
                update.Entry(binding).Property(x => x.TenantId).CurrentValue = Guid.NewGuid());
        }

        using (var delete = NewContext())
        {
            var binding = await delete.WorkforceTenantBindings.SingleAsync();
            delete.WorkforceTenantBindings.Remove(binding);

            await Assert.ThrowsAsync<WriteGuardException>(() => delete.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Ensure_accepts_final_rows_and_GetRequired_returns_the_singleton()
    {
        var tenantId = Guid.NewGuid();
        using var db = NewContext();
        var store = Store(db);
        await store.EnsureAsync(tenantId, CancellationToken.None);
        db.Users.Add(User.JitProvisionMicrosoft(tenantId, Guid.NewGuid(), null, DateTime.UtcNow));
        await db.SaveChangesAsync();

        await store.EnsureAsync(tenantId, CancellationToken.None);

        Assert.Equal(tenantId, await store.GetRequiredTenantIdAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Microsoft_repository_lookup_uses_only_the_exact_tuple_and_generic_lookup_cannot_see_it()
    {
        var tenantId = Guid.NewGuid();
        var exactObjectId = Guid.NewGuid();
        using var db = NewContext();
        await Store(db).EnsureAsync(tenantId, CancellationToken.None);
        var exact = User.JitProvisionMicrosoft(tenantId, exactObjectId, "shared@example.com", DateTime.UtcNow);
        var sameEmail = User.JitProvisionMicrosoft(tenantId, Guid.NewGuid(), "shared@example.com", DateTime.UtcNow);
        db.Users.AddRange(exact, sameEmail);
        await db.SaveChangesAsync();
        var repository = new UserRepository(
            db,
            NullLogger<UserRepository>.Instance,
            NoOpSecurityTelemetry.Instance,
            new GovernanceSqlLockManager(db));

        Assert.Equal(exact.Id, (await repository.GetByMicrosoftIdentityAsync(
            tenantId, exactObjectId, CancellationToken.None))?.Id);
        Assert.Null(await repository.GetByMicrosoftIdentityAsync(
            tenantId, Guid.NewGuid(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.GetByIdentityAsync(
            new SharedKernel.ProviderIdentity(User.MicrosoftProvider, exactObjectId.ToString("D")),
            CancellationToken.None));
    }

    [Fact]
    public async Task Ensure_rejects_migration_only_unbound_rows_without_value_echo()
    {
        using var db = NewContext();
        db.Users.Add(User.CreateScoped("private@example.com", DateTime.UtcNow));
        await db.SaveChangesAsync();
        var tenantId = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Store(db).EnsureAsync(tenantId, CancellationToken.None));

        Assert.DoesNotContain("private@example.com", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(tenantId.ToString("D"), error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_store_acquires_the_exact_transaction_owned_lock_before_reading_the_singleton()
    {
        var root = FindRepoRoot();
        var store = File.ReadAllText(Path.Combine(root,
            "src/Persistence/Persistence.ControlPlane/Admins/WorkforceTenantBindingStore.cs"));
        var lockManager = File.ReadAllText(Path.Combine(root,
            "src/Persistence/Persistence.ControlPlane/Governance/GovernanceSqlLockManager.cs"));

        var acquire = store.IndexOf("await locks.AcquireAsync(LockResource", StringComparison.Ordinal);
        var read = store.IndexOf("db.WorkforceTenantBindings.SingleOrDefaultAsync", StringComparison.Ordinal);
        Assert.Contains("admin-workforce-tenant-binding", store, StringComparison.Ordinal);
        Assert.True(acquire >= 0 && acquire < read, "Tenant applock must be acquired before the singleton read.");
        Assert.Contains("@LockMode = N'Exclusive'", lockManager, StringComparison.Ordinal);
        Assert.Contains("@LockOwner = N'Transaction'", lockManager, StringComparison.Ordinal);
    }

    private ControlPlaneDbContext NewContext() => new(
        new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite(_connection).Options,
        FakeWriteAuthorizer.AllowAll,
        NoOpSecurityTelemetry.Instance);

    private static WorkforceTenantBindingStore Store(ControlPlaneDbContext db) => new(
        db,
        new ControlPlaneUnitOfWork(db, NoOpSecurityTelemetry.Instance),
        new GovernanceSqlLockManager(db));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repo root from the test binary.");
    }

    public void Dispose() => _connection.Dispose();
}
