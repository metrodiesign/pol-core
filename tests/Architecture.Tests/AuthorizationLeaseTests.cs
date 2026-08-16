using Admins.Domain.Users;
using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Admins;

namespace Architecture.Tests;

/// <summary>
/// Proves the rls-to-query-filter REQ-4.9 authorization lease: a caller's stale snapshot is denied before
/// the transaction starts, a concurrent revoke racing in during the transaction is denied at commit (the
/// EF concurrency-token WHERE clause emits 0 affected rows), and a matching snapshot with no interference
/// succeeds — distinct from a BUSINESS write's row count (proven separately by
/// <see cref="WriteFloorTests"/>), which is never treated as an authorization signal.
/// </summary>
public sealed class AuthorizationLeaseTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public AuthorizationLeaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = NewContext();
        setup.Database.EnsureCreated();
    }

    private ControlPlaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite(_connection).Options,
            FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    [Fact]
    public async Task Matching_snapshot_with_no_interference_succeeds()
    {
        var callerId = await SeedAdminAsync();

        using var context = NewContext();
        await AuthorizationLease.VerifyAsync(context, callerId, expectedVersion: 0, NoOpSecurityTelemetry.Instance, CancellationToken.None);
        await context.SaveChangesAsync(); // must not throw
    }

    [Fact]
    public async Task Stale_snapshot_from_the_start_is_denied_before_any_write()
    {
        var callerId = await SeedAdminAsync();

        using (var bumper = NewContext())
        {
            var caller = await bumper.Users.SingleAsync(u => u.Id == callerId);
            caller.Suspend(Guid.NewGuid()); // bumps AuthorizationVersion 0 -> 1
            await bumper.SaveChangesAsync();
        }

        using var context = NewContext();
        var ex = await Assert.ThrowsAsync<WriteGuardException>(
            () => AuthorizationLease.VerifyAsync(context, callerId, expectedVersion: 0, NoOpSecurityTelemetry.Instance, CancellationToken.None));
        Assert.Contains("stale", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_revoke_racing_in_mid_transaction_is_denied_at_commit_not_at_verify()
    {
        var callerId = await SeedAdminAsync();

        using var leaseHolder = NewContext();
        await AuthorizationLease.VerifyAsync(leaseHolder, callerId, expectedVersion: 0, NoOpSecurityTelemetry.Instance, CancellationToken.None);
        // VerifyAsync succeeded (matched at read time) but has NOT saved yet — simulate a concurrent revoke
        // committing in between this lease's verify and its eventual SaveChanges.
        using (var concurrentRevoke = NewContext())
        {
            var caller = await concurrentRevoke.Users.SingleAsync(u => u.Id == callerId);
            caller.Suspend(Guid.NewGuid());
            await concurrentRevoke.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => leaseHolder.SaveChangesAsync());
    }

    [Fact]
    public async Task Unknown_caller_is_denied()
    {
        using var context = NewContext();
        await Assert.ThrowsAsync<WriteGuardException>(
            () => AuthorizationLease.VerifyAsync(context, Guid.NewGuid(), expectedVersion: 0, NoOpSecurityTelemetry.Instance, CancellationToken.None));
    }

    private async Task<Guid> SeedAdminAsync()
    {
        using var writer = NewContext();
        var admin = User.SelfProvision("google", $"g-sub-{Guid.NewGuid():N}", "ops@example.com", DateTime.UtcNow);
        writer.Users.Add(admin);
        await writer.SaveChangesAsync();
        return admin.Id;
    }

    public void Dispose() => _connection.Dispose();
}
