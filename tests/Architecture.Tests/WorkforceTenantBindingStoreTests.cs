using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
            update.Entry(binding).Property(x => x.TenantId).CurrentValue = Guid.NewGuid();

            await Assert.ThrowsAsync<WriteGuardException>(() => update.SaveChangesAsync());
        }

        using (var delete = NewContext())
        {
            var binding = await delete.WorkforceTenantBindings.SingleAsync();
            delete.WorkforceTenantBindings.Remove(binding);

            await Assert.ThrowsAsync<WriteGuardException>(() => delete.SaveChangesAsync());
        }
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
