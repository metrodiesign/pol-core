using Admins.Domain.Permissions;
using Admins.Domain.Roles;
using Admins.Domain.Users;

namespace Admins.Tests;

public sealed class PlatformUserTests
{
    private static readonly DateTime Now = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SelfProvision_creates_an_active_super_with_a_bound_subject()
    {
        var admin = User.SelfProvision("g-sub-1", "ops@org.com", Now);

        Assert.Equal(Tier.Super, admin.Tier);
        Assert.Equal(UserStatus.Active, admin.Status);
        Assert.Equal("g-sub-1", admin.Subject);
        Assert.Equal("ops@org.com", admin.Email);
    }

    [Fact]
    public void CreateScoped_creates_an_active_scoped_invite_with_no_subject()
    {
        var admin = User.CreateScoped("scoped@org.com", Now);

        Assert.Equal(Tier.Scoped, admin.Tier);
        Assert.Equal(UserStatus.Active, admin.Status);
        Assert.Null(admin.Subject); // unbound until first login (REQ-3.1)
        Assert.Equal("scoped@org.com", admin.Email);
    }

    [Fact]
    public void BindSubject_binds_an_invited_account_on_first_login()
    {
        var admin = User.CreateScoped("scoped@org.com", Now);

        admin.BindSubject("g-sub-2");

        Assert.Equal("g-sub-2", admin.Subject);
    }

    [Fact]
    public void BindSubject_rejects_rebinding_a_bound_account()
    {
        var admin = User.SelfProvision("g-sub-1", "ops@org.com", Now);

        Assert.Throws<InvalidOperationException>(() => admin.BindSubject("g-sub-other"));
    }

    [Fact]
    public void Suspend_revokes_another_admin()
    {
        var admin = User.CreateScoped("scoped@org.com", Now);

        admin.Suspend(Guid.NewGuid()); // a different acting admin

        Assert.Equal(UserStatus.Suspended, admin.Status);
    }

    [Fact]
    public void Suspend_rejects_self_suspension()
    {
        var admin = User.SelfProvision("g-sub-1", "ops@org.com", Now);

        // Oversight can never be locked out — an admin cannot suspend itself (REQ-8.2).
        Assert.Throws<InvalidOperationException>(() => admin.Suspend(admin.Id));
        Assert.Equal(UserStatus.Active, admin.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SelfProvision_rejects_a_blank_subject(string? subject) =>
        Assert.ThrowsAny<ArgumentException>(() => User.SelfProvision(subject!, "ops@org.com", Now));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CreateScoped_rejects_a_blank_email(string? email) =>
        Assert.ThrowsAny<ArgumentException>(() => User.CreateScoped(email!, Now));

    [Fact]
    public void Assignment_rejects_empty_ids()
    {
        Assert.Throws<ArgumentException>(() => MerchantAccess.Create(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), Now));
        Assert.Throws<ArgumentException>(() => MerchantAccess.Create(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), Now));
    }

    [Fact]
    public void Audit_requires_an_action_correlation_and_actor()
    {
        Assert.ThrowsAny<ArgumentException>(() => Audit.For("", Guid.NewGuid(), "corr", Now));
        Assert.ThrowsAny<ArgumentException>(() => Audit.For(AuditAction.Suspend, Guid.NewGuid(), "", Now));
        Assert.Throws<ArgumentException>(() => Audit.For(AuditAction.Suspend, Guid.Empty, "corr", Now));
    }
}
