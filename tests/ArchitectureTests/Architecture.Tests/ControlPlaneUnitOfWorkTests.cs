using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Admins;

namespace Architecture.Tests;

// Codex P1 (PR #124): a FAILED ExecuteInTransactionAsync attempt must not leak its staged entities into the
// request-scoped context. The raced bootstrap (SelfProvisionSuperHandler) CATCHES the unique-key
// ConflictException and continues on the same context — without clearing, the login's later session save
// retried the failed attempt's duplicate inserts and turned the losing callback into session-write-failed.
public sealed class ControlPlaneUnitOfWorkTests
{
    [Fact]
    public async Task A_failed_transaction_attempt_leaves_the_change_tracker_clean_for_later_saves()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var db = new ControlPlaneDbContext(
            new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite(connection).Options,
            FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
        await db.Database.EnsureCreatedAsync();
        var uow = new ControlPlaneUnitOfWork(db, NoOpSecurityTelemetry.Instance);

        // The operation stages an entity, then fails at commit time (what a unique-violation does).
        await Assert.ThrowsAsync<ConflictException>(() => uow.ExecuteInTransactionAsync<int>(async ct =>
        {
            db.Users.Add(Admins.Domain.Users.User.SelfProvision("google", "race-loser-sub", "loser@org.com", DateTime.UtcNow));
            await db.SaveChangesAsync(ct);
            throw new ConflictException("simulated unique-key violation surfaced by SaveChangesAsync");
        }, default));

        Assert.Empty(db.ChangeTracker.Entries()); // nothing staged survives the failure

        // ...and a later save on the SAME context (the login's session insert) is not poisoned by the failed attempt.
        var saved = await uow.SaveChangesAsync(default);
        Assert.Equal(0, saved);
        Assert.Empty(await db.Users.Where(u => u.Subject == "race-loser-sub").ToListAsync());
    }
}
