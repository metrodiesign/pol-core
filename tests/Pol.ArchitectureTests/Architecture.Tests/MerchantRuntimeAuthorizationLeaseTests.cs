using BuildingBlocks.Application;
using Governance.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Payments.Application.AdminControlPlane;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Admins;
using Persistence.ControlPlane.Payments;

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
        setup.Database.ExecuteSqlInterpolated($"""
            INSERT INTO Users (Id, Provider, Tier, Status, CreatedAt, AuthorizationVersion, Version)
            VALUES ({Actor}, 'microsoft', 2, 1, {DateTime.UtcNow}, 0, 1)
            """);
    }

    [Fact]
    public async Task Matching_active_snapshot_commits_lease_and_business_write_together()
    {
        await using var db = NewContext();
        var uow = new ControlPlaneUnitOfWork(db, NoOpSecurityTelemetry.Instance);
        await uow.ExecuteInTransactionAsync(async ct =>
        {
            await new MerchantRuntimeAuthorizationLease(db, NoOpSecurityTelemetry.Instance)
                .VerifyAsync(Access(0), ct);
            db.OperationRecords.Add(OperationRecord.Create(
                Actor, "lease-test", "matching", new string('a', 64),
                GovernanceScopeKind.Merchant, Merchant, DateTime.UtcNow, DateTime.UtcNow.AddHours(24)));
            await uow.SaveChangesAsync(ct);
            return true;
        }, default);

        await using var verify = NewContext();
        Assert.Single(await verify.OperationRecords.ToListAsync());
    }

    [Fact]
    public async Task Conditional_predicate_denies_stale_or_inactive_snapshot_before_business_write()
    {
        await using (var stale = NewContext())
        {
            await stale.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE Users SET AuthorizationVersion = {1L} WHERE Id = {Actor}");
        }

        await using var db = NewContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var error = await Assert.ThrowsAsync<AccessDeniedException>(() =>
            new MerchantRuntimeAuthorizationLease(db, NoOpSecurityTelemetry.Instance).VerifyAsync(Access(0), default));
        Assert.Equal("authorization_stale", error.Code);
        await transaction.RollbackAsync();

        await using var reset = NewContext();
        await reset.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE Users SET AuthorizationVersion = {0L}, Status = {2} WHERE Id = {Actor}");
        await using var inactiveDb = NewContext();
        await using var inactiveTransaction = await inactiveDb.Database.BeginTransactionAsync();
        var inactive = await Assert.ThrowsAsync<AccessDeniedException>(() =>
            new MerchantRuntimeAuthorizationLease(inactiveDb, NoOpSecurityTelemetry.Instance)
                .VerifyAsync(Access(0), default));
        Assert.Equal("authorization_stale", inactive.Code);
        await inactiveTransaction.RollbackAsync();
    }

    [Fact]
    public async Task Business_failure_rolls_back_the_lease_transaction_without_partial_write()
    {
        await using var db = NewContext();
        var uow = new ControlPlaneUnitOfWork(db, NoOpSecurityTelemetry.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            uow.ExecuteInTransactionAsync(async ct =>
            {
                await new MerchantRuntimeAuthorizationLease(db, NoOpSecurityTelemetry.Instance)
                    .VerifyAsync(Access(0), ct);
                db.OperationRecords.Add(OperationRecord.Create(
                    Actor, "lease-test", "rollback", new string('b', 64),
                    GovernanceScopeKind.Merchant, Merchant, DateTime.UtcNow, DateTime.UtcNow.AddHours(24)));
                await uow.SaveChangesAsync(ct);
                throw new InvalidOperationException("test rollback");
#pragma warning disable CS0162
                return true;
#pragma warning restore CS0162
            }, default));

        await using var verify = NewContext();
        Assert.Empty(await verify.OperationRecords.ToListAsync());
        Assert.Equal(0L, await verify.Users.Where(x => x.Id == Actor)
            .Select(x => x.AuthorizationVersion).SingleAsync());
    }

    [Fact]
    public void Lease_is_not_an_EF_projection_or_a_second_mapping_owner()
    {
        using var db = NewContext();
        Assert.DoesNotContain(db.Model.GetEntityTypes(), x =>
            x.ClrType.Name == "AdminAuthorizationLeaseRow");

        var root = FindRepoRoot();
        var references = Directory.EnumerateFiles(
                Path.Combine(root, "src"), "*.cs", System.IO.SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(path => File.ReadAllText(path).Contains("AdminAuthorizationLeaseRow", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(references);
    }

    [Fact]
    public void Every_admin_payment_mutation_transaction_invokes_the_lease()
    {
        var root = FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(root,
            "src/Pol.Infrastructure/Persistence/Persistence.ControlPlane/Payments/AdminPaymentsControlStore.cs"));

        Assert.Equal(16, System.Text.RegularExpressions.Regex.Matches(
            source, @"\.ExecuteInTransactionAsync\(").Count);
        Assert.Equal(16, System.Text.RegularExpressions.Regex.Matches(
            source, @"authorizationLease\.VerifyAsync\(").Count);
    }

    private ControlPlaneDbContext NewContext() => new(
        new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite(_connection).Options,
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
