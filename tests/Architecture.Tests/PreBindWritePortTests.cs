using Admins.Domain.Users;
using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Admins;
using Persistence.MerchantUsers;
using Persistence.MerchantUsers.Users;
using MerchantUserAccount = Merchants.Domain.Users.User;

namespace Architecture.Tests;

/// <summary>
/// Proves the rls-to-query-filter task 5 remaining pre-bind WRITE ports (design.md "Pre-owner-bind READS vs
/// WRITES"): <see cref="ISelfProvisionSuperWriter"/> / <see cref="IBindInvitedAdminIdentity"/> (ControlPlane,
/// no query filter — no bypass primitive needed) and <see cref="IRegistrationWriter"/> (MerchantUser, pre-bind
/// Subject lookup over a NULL-<c>MerchantId</c> row — needs the same <c>IgnoreQueryFilters()</c> escape hatch
/// as the approve/reject ports).
/// </summary>
public sealed class PreBindWritePortTests
{
    private static ControlPlaneDbContext NewControlPlaneContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite(connection).Options,
            FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    private static MerchantUserDbContext NewMerchantUserContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<MerchantUserDbContext>().UseSqlite(connection).Options,
            FakeActorContext.Unbound, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    [Fact]
    public async Task SelfProvision_creates_a_new_super_admin_for_an_unknown_subject()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (var setup = NewControlPlaneContext(connection))
            await setup.Database.EnsureCreatedAsync();

        using var db = NewControlPlaneContext(connection);
        var outcome = await new AdminSelfProvisionWriter(db).ProvisionAsync("g-sub-1", "boot@example.com", DateTime.UtcNow, CancellationToken.None);

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
            firstId = (await new AdminSelfProvisionWriter(first).ProvisionAsync("g-sub-2", "a@example.com", DateTime.UtcNow, CancellationToken.None)).AdminId;

        using var replay = NewControlPlaneContext(connection);
        var outcome = await new AdminSelfProvisionWriter(replay).ProvisionAsync("g-sub-2", "a@example.com", DateTime.UtcNow, CancellationToken.None);

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
        var outcome = await new AdminBindInvitedIdentityWriter(db).BindAsync("g-sub-3", "invited@example.com", CancellationToken.None);

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
        var outcome = await new AdminBindInvitedIdentityWriter(db).BindAsync("g-sub-4", "nobody@example.com", CancellationToken.None);

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
            await new AdminBindInvitedIdentityWriter(first).BindAsync("g-sub-5", "invited2@example.com", CancellationToken.None);

        using var replay = NewControlPlaneContext(connection);
        var outcome = await new AdminBindInvitedIdentityWriter(replay).BindAsync("g-sub-5-again", "invited2@example.com", CancellationToken.None);

        Assert.Equal(BindInvitedOutcome.AlreadyBound, outcome);
    }

    [Fact]
    public async Task Registration_creates_a_pending_user_and_external_login_for_a_new_subject()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (var setup = NewMerchantUserContext(connection))
            await setup.Database.EnsureCreatedAsync();

        using var db = NewMerchantUserContext(connection);
        var write = new RegistrationWrite(
            "m-sub-1", "one@example.com", IsCorrection: false,
            "First", "Last", PersonType: null, IdNumber: null, ProducerCode: null, LicenseNumber: null, Phone: null,
            PhotoObjectKey: null, PhotoContentType: null, DateTime.UtcNow);

        var result = await new MerchantRegistrationSubmitWriter(db).SubmitAsync(write, CancellationToken.None);

        Assert.Equal(RegistrationWriteOutcome.Registered, result.Outcome);
        Assert.NotNull(result.MerchantUserId);

        using var reader = NewMerchantUserContext(connection);
        var stored = await reader.Users.IgnoreQueryFilters().SingleAsync(u => u.Subject == "m-sub-1");
        Assert.Equal(Merchants.Domain.Users.UserStatus.PendingApproval, stored.Status);
        Assert.Null(stored.MerchantId);
        Assert.Equal(1, await reader.ExternalLogins.CountAsync(l => l.Subject == "m-sub-1"));
    }

    [Fact]
    public async Task Registration_returns_DuplicateSubject_for_a_second_registration_of_the_same_subject()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (var setup = NewMerchantUserContext(connection))
            await setup.Database.EnsureCreatedAsync();

        var write = new RegistrationWrite(
            "m-sub-2", "two@example.com", IsCorrection: false,
            "First", "Last", null, null, null, null, null, null, null, DateTime.UtcNow);
        using (var first = NewMerchantUserContext(connection))
            await new MerchantRegistrationSubmitWriter(first).SubmitAsync(write, CancellationToken.None);

        using var replay = NewMerchantUserContext(connection);
        var result = await new MerchantRegistrationSubmitWriter(replay).SubmitAsync(write, CancellationToken.None);

        Assert.Equal(RegistrationWriteOutcome.DuplicateSubject, result.Outcome);
    }

    [Fact]
    public async Task Correction_resubmits_a_rejected_registration_to_pending()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (var setup = NewMerchantUserContext(connection))
            await setup.Database.EnsureCreatedAsync();

        var initial = new RegistrationWrite(
            "m-sub-3", "three@example.com", IsCorrection: false,
            "First", "Last", null, null, null, null, null, null, null, DateTime.UtcNow);
        using (var register = NewMerchantUserContext(connection))
            await new MerchantRegistrationSubmitWriter(register).SubmitAsync(initial, CancellationToken.None);
        using (var reject = NewMerchantUserContext(connection))
            await new MerchantRegistrationWriter(reject, NoOpSecurityTelemetry.Instance).RejectAsync("m-sub-3", CancellationToken.None);

        var correction = initial with { IsCorrection = true, FirstName = "Corrected" };
        using var db = NewMerchantUserContext(connection);
        var result = await new MerchantRegistrationSubmitWriter(db).SubmitAsync(correction, CancellationToken.None);

        Assert.Equal(RegistrationWriteOutcome.Resubmitted, result.Outcome);
        using var reader = NewMerchantUserContext(connection);
        var stored = await reader.Users.IgnoreQueryFilters().SingleAsync(u => u.Subject == "m-sub-3");
        Assert.Equal(Merchants.Domain.Users.UserStatus.PendingApproval, stored.Status);
        Assert.Equal("Corrected", stored.FirstName);
    }

    [Fact]
    public async Task Correction_returns_CorrectionTargetNotFound_when_no_prior_registration_exists()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (var setup = NewMerchantUserContext(connection))
            await setup.Database.EnsureCreatedAsync();

        var correction = new RegistrationWrite(
            "m-sub-4", "four@example.com", IsCorrection: true,
            "First", "Last", null, null, null, null, null, null, null, DateTime.UtcNow);
        using var db = NewMerchantUserContext(connection);
        var result = await new MerchantRegistrationSubmitWriter(db).SubmitAsync(correction, CancellationToken.None);

        Assert.Equal(RegistrationWriteOutcome.CorrectionTargetNotFound, result.Outcome);
    }

    [Fact]
    public async Task Correction_returns_CorrectionTargetNotRejected_for_a_still_pending_registration()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (var setup = NewMerchantUserContext(connection))
            await setup.Database.EnsureCreatedAsync();

        var initial = new RegistrationWrite(
            "m-sub-5", "five@example.com", IsCorrection: false,
            "First", "Last", null, null, null, null, null, null, null, DateTime.UtcNow);
        using (var register = NewMerchantUserContext(connection))
            await new MerchantRegistrationSubmitWriter(register).SubmitAsync(initial, CancellationToken.None);

        var correction = initial with { IsCorrection = true };
        using var db = NewMerchantUserContext(connection);
        var result = await new MerchantRegistrationSubmitWriter(db).SubmitAsync(correction, CancellationToken.None);

        Assert.Equal(RegistrationWriteOutcome.CorrectionTargetNotRejected, result.Outcome);
    }
}
