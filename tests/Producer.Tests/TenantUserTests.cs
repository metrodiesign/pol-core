using Producer.Domain;

namespace Producer.Tests;

/// <summary>The <see cref="TenantUser"/> state machine (REQ-1): only the four legal transitions are exposed,
/// Approve is idempotent on the same tenant, and every illegal transition throws and changes nothing.</summary>
public sealed class TenantUserTests
{
    private static readonly DateTime Now = new(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid TenantA = Guid.Parse("a0000000-0000-0000-0000-0000000000a1");
    private static readonly Guid TenantB = Guid.Parse("b0000000-0000-0000-0000-0000000000b1");

    private static TenantUser NewPending() => TenantUser.Register("g-sub-1", "p@org.com", Now);

    [Fact]
    public void Register_creates_a_pending_user_with_no_tenant()
    {
        var user = NewPending();

        Assert.Equal(TenantUserStatus.PendingApproval, user.Status);
        Assert.Null(user.TenantId); // NULL until an approval binds it (REQ-1.3)
        Assert.Equal("g-sub-1", user.Subject);
        Assert.Equal("p@org.com", user.Email);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_rejects_a_blank_subject(string? subject) =>
        Assert.ThrowsAny<ArgumentException>(() => TenantUser.Register(subject!, "p@org.com", Now));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Register_rejects_a_blank_email(string? email) =>
        Assert.ThrowsAny<ArgumentException>(() => TenantUser.Register("g-sub-1", email!, Now));

    // --- Approve (PendingApproval -> Active) ---

    [Fact]
    public void Approve_binds_the_tenant_and_activates_a_pending_user()
    {
        var user = NewPending();

        user.Approve(TenantA, Now);

        Assert.Equal(TenantUserStatus.Active, user.Status);
        Assert.Equal(TenantA, user.TenantId);
    }

    [Fact]
    public void Approve_is_an_idempotent_no_op_on_the_same_tenant()
    {
        var user = NewPending();
        user.Approve(TenantA, Now);

        user.Approve(TenantA, Now); // REQ-6.4 — re-approving the same tenant succeeds with no change

        Assert.Equal(TenantUserStatus.Active, user.Status);
        Assert.Equal(TenantA, user.TenantId);
    }

    [Fact]
    public void Approve_an_active_user_onto_a_different_tenant_throws()
    {
        var user = NewPending();
        user.Approve(TenantA, Now);

        Assert.Throws<InvalidOperationException>(() => user.Approve(TenantB, Now));
        Assert.Equal(TenantA, user.TenantId); // unchanged
    }

    [Fact]
    public void Approve_rejects_an_empty_tenant_id()
    {
        var user = NewPending();

        Assert.Throws<ArgumentException>(() => user.Approve(Guid.Empty, Now));
        Assert.Equal(TenantUserStatus.PendingApproval, user.Status);
    }

    [Fact]
    public void Approve_a_rejected_user_throws()
    {
        var user = NewPending();
        user.Reject(Now);

        Assert.Throws<InvalidOperationException>(() => user.Approve(TenantA, Now)); // must resubmit first (REQ-6.5)
        Assert.Equal(TenantUserStatus.Rejected, user.Status);
    }

    [Fact]
    public void Approve_a_suspended_user_throws()
    {
        var user = NewPending();
        user.Approve(TenantA, Now);
        user.Suspend(Now);

        Assert.Throws<InvalidOperationException>(() => user.Approve(TenantA, Now));
        Assert.Equal(TenantUserStatus.Suspended, user.Status);
    }

    // --- Reject (PendingApproval -> Rejected) ---

    [Fact]
    public void Reject_a_pending_user_sets_rejected()
    {
        var user = NewPending();

        user.Reject(Now);

        Assert.Equal(TenantUserStatus.Rejected, user.Status);
    }

    [Fact]
    public void Reject_an_active_user_throws()
    {
        var user = NewPending();
        user.Approve(TenantA, Now);

        Assert.Throws<InvalidOperationException>(() => user.Reject(Now));
        Assert.Equal(TenantUserStatus.Active, user.Status);
    }

    [Fact]
    public void Reject_an_already_rejected_user_throws()
    {
        var user = NewPending();
        user.Reject(Now);

        Assert.Throws<InvalidOperationException>(() => user.Reject(Now));
    }

    // --- Resubmit (Rejected -> PendingApproval) ---

    [Fact]
    public void Resubmit_a_rejected_user_reopens_it_for_review()
    {
        var user = NewPending();
        user.Reject(Now);

        user.Resubmit(Now);

        Assert.Equal(TenantUserStatus.PendingApproval, user.Status);
    }

    [Fact]
    public void Resubmit_a_pending_user_throws()
    {
        var user = NewPending();

        Assert.Throws<InvalidOperationException>(() => user.Resubmit(Now));
    }

    [Fact]
    public void Resubmit_an_active_user_throws()
    {
        var user = NewPending();
        user.Approve(TenantA, Now);

        Assert.Throws<InvalidOperationException>(() => user.Resubmit(Now));
    }

    // --- Suspend (Active -> Suspended) ---

    [Fact]
    public void Suspend_an_active_user_sets_suspended()
    {
        var user = NewPending();
        user.Approve(TenantA, Now);

        user.Suspend(Now);

        Assert.Equal(TenantUserStatus.Suspended, user.Status);
    }

    [Fact]
    public void Suspend_a_pending_user_throws()
    {
        var user = NewPending();

        Assert.Throws<InvalidOperationException>(() => user.Suspend(Now));
        Assert.Equal(TenantUserStatus.PendingApproval, user.Status);
    }

    [Fact]
    public void Suspend_a_rejected_user_throws()
    {
        var user = NewPending();
        user.Reject(Now);

        Assert.Throws<InvalidOperationException>(() => user.Suspend(Now));
    }

    // --- Full reject -> correct -> resubmit -> approve loop (REQ-5) ---

    [Fact]
    public void Reject_then_resubmit_then_approve_round_trips_to_active()
    {
        var user = NewPending();

        user.Reject(Now);
        user.Resubmit(Now);
        user.Approve(TenantA, Now);

        Assert.Equal(TenantUserStatus.Active, user.Status);
        Assert.Equal(TenantA, user.TenantId);
    }
}
