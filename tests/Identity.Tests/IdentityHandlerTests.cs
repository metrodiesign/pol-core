using BuildingBlocks.Application;
using Identity.Application.ApproveTenantUser;
using Identity.Application.CompleteRegistration;
using Identity.Application.IssueRegistrationTicket;
using Identity.Application.ResolveTenantUser;
using Identity.Domain;

namespace Identity.Tests;

public sealed class IdentityHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);

    // ---- IssueRegistrationTicket ----

    [Fact]
    public async Task Issue_creates_ticket_for_a_new_subject()
    {
        var users = new FakeTenantUserRepository();
        var tickets = new FakeRegistrationTicketStore();
        var handler = new IssueRegistrationTicketHandler(users, tickets, new FakeUnitOfWork(), new FixedClock());

        var result = await handler.Handle(new IssueRegistrationTicketCommand("sub-1", "a@x.com", "x.com"), default);

        Assert.NotEqual(Guid.Empty, result.TicketId);
        Assert.Single(tickets.Tickets);
    }

    [Fact]
    public async Task Issue_rejects_an_already_registered_subject()
    {
        var users = new FakeTenantUserRepository();
        users.Add(TenantUser.Register("sub-1", "a@x.com", Now));
        var handler = new IssueRegistrationTicketHandler(users, new FakeRegistrationTicketStore(), new FakeUnitOfWork(), new FixedClock());

        await Assert.ThrowsAsync<ConflictException>(async () =>
            await handler.Handle(new IssueRegistrationTicketCommand("sub-1", "a@x.com", null), default));
    }

    // ---- CompleteRegistration ----

    [Fact]
    public async Task Complete_creates_pending_user_login_profile_and_consumes_ticket()
    {
        var users = new FakeTenantUserRepository();
        var tickets = new FakeRegistrationTicketStore();
        var ticket = RegistrationTicket.Issue("sub-9", "u@x.com", null, Now, TimeSpan.FromMinutes(15));
        tickets.Add(ticket);
        var handler = new CompleteRegistrationHandler(users, tickets, new FakeUnitOfWork(), new FixedClock());

        var result = await handler.Handle(new CompleteRegistrationCommand(ticket.Id, "Display Name"), default);

        var user = Assert.Single(users.Users);
        Assert.Equal("sub-9", user.Subject);          // subject from ticket, not the form
        Assert.Equal(TenantUserStatus.PendingApproval, user.Status);
        Assert.Equal(result.TenantUserId, user.Id);
        Assert.Single(users.Logins);
        Assert.Single(users.Profiles);
        Assert.NotNull(ticket.UsedAtUtc);             // single-use consumed
    }

    [Fact]
    public async Task Complete_rejects_an_unknown_ticket()
    {
        var handler = new CompleteRegistrationHandler(
            new FakeTenantUserRepository(), new FakeRegistrationTicketStore(), new FakeUnitOfWork(), new FixedClock());

        await Assert.ThrowsAsync<NotFoundException>(async () =>
            await handler.Handle(new CompleteRegistrationCommand(Guid.NewGuid(), "x"), default));
    }

    [Fact]
    public async Task Complete_rejects_a_replayed_ticket()
    {
        var users = new FakeTenantUserRepository();
        var tickets = new FakeRegistrationTicketStore();
        var ticket = RegistrationTicket.Issue("sub-9", "u@x.com", null, Now, TimeSpan.FromMinutes(15));
        ticket.Consume(Now); // already used
        tickets.Add(ticket);
        var handler = new CompleteRegistrationHandler(users, tickets, new FakeUnitOfWork(), new FixedClock());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await handler.Handle(new CompleteRegistrationCommand(ticket.Id, "x"), default));
        Assert.Empty(users.Users);
    }

    // ---- ApproveTenantUser ----

    [Fact]
    public async Task Approve_activates_the_user_on_the_admin_selected_tenant_and_audits()
    {
        var users = new FakeTenantUserRepository();
        users.Add(TenantUser.Register("sub-1", "a@x.com", Now));
        var audit = new FakeRegistrationAuditWriter();
        var tenantId = Guid.NewGuid();
        var handler = new ApproveTenantUserHandler(users, new FakeTenantDirectory { ActiveResult = true }, audit, new FakeUnitOfWork(), new FixedClock());

        var result = await handler.Handle(
            new ApproveTenantUserCommand("sub-1", tenantId, TenantUserRole.Finance, "admin-7", "corr-1"), default);

        var user = Assert.Single(users.Users);
        Assert.Equal(TenantUserStatus.Active, user.Status);
        Assert.Equal(tenantId, user.TenantId);
        Assert.Equal("Finance", result.Role);
        var row = Assert.Single(audit.Appended);
        Assert.Equal("admin-7", row.AdminSubject);
        Assert.Equal("sub-1", row.TargetSubject);
    }

    [Fact]
    public async Task Approve_rejects_an_inactive_or_unknown_tenant_without_changing_the_user()
    {
        var users = new FakeTenantUserRepository();
        users.Add(TenantUser.Register("sub-1", "a@x.com", Now));
        var handler = new ApproveTenantUserHandler(users, new FakeTenantDirectory { ActiveResult = false }, new FakeRegistrationAuditWriter(), new FakeUnitOfWork(), new FixedClock());

        await Assert.ThrowsAsync<ConflictException>(async () =>
            await handler.Handle(new ApproveTenantUserCommand("sub-1", Guid.NewGuid(), TenantUserRole.Viewer, "admin", "corr"), default));
        Assert.Equal(TenantUserStatus.PendingApproval, users.Users[0].Status);
    }

    [Fact]
    public async Task Approve_rejects_an_unknown_user()
    {
        var handler = new ApproveTenantUserHandler(
            new FakeTenantUserRepository(), new FakeTenantDirectory { ActiveResult = true }, new FakeRegistrationAuditWriter(), new FakeUnitOfWork(), new FixedClock());

        await Assert.ThrowsAsync<NotFoundException>(async () =>
            await handler.Handle(new ApproveTenantUserCommand("ghost", Guid.NewGuid(), TenantUserRole.Viewer, "admin", "corr"), default));
    }

    // ---- ResolveTenantUser ----

    [Fact]
    public async Task Resolve_returns_tenant_and_role_for_an_active_user()
    {
        var users = new FakeTenantUserRepository();
        var user = TenantUser.Register("sub-1", "a@x.com", Now);
        var tenantId = Guid.NewGuid();
        user.Approve(tenantId, TenantUserRole.TenantAdmin, Now);
        users.Add(user);
        var handler = new ResolveTenantUserHandler(users);

        var resolution = await handler.Handle(new ResolveTenantUserQuery("sub-1"), default);

        Assert.NotNull(resolution);
        Assert.Equal(tenantId, resolution!.TenantId);
        Assert.Equal("TenantAdmin", resolution.Role);
    }

    [Fact]
    public async Task Resolve_returns_null_for_pending_or_suspended_or_unknown()
    {
        var users = new FakeTenantUserRepository();
        users.Add(TenantUser.Register("pending", "a@x.com", Now)); // pending
        var suspended = TenantUser.Register("suspended", "b@x.com", Now);
        suspended.Approve(Guid.NewGuid(), TenantUserRole.Viewer, Now);
        suspended.Suspend();
        users.Add(suspended);
        var handler = new ResolveTenantUserHandler(users);

        Assert.Null(await handler.Handle(new ResolveTenantUserQuery("pending"), default));
        Assert.Null(await handler.Handle(new ResolveTenantUserQuery("suspended"), default));
        Assert.Null(await handler.Handle(new ResolveTenantUserQuery("ghost"), default));
    }
}
