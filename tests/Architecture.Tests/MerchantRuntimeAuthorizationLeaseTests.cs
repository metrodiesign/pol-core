using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Idempotency;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Payments;
using Payments.Application.AdminControlPlane;

namespace Architecture.Tests;

public sealed class MerchantRuntimeAuthorizationLeaseTests : IDisposable
{
    private static readonly Guid Merchant = Guid.Parse("f1000000-0000-4000-8000-000000000001");
    private static readonly Guid Actor = Guid.Parse("f2000000-0000-4000-8000-000000000001");
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public MerchantRuntimeAuthorizationLeaseTests()
    {
        _connection.Open();
        using var setup = NewContext();
        setup.Database.EnsureCreated();
        setup.Database.ExecuteSqlInterpolated(
            $"INSERT INTO AdminAuthorizationLeaseUsers (Id, Status, AuthorizationVersion) VALUES ({Actor}, {1}, {0L})");
    }

    [Fact]
    public async Task Matching_active_snapshot_commits_lease_and_business_write_together()
    {
        await using var db = NewContext();
        var uow = new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance);
        await uow.ExecuteInTransactionAsync(async ct =>
        {
            await new MerchantRuntimeAuthorizationLease(db, NoOpSecurityTelemetry.Instance)
                .VerifyAsync(Access(0), ct);
            db.AdminOperationRecords.Add(AdminOperationRecord.Create(
                Merchant, Actor, "lease-test", "matching", new string('a', 64), DateTime.UtcNow));
            await uow.SaveChangesAsync(ct);
            return true;
        }, default);

        await using var verify = NewContext();
        Assert.Single(await verify.AdminOperationRecords.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Stale_or_inactive_snapshot_is_denied_before_business_write()
    {
        foreach (var access in new[] { Access(1), Access(0) })
        {
            await using var db = NewContext();
            if (access.AuthorizationVersion == 0)
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE AdminAuthorizationLeaseUsers SET Status = {2} WHERE Id = {Actor}");
            await using var transaction = await db.Database.BeginTransactionAsync();

            var error = await Assert.ThrowsAsync<AccessDeniedException>(() =>
                new MerchantRuntimeAuthorizationLease(db, NoOpSecurityTelemetry.Instance).VerifyAsync(access, default));

            Assert.Equal("authorization_stale", error.Code);
            await transaction.RollbackAsync();
            if (access.AuthorizationVersion == 0)
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE AdminAuthorizationLeaseUsers SET Status = {1} WHERE Id = {Actor}");
        }
    }

    [Fact]
    public async Task Authorization_change_before_commit_rolls_back_business_write()
    {
        await using var db = NewContext();
        var uow = new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance);

        var error = await Assert.ThrowsAsync<AccessDeniedException>(() =>
            uow.ExecuteInTransactionAsync(async ct =>
            {
                await new MerchantRuntimeAuthorizationLease(db, NoOpSecurityTelemetry.Instance)
                    .VerifyAsync(Access(0), ct);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE AdminAuthorizationLeaseUsers SET AuthorizationVersion = {1L} WHERE Id = {Actor}", ct);
                db.AdminOperationRecords.Add(AdminOperationRecord.Create(
                    Merchant, Actor, "lease-test", "raced", new string('b', 64), DateTime.UtcNow));
                await uow.SaveChangesAsync(ct);
                return true;
            }, default));

        Assert.Equal("authorization_stale", error.Code);
        await using var verify = NewContext();
        Assert.Empty(await verify.AdminOperationRecords.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(0L, await verify.Set<AdminAuthorizationLeaseRow>()
            .Where(x => x.Id == Actor).Select(x => x.AuthorizationVersion).SingleAsync());
    }

    [Fact]
    public void Shadow_row_mapping_and_source_references_are_narrow()
    {
        using var db = NewContext();
        var entity = db.Model.FindEntityType(typeof(AdminAuthorizationLeaseRow));
        Assert.NotNull(entity);
        Assert.True(typeof(AdminAuthorizationLeaseRow).IsNotPublic);
        Assert.True(typeof(AdminAuthorizationLeaseRow).IsSealed);
        Assert.Equal("AdminAuthorizationLeaseUsers", entity.GetTableName());
        Assert.Equal("admin", entity.GetSchema());
        Assert.Equal(["AuthorizationVersion", "Id", "Status"],
            entity.GetProperties().Select(x => x.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.True(entity.FindProperty(nameof(AdminAuthorizationLeaseRow.AuthorizationVersion))!.IsConcurrencyToken);

        var root = FindRepoRoot();
        var references = Directory.EnumerateFiles(
                Path.Combine(root, "src"), "*.cs", System.IO.SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => File.ReadAllText(path).Contains(nameof(AdminAuthorizationLeaseRow), StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal([
            "src/Hosts/Api/Persistence/WriteAuthorizers.cs",
            "src/Persistence/Persistence.MerchantRuntime/MerchantRuntimeDbContext.cs",
            "src/Persistence/Persistence.MerchantRuntime/MerchantRuntimeUnitOfWork.cs",
            "src/Persistence/Persistence.MerchantRuntime/Payments/MerchantRuntimeAuthorizationLease.cs",
        ], references);
    }

    [Fact]
    public void Every_admin_payment_mutation_transaction_invokes_the_lease()
    {
        var root = FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(root,
            "src/Persistence/Persistence.MerchantRuntime/Payments/AdminPaymentsControlStore.cs"));

        Assert.Equal(14, System.Text.RegularExpressions.Regex.Matches(
            source, @"\.ExecuteInTransactionAsync\(").Count);
        Assert.Equal(14, System.Text.RegularExpressions.Regex.Matches(
            source, @"authorizationLease\.VerifyAsync\(").Count);
    }

    private MerchantRuntimeDbContext NewContext() => new(
        new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
        FakeActorContext.For(Merchant), FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    private static AdminPaymentsAccess Access(long version) =>
        new(Actor, version, true, new HashSet<Guid>());

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pol-core.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    public void Dispose() => _connection.Dispose();
}
