using Admins.Domain.Roles;
using Admins.Domain.Users;

namespace Admins.Tests;

public sealed class PlatformUserTests
{
    private static readonly DateTime Now = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SelfProvision_creates_an_active_super_with_a_bound_non_microsoft_subject()
    {
        var admin = User.SelfProvision("google", "g-sub-1", " Employee@VIRIYAH.CO.TH ", Now);

        Assert.Equal(Tier.Super, admin.Tier);
        Assert.Equal(UserStatus.Active, admin.Status);
        Assert.Equal("g-sub-1", admin.Subject);
        Assert.Equal("Employee@VIRIYAH.CO.TH", admin.Email);
        Assert.Null(admin.TenantId);
    }

    [Theory]
    [InlineData("microsoft")]
    [InlineData("Microsoft")]
    public void Historical_self_provision_rejects_microsoft_provider(string provider)
    {
        Assert.Throws<ArgumentException>(() =>
            User.SelfProvision(provider, "mutable-subject", "ops@org.com", Now));
    }

    [Fact]
    public void CreateScoped_keeps_the_historical_non_microsoft_invite_shape()
    {
        var admin = User.CreateScoped("scoped@org.com", Now);

        Assert.Equal(Tier.Scoped, admin.Tier);
        Assert.Equal(UserStatus.Active, admin.Status);
        Assert.Null(admin.Subject);
        Assert.Null(admin.TenantId);
        Assert.Equal("scoped@org.com", admin.Email);
    }

    [Fact]
    public void JitProvisionMicrosoft_uses_the_canonical_tenant_object_tuple_and_optional_contact()
    {
        var tenantId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var admin = User.JitProvisionMicrosoft(tenantId, objectId, " Employee@VIRIYAH.CO.TH ", Now);

        Assert.Equal(User.MicrosoftProvider, admin.Provider);
        Assert.Equal(tenantId, admin.TenantId);
        Assert.Equal(objectId.ToString("D"), admin.Subject);
        Assert.Equal("Employee@VIRIYAH.CO.TH", admin.Email);
        Assert.Equal(Tier.Scoped, admin.Tier);
        Assert.Equal(UserStatus.Active, admin.Status);
    }

    [Fact]
    public void JitProvisionMicrosoft_accepts_missing_contact_without_placeholder()
    {
        var admin = User.JitProvisionMicrosoft(Guid.NewGuid(), Guid.NewGuid(), null, Now);

        Assert.Null(admin.Email);
    }

    [Fact]
    public void BindSubject_binds_an_invited_account_on_first_login()
    {
        var admin = User.CreateScoped("scoped@org.com", Now);

        admin.BindSubject("google", "g-sub-2");

        Assert.Equal("g-sub-2", admin.Subject);
        Assert.Equal(2, admin.Version);
        Assert.Equal(0, admin.AuthorizationVersion);
    }

    [Theory]
    [InlineData("microsoft")]
    [InlineData("Microsoft")]
    public void Historical_bind_rejects_microsoft_provider(string provider)
    {
        var admin = User.CreateScoped("scoped@org.com", Now);

        Assert.Throws<ArgumentException>(() => admin.BindSubject(provider, "mutable-subject"));
        Assert.Null(admin.Subject);
        Assert.Null(admin.TenantId);
    }

    [Fact]
    public void BindSubject_rejects_rebinding_a_bound_account()
    {
        var admin = User.SelfProvision("google", "g-sub-1", "ops@org.com", Now);

        Assert.Throws<InvalidOperationException>(() => admin.BindSubject("google", "g-sub-other"));
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
        var admin = User.SelfProvision("google", "g-sub-1", "ops@org.com", Now);

        // Oversight can never be locked out — an admin cannot suspend itself (REQ-8.2).
        Assert.Throws<InvalidOperationException>(() => admin.Suspend(admin.Id));
        Assert.Equal(UserStatus.Active, admin.Status);
    }

    [Fact]
    public void Suspend_and_Reactivate_each_bump_the_authorization_version()
    {
        // rls-to-query-filter REQ-4.11 invalidation-matrix source "Status": every write that changes
        // effective authorization bumps AuthorizationVersion so a caller holding a stale lease is denied.
        var admin = User.CreateScoped("scoped@org.com", Now);
        Assert.Equal(0, admin.AuthorizationVersion);

        admin.Suspend(Guid.NewGuid());
        Assert.Equal(1, admin.AuthorizationVersion);

        admin.Reactivate();
        Assert.Equal(2, admin.AuthorizationVersion);
    }

    [Fact]
    public void ChangeTier_promotes_and_demotes_and_bumps_the_authorization_version()
    {
        var admin = User.CreateScoped("scoped@org.com", Now);
        Assert.Equal(Tier.Scoped, admin.Tier);

        admin.ChangeTier(Tier.Super, Guid.NewGuid());
        Assert.Equal(Tier.Super, admin.Tier);
        Assert.Equal(1, admin.AuthorizationVersion);

        admin.ChangeTier(Tier.Scoped, Guid.NewGuid());
        Assert.Equal(Tier.Scoped, admin.Tier);
        Assert.Equal(2, admin.AuthorizationVersion);
    }

    [Fact]
    public void ChangeTier_to_the_current_tier_is_an_idempotent_no_op()
    {
        var admin = User.CreateScoped("scoped@org.com", Now);

        admin.ChangeTier(Tier.Scoped, Guid.NewGuid()); // already Scoped

        Assert.Equal(Tier.Scoped, admin.Tier);
        Assert.Equal(0, admin.AuthorizationVersion); // no spurious bump
    }

    [Fact]
    public void Resource_version_is_monotonic_and_profile_edits_do_not_change_authorization_version()
    {
        var admin = User.CreateScoped("scoped@org.com", Now);
        Assert.Equal(1, admin.Version);

        admin.UpdateProfile(Guid.NewGuid(), null, null, null);

        Assert.Equal(2, admin.Version);
        Assert.Equal(0, admin.AuthorizationVersion);
        Assert.Throws<InvalidOperationException>(() => admin.Suspend(admin.Id));
        Assert.Equal(2, admin.Version);
    }

    [Fact]
    public void ChangeTier_rejects_changing_ones_own_tier()
    {
        var admin = User.SelfProvision("google", "g-sub-1", "ops@org.com", Now);

        // Mirrors Suspend's self-guard (REQ-8.2) — a lone Super demoting itself could strand oversight.
        Assert.Throws<InvalidOperationException>(() => admin.ChangeTier(Tier.Scoped, admin.Id));
        Assert.Equal(Tier.Super, admin.Tier);
        Assert.Equal(0, admin.AuthorizationVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SelfProvision_rejects_a_blank_subject(string? subject) =>
        Assert.ThrowsAny<ArgumentException>(() => User.SelfProvision("google", subject!, "ops@org.com", Now));

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

    [Fact]
    public void System_audit_has_system_actor_and_separate_target()
    {
        var actor = Guid.Parse("a9000000-0000-4000-8000-000000000001");
        var target = Guid.NewGuid();

        var audit = Audit.ForSystem(AuditAction.MicrosoftEmailBind, actor, target, "approval-1", Now);

        Assert.Equal("system", audit.ActorType);
        Assert.Equal(actor, audit.ActorId);
        Assert.Equal(target, audit.TargetAdminId);
        Assert.Equal("approval-1", audit.CorrelationId);
    }
}

public sealed class WorkforceTenantBindingTests
{
    [Fact]
    public void Create_sets_the_singleton_id_and_non_empty_tenant()
    {
        var tenantId = Guid.NewGuid();

        var binding = WorkforceTenantBinding.Create(tenantId);

        Assert.Equal(WorkforceTenantBinding.SingletonId, binding.Id);
        Assert.Equal(tenantId, binding.TenantId);
    }

    [Fact]
    public void Create_rejects_an_empty_tenant()
    {
        Assert.Throws<ArgumentException>(() => WorkforceTenantBinding.Create(Guid.Empty));
    }
}
