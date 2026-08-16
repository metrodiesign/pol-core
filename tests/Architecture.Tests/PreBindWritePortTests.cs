using Admins.Domain.Users;
using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Admins;
using SharedKernel;

namespace Architecture.Tests;

/// <summary>
/// Proves the rls-to-query-filter task 5 remaining pre-bind WRITE ports (design.md "Pre-owner-bind READS vs
/// WRITES"): <see cref="ISelfProvisionSuperWriter"/> / <see cref="IBindInvitedAdminIdentity"/> (ControlPlane,
/// no query filter — no bypass primitive needed). The merchant-user registration/correction/approve/reject
/// DML writers this file once covered were deleted by bugfix-merchant-prebind-wiring: those flows now run
/// through the REAL handlers over <c>IAccountStore</c> — see <see cref="MerchantIdentityLifecycleTests"/>.
/// </summary>
public sealed class PreBindWritePortTests
{
    private static ControlPlaneDbContext NewControlPlaneContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite(connection).Options,
            FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    [Fact]
    public async Task SelfProvision_creates_a_new_super_admin_for_an_unknown_subject()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (var setup = NewControlPlaneContext(connection))
            await setup.Database.EnsureCreatedAsync();

        using var db = NewControlPlaneContext(connection);
        var outcome = await new AdminSelfProvisionWriter(db).ProvisionAsync(new ProviderIdentity("google", "g-sub-1"), "boot@example.com", DateTime.UtcNow, CancellationToken.None);

        Assert.False(outcome.AlreadyExisted);
        Assert.Equal("boot@example.com", outcome.Email);

        using var reader = NewControlPlaneContext(connection);
        var stored = await reader.Users.SingleAsync(u => u.Id == outcome.AdminId);
        Assert.Equal(Tier.Super, stored.Tier);
        Assert.Equal("g-sub-1", stored.Subject);
    }

    [Fact]
    public async Task SelfProvision_is_idempotent_for_an_already_provisioned_subject()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (var setup = NewControlPlaneContext(connection))
            await setup.Database.EnsureCreatedAsync();

        Guid firstId;
        using (var first = NewControlPlaneContext(connection))
            firstId = (await new AdminSelfProvisionWriter(first).ProvisionAsync(new ProviderIdentity("google", "g-sub-2"), "a@example.com", DateTime.UtcNow, CancellationToken.None)).AdminId;

        using var replay = NewControlPlaneContext(connection);
        var outcome = await new AdminSelfProvisionWriter(replay).ProvisionAsync(new ProviderIdentity("google", "g-sub-2"), "a@example.com", DateTime.UtcNow, CancellationToken.None);

        Assert.True(outcome.AlreadyExisted);
        Assert.Equal(firstId, outcome.AdminId);

        using var reader = NewControlPlaneContext(connection);
        Assert.Equal(1, await reader.Users.CountAsync(u => u.Subject == "g-sub-2"));
    }

    [Fact]
    public async Task BindInvited_binds_the_subject_to_an_invited_scoped_account()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (var setup = NewControlPlaneContext(connection))
            await setup.Database.EnsureCreatedAsync();

        using (var writer = NewControlPlaneContext(connection))
        {
            writer.Users.Add(User.CreateScoped("invited@example.com", DateTime.UtcNow));
            await writer.SaveChangesAsync();
        }

        using var db = NewControlPlaneContext(connection);
        var outcome = await new AdminBindInvitedIdentityWriter(db).BindAsync("google", "g-sub-3", "invited@example.com", CancellationToken.None);

        Assert.Equal(BindInvitedOutcome.Bound, outcome);
        using var reader = NewControlPlaneContext(connection);
        var stored = await reader.Users.SingleAsync(u => u.Email == "invited@example.com");
        Assert.Equal("g-sub-3", stored.Subject);
    }

    [Fact]
    public async Task BindInvited_returns_NoInviteFound_for_an_unknown_email()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (var setup = NewControlPlaneContext(connection))
            await setup.Database.EnsureCreatedAsync();

        using var db = NewControlPlaneContext(connection);
        var outcome = await new AdminBindInvitedIdentityWriter(db).BindAsync("google", "g-sub-4", "nobody@example.com", CancellationToken.None);

        Assert.Equal(BindInvitedOutcome.NoInviteFound, outcome);
    }

    [Fact]
    public async Task BindInvited_returns_AlreadyBound_when_replayed()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (var setup = NewControlPlaneContext(connection))
            await setup.Database.EnsureCreatedAsync();

        using (var writer = NewControlPlaneContext(connection))
        {
            writer.Users.Add(User.CreateScoped("invited2@example.com", DateTime.UtcNow));
            await writer.SaveChangesAsync();
        }
        using (var first = NewControlPlaneContext(connection))
            await new AdminBindInvitedIdentityWriter(first).BindAsync("google", "g-sub-5", "invited2@example.com", CancellationToken.None);

        using var replay = NewControlPlaneContext(connection);
        var outcome = await new AdminBindInvitedIdentityWriter(replay).BindAsync("google", "g-sub-5-again", "invited2@example.com", CancellationToken.None);

        Assert.Equal(BindInvitedOutcome.AlreadyBound, outcome);
    }

}
