using Admin.Application;
using Admin.Application.AdminAccountQueries;
using Admin.Application.CreateScopedAdmin;
using Admin.Application.UpdateAdminProfile;
using Admin.Domain;
using BuildingBlocks.Application;

namespace Admin.Tests;

public sealed class MasterDataAndProfileTests
{
    private static readonly DateTime Now = new(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Actor = Guid.NewGuid();

    // ===== Domain: MasterData =====

    [Fact]
    public void Create_sets_fields_active_and_trims()
    {
        var p = Position.Create(" ceo ", "  Chief Executive  ");
        Assert.Equal("ceo", p.Code);
        Assert.Equal("Chief Executive", p.Name);
        Assert.True(p.IsActive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("CEO")]        // uppercase not allowed
    [InlineData("head office")] // space not allowed
    public void Create_rejects_a_bad_code(string code)
    {
        Assert.Throws<ArgumentException>(() => Office.Create(code, "x"));
    }

    [Fact]
    public void Rename_and_toggle_active()
    {
        var l = Level.Create("c7", "C7");
        l.Rename(" C-Seven ");
        Assert.Equal("C-Seven", l.Name);
        l.Deactivate();
        Assert.False(l.IsActive);
        l.Activate();
        Assert.True(l.IsActive);
    }

    // ===== Domain: AdminAccount profile FKs =====

    [Fact]
    public void CreateScoped_stores_profile_fks_and_UpdateProfile_replaces_them()
    {
        var pid = Guid.NewGuid();
        var acc = AdminAccount.CreateScoped("a@x", Now, positionId: pid);
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
        var handler = new CreateScopedAdminHandler(
            new FakeAdminAccountRepository(), new FakeAdminAccountAuditWriter(),
            new FakeMasterDataStore(), new FakeUnitOfWork(), new FixedClock());

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await handler.Handle(new CreateScopedAdminCommand(
                "a@x", Actor, "corr", PositionId: Guid.NewGuid()), default));
    }

    [Fact]
    public async Task CreateScoped_accepts_an_active_master_fk()
    {
        var masters = new FakeMasterDataStore();
        var pos = Position.Create("ceo", "CEO");
        masters.Items.Add(pos);
        var admins = new FakeAdminAccountRepository();
        var handler = new CreateScopedAdminHandler(
            admins, new FakeAdminAccountAuditWriter(), masters, new FakeUnitOfWork(), new FixedClock());

        await handler.Handle(new CreateScopedAdminCommand("a@x", Actor, "corr", PositionId: pos.Id), default);

        Assert.Equal(pos.Id, Assert.Single(admins.Accounts).PositionId);
    }

    [Fact]
    public async Task CreateScoped_rejects_an_inactive_master_fk()
    {
        var masters = new FakeMasterDataStore();
        var pos = Position.Create("ceo", "CEO");
        pos.Deactivate();
        masters.Items.Add(pos);
        var handler = new CreateScopedAdminHandler(
            new FakeAdminAccountRepository(), new FakeAdminAccountAuditWriter(),
            masters, new FakeUnitOfWork(), new FixedClock());

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await handler.Handle(new CreateScopedAdminCommand("a@x", Actor, "corr", PositionId: pos.Id), default));
    }

    // ===== UpdateAdminProfile =====

    private static UpdateAdminProfileHandler ProfileHandler(
        FakeAdminAccountRepository admins, FakeMasterDataStore masters, FakeAdminAccountAuditWriter audit) =>
        new(admins, masters, audit, new FakeUnitOfWork(), new FixedClock());

    [Fact]
    public async Task UpdateProfile_unknown_admin_is_404()
    {
        await Assert.ThrowsAsync<NotFoundException>(async () =>
            await ProfileHandler(new FakeAdminAccountRepository(), new FakeMasterDataStore(), new FakeAdminAccountAuditWriter())
                .Handle(new UpdateAdminProfileCommand(Guid.NewGuid(), null, null, null, null, Actor, "corr"), default));
    }

    [Fact]
    public async Task UpdateProfile_sets_fks_and_audits()
    {
        var admins = new FakeAdminAccountRepository();
        var acc = AdminAccount.CreateScoped("a@x", Now);
        admins.Add(acc);
        var masters = new FakeMasterDataStore();
        var div = Division.Create("north", "ภาคเหนือ");
        masters.Items.Add(div);
        var audit = new FakeAdminAccountAuditWriter();

        await ProfileHandler(admins, masters, audit)
            .Handle(new UpdateAdminProfileCommand(acc.Id, null, null, null, div.Id, Actor, "corr"), default);

        Assert.Equal(div.Id, acc.DivisionId);
        var row = Assert.Single(audit.Appended);
        Assert.Equal(AdminAuditAction.UpdateProfile, row.Action);
        Assert.Equal(acc.Id, row.TargetAdminId);
    }

    [Fact]
    public async Task UpdateProfile_rejects_an_inactive_master_fk()
    {
        var admins = new FakeAdminAccountRepository();
        var acc = AdminAccount.CreateScoped("a@x", Now);
        admins.Add(acc);
        var masters = new FakeMasterDataStore();
        var div = Division.Create("north", "ภาคเหนือ");
        div.Deactivate();
        masters.Items.Add(div);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await ProfileHandler(admins, masters, new FakeAdminAccountAuditWriter())
                .Handle(new UpdateAdminProfileCommand(acc.Id, null, null, null, div.Id, Actor, "corr"), default));
    }

    // ===== Detail exposes resolved refs =====

    [Fact]
    public async Task GetAdminById_exposes_resolved_master_refs()
    {
        var admins = new FakeAdminAccountRepository();
        var masters = new FakeMasterDataStore();
        var pos = Position.Create("ceo", "CEO");
        masters.Items.Add(pos);
        var acc = AdminAccount.CreateScoped("a@x", Now, positionId: pos.Id);
        admins.Add(acc);

        var detail = await new GetAdminByIdHandler(admins, new FakeAdminRoleRepository(), masters)
            .Handle(new GetAdminByIdQuery(acc.Id), default);

        Assert.NotNull(detail);
        Assert.Equal(pos.Id, detail!.Position!.Id);
        Assert.Equal("ceo", detail.Position.Code);
        Assert.Equal("CEO", detail.Position.Name);
        Assert.Null(detail.Office);   // unset dimension stays null
    }
}
