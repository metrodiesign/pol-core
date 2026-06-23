using Identity.Domain;

namespace Identity.Tests;

public sealed class TenantUserTests
{
    private static readonly DateTime Now = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Register_creates_a_pending_user_with_no_tenant_or_role()
    {
        var user = TenantUser.Register("google-sub-1", "a@example.com", Now);

        Assert.Equal(TenantUserStatus.PendingApproval, user.Status);
        Assert.Null(user.TenantId);
        Assert.Null(user.Role);
        Assert.Equal("google-sub-1", user.Subject);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_rejects_blank_subject(string? subject) =>
        Assert.ThrowsAny<ArgumentException>(() => TenantUser.Register(subject!, "a@example.com", Now));

    [Fact]
    public void Approve_binds_admin_selected_tenant_and_role_and_activates()
    {
        var user = TenantUser.Register("google-sub-1", "a@example.com", Now);
        var tenantId = Guid.NewGuid();

        user.Approve(tenantId, TenantUserRole.Finance, Now);

        Assert.Equal(TenantUserStatus.Active, user.Status);
        Assert.Equal(tenantId, user.TenantId);
        Assert.Equal(TenantUserRole.Finance, user.Role);
    }

    [Fact]
    public void Approve_rejects_a_non_pending_user()
    {
        var user = TenantUser.Register("google-sub-1", "a@example.com", Now);
        user.Approve(Guid.NewGuid(), TenantUserRole.Viewer, Now);

        // Re-approval / overwrite of an active user is rejected (REQ-5.5).
        Assert.Throws<InvalidOperationException>(() => user.Approve(Guid.NewGuid(), TenantUserRole.TenantAdmin, Now));
    }

    [Fact]
    public void Approve_rejects_an_undefined_role()
    {
        var user = TenantUser.Register("google-sub-1", "a@example.com", Now);

        // A numeric cast outside Viewer/Finance/TenantAdmin must not bind (REQ-1.6/1.3).
        Assert.Throws<ArgumentException>(() => user.Approve(Guid.NewGuid(), (TenantUserRole)999, Now));
    }

    [Fact]
    public void Approve_rejects_empty_tenant()
    {
        var user = TenantUser.Register("google-sub-1", "a@example.com", Now);
        Assert.Throws<ArgumentException>(() => user.Approve(Guid.Empty, TenantUserRole.Viewer, Now));
    }

    [Fact]
    public void Suspend_revokes_access()
    {
        var user = TenantUser.Register("google-sub-1", "a@example.com", Now);
        user.Approve(Guid.NewGuid(), TenantUserRole.Viewer, Now);

        user.Suspend();

        Assert.Equal(TenantUserStatus.Suspended, user.Status);
    }

    [Fact]
    public void Google_external_login_links_to_the_user()
    {
        var userId = Guid.NewGuid();
        var login = ExternalLogin.Google("google-sub-1", userId);

        Assert.Equal("google", login.Provider);
        Assert.Equal("google-sub-1", login.Subject);
        Assert.Equal(userId, login.TenantUserId);
    }
}
