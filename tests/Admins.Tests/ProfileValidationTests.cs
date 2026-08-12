using Admins.Application;
using Admins.Application.Roles;
using Admins.Application.Users;
using Admins.Domain.Roles;
using Admins.Domain.Users;
using BuildingBlocks.Application;

namespace Admins.Tests;

// Profile-FK validation + detail tests (the entity-invariant section of the old combined test file moved to
// the four per-module test projects in masterdata-split task 1). Seeds go through FakeProfileLookup's
// enum-keyed rows, never a reference-module type.
public sealed class ProfileValidationTests
{
    private static readonly DateTime Now = new(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Actor = Guid.NewGuid();

    // ===== Domain: User profile FKs =====

    [Fact]
    public void CreateScoped_stores_profile_fks_and_UpdateProfile_replaces_them()
    {
        var pid = Guid.NewGuid();
        var acc = User.CreateScoped("a@x", Now, positionId: pid);
        Assert.Equal(pid, acc.PositionId);
        Assert.Null(acc.OfficeId);

        var oid = Guid.NewGuid();
        acc.UpdateProfile(positionId: null, officeId: oid, levelId: null, divisionId: null);
        Assert.Null(acc.PositionId);              // full replace clears the previous position
        Assert.Equal(oid, acc.OfficeId);
    }

    // ===== CreateScopedAdmin: FK validation =====

    [Fact]
    public async Task CreateScoped_rejects_an_unknown_master_fk()
    {
        var handler = new CreateScopedHandler(
            new FakePlatformUserRepository(), new FakePlatformUserAuditWriter(),
            new FakeProfileLookup(), new FakeUnitOfWork(), new FixedClock());

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await handler.Handle(new CreateScopedCommand(
                "a@x", Actor, "corr", PositionId: Guid.NewGuid()), default));
    }

    [Fact]
    public async Task CreateScoped_accepts_an_active_master_fk()
    {
        var masters = new FakeProfileLookup();
        var posId = masters.Add(ProfileField.Position, "ceo", "CEO");
        var admins = new FakePlatformUserRepository();
        var handler = new CreateScopedHandler(
            admins, new FakePlatformUserAuditWriter(), masters, new FakeUnitOfWork(), new FixedClock());

        await handler.Handle(new CreateScopedCommand("a@x", Actor, "corr", PositionId: posId), default);

        Assert.Equal(posId, Assert.Single(admins.Accounts).PositionId);
    }

    [Fact]
    public async Task CreateScoped_rejects_an_inactive_master_fk()
    {
        var masters = new FakeProfileLookup();
        var posId = masters.Add(ProfileField.Position, "ceo", "CEO", isActive: false);
        var handler = new CreateScopedHandler(
            new FakePlatformUserRepository(), new FakePlatformUserAuditWriter(),
            masters, new FakeUnitOfWork(), new FixedClock());

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await handler.Handle(new CreateScopedCommand("a@x", Actor, "corr", PositionId: posId), default));
    }

    // ===== UpdateAdminProfile =====

    private static UpdateProfileHandler ProfileHandler(
        FakePlatformUserRepository admins, FakeProfileLookup masters, FakePlatformUserAuditWriter audit) =>
        new(admins, masters, audit, new FakeUnitOfWork(), new FixedClock());

    [Fact]
    public async Task UpdateProfile_unknown_admin_is_404()
    {
        await Assert.ThrowsAsync<NotFoundException>(async () =>
            await ProfileHandler(new FakePlatformUserRepository(), new FakeProfileLookup(), new FakePlatformUserAuditWriter())
                .Handle(new UpdateProfileCommand(
                    Guid.NewGuid(), null, null, null, null, Actor, "corr", 1), default));
    }

    [Fact]
    public async Task UpdateProfile_sets_fks_and_audits()
    {
        var admins = new FakePlatformUserRepository();
        var acc = User.CreateScoped("a@x", Now);
        admins.Add(acc);
        var masters = new FakeProfileLookup();
        var divId = masters.Add(ProfileField.Division, "north", "ภาคเหนือ");
        var audit = new FakePlatformUserAuditWriter();

        await ProfileHandler(admins, masters, audit)
            .Handle(new UpdateProfileCommand(
                acc.Id, null, null, null, divId, Actor, "corr", acc.Version), default);

        Assert.Equal(divId, acc.DivisionId);
        var row = Assert.Single(audit.Appended);
        Assert.Equal(AuditAction.UpdateProfile, row.Action);
        Assert.Equal(acc.Id, row.TargetAdminId);
    }

    [Fact]
    public async Task UpdateProfile_rejects_an_inactive_master_fk()
    {
        var admins = new FakePlatformUserRepository();
        var acc = User.CreateScoped("a@x", Now);
        admins.Add(acc);
        var masters = new FakeProfileLookup();
        var divId = masters.Add(ProfileField.Division, "north", "ภาคเหนือ", isActive: false);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await ProfileHandler(admins, masters, new FakePlatformUserAuditWriter())
                .Handle(new UpdateProfileCommand(
                    acc.Id, null, null, null, divId, Actor, "corr", acc.Version), default));
    }

    // ===== Detail exposes resolved refs =====

    [Fact]
    public async Task GetAdminById_exposes_resolved_master_refs()
    {
        var admins = new FakePlatformUserRepository();
        var masters = new FakeProfileLookup();
        var posId = masters.Add(ProfileField.Position, "ceo", "CEO");
        var acc = User.CreateScoped("a@x", Now, positionId: posId);
        admins.Add(acc);

        var detail = await new GetAdminByIdHandler(admins, new FakeAdminRoleRepository(), masters)
            .Handle(new GetAdminByIdQuery(acc.Id), default);

        Assert.NotNull(detail);
        Assert.Equal(posId, detail!.Position!.Id);
        Assert.Equal("ceo", detail.Position.Code);
        Assert.Equal("CEO", detail.Position.Name);
        Assert.Null(detail.Office);   // unset dimension stays null
    }
}
