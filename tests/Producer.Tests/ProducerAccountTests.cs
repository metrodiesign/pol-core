using Producer.Domain;

namespace Producer.Tests;

/// <summary>The <see cref="ProducerAccount"/> state machine (REQ-1): only the four legal transitions are exposed,
/// Approve is idempotent, and every illegal transition throws and changes nothing. The tenant edge is NOT part of the
/// account (it is a <see cref="ProducerTenantAssignment"/> created by the approval handler), so Approve carries no tenant.</summary>
public sealed class ProducerAccountTests
{
    private static readonly DateTime Now = new(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc);

    private static ProducerAccount NewPending() => ProducerAccount.Register("g-sub-1", "p@org.com", Now);

    [Fact]
    public void Register_creates_a_pending_account()
    {
        var account = NewPending();

        Assert.Equal(ProducerAccountStatus.PendingApproval, account.Status);
        Assert.Equal("g-sub-1", account.Subject);
        Assert.Equal("p@org.com", account.Email);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_rejects_a_blank_subject(string? subject) =>
        Assert.ThrowsAny<ArgumentException>(() => ProducerAccount.Register(subject!, "p@org.com", Now));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Register_rejects_a_blank_email(string? email) =>
        Assert.ThrowsAny<ArgumentException>(() => ProducerAccount.Register("g-sub-1", email!, Now));

    // --- Approve (PendingApproval -> Active) ---

    [Fact]
    public void Approve_activates_a_pending_account()
    {
        var account = NewPending();

        account.Approve(Now);

        Assert.Equal(ProducerAccountStatus.Active, account.Status);
    }

    [Fact]
    public void Approve_is_an_idempotent_no_op_on_an_active_account()
    {
        var account = NewPending();
        account.Approve(Now);

        account.Approve(Now); // REQ-6.4 — re-approving succeeds with no change (tenant-match guard lives in the handler)

        Assert.Equal(ProducerAccountStatus.Active, account.Status);
    }

    [Fact]
    public void Approve_a_rejected_account_throws()
    {
        var account = NewPending();
        account.Reject(Now);

        Assert.Throws<InvalidOperationException>(() => account.Approve(Now)); // must resubmit first (REQ-6.5)
        Assert.Equal(ProducerAccountStatus.Rejected, account.Status);
    }

    [Fact]
    public void Approve_a_suspended_account_throws()
    {
        var account = NewPending();
        account.Approve(Now);
        account.Suspend(Now);

        Assert.Throws<InvalidOperationException>(() => account.Approve(Now));
        Assert.Equal(ProducerAccountStatus.Suspended, account.Status);
    }

    // --- Reject (PendingApproval -> Rejected) ---

    [Fact]
    public void Reject_a_pending_account_sets_rejected()
    {
        var account = NewPending();

        account.Reject(Now);

        Assert.Equal(ProducerAccountStatus.Rejected, account.Status);
    }

    [Fact]
    public void Reject_an_active_account_throws()
    {
        var account = NewPending();
        account.Approve(Now);

        Assert.Throws<InvalidOperationException>(() => account.Reject(Now));
        Assert.Equal(ProducerAccountStatus.Active, account.Status);
    }

    [Fact]
    public void Reject_an_already_rejected_account_throws()
    {
        var account = NewPending();
        account.Reject(Now);

        Assert.Throws<InvalidOperationException>(() => account.Reject(Now));
    }

    // --- Resubmit (Rejected -> PendingApproval) ---

    [Fact]
    public void Resubmit_a_rejected_account_reopens_it_for_review()
    {
        var account = NewPending();
        account.Reject(Now);

        account.Resubmit(Now);

        Assert.Equal(ProducerAccountStatus.PendingApproval, account.Status);
    }

    [Fact]
    public void Resubmit_a_pending_account_throws()
    {
        var account = NewPending();

        Assert.Throws<InvalidOperationException>(() => account.Resubmit(Now));
    }

    [Fact]
    public void Resubmit_an_active_account_throws()
    {
        var account = NewPending();
        account.Approve(Now);

        Assert.Throws<InvalidOperationException>(() => account.Resubmit(Now));
    }

    // --- Suspend (Active -> Suspended) ---

    [Fact]
    public void Suspend_an_active_account_sets_suspended()
    {
        var account = NewPending();
        account.Approve(Now);

        account.Suspend(Now);

        Assert.Equal(ProducerAccountStatus.Suspended, account.Status);
    }

    [Fact]
    public void Suspend_a_pending_account_throws()
    {
        var account = NewPending();

        Assert.Throws<InvalidOperationException>(() => account.Suspend(Now));
        Assert.Equal(ProducerAccountStatus.PendingApproval, account.Status);
    }

    [Fact]
    public void Suspend_a_rejected_account_throws()
    {
        var account = NewPending();
        account.Reject(Now);

        Assert.Throws<InvalidOperationException>(() => account.Suspend(Now));
    }

    // --- Full reject -> correct -> resubmit -> approve loop (REQ-5) ---

    [Fact]
    public void Reject_then_resubmit_then_approve_round_trips_to_active()
    {
        var account = NewPending();

        account.Reject(Now);
        account.Resubmit(Now);
        account.Approve(Now);

        Assert.Equal(ProducerAccountStatus.Active, account.Status);
    }
}
