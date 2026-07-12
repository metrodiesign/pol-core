using Merchants.Domain;
using Merchants.Domain.Users;
using Merchants.Domain.Users.Roles;

namespace Merchants.Tests;

/// <summary>The <see cref="MerchantUser"/> state machine (REQ-1/2.3): only the four legal transitions are exposed,
/// Approve is idempotent for the SAME merchant and throws for a DIFFERENT one, and every illegal transition throws
/// and changes nothing. The merchant edge is now MerchantUser.MerchantId directly (the former separate
/// ProducerTenantAssignment row is absorbed), so Approve takes the merchant id as a parameter.</summary>
public sealed class MerchantUserTests
{
    private static readonly DateTime Now = new(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid MerchantId = Guid.Parse("d2222222-2222-2222-2222-222222222222");

    private static User NewPending() => User.Register("g-sub-1", "p@org.com", Now);

    [Fact]
    public void Register_creates_a_pending_account()
    {
        var account = NewPending();

        Assert.Equal(UserStatus.PendingApproval, account.Status);
        Assert.Equal("g-sub-1", account.Subject);
        Assert.Equal("p@org.com", account.Email);
        Assert.Null(account.MerchantId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_rejects_a_blank_subject(string? subject) =>
        Assert.ThrowsAny<ArgumentException>(() => User.Register(subject!, "p@org.com", Now));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Register_rejects_a_blank_email(string? email) =>
        Assert.ThrowsAny<ArgumentException>(() => User.Register("g-sub-1", email!, Now));

    // --- Approve (PendingApproval -> Active) ---

    [Fact]
    public void Approve_activates_a_pending_account_and_sets_the_merchant()
    {
        var account = NewPending();

        account.Approve(MerchantId, Now);

        Assert.Equal(UserStatus.Active, account.Status);
        Assert.Equal(MerchantId, account.MerchantId);
    }

    [Fact]
    public void Approve_rejects_an_empty_merchant_id() =>
        Assert.Throws<ArgumentException>(() => NewPending().Approve(Guid.Empty, Now));

    [Fact]
    public void Approve_is_an_idempotent_no_op_on_an_active_account_for_the_same_merchant()
    {
        var account = NewPending();
        account.Approve(MerchantId, Now);

        account.Approve(MerchantId, Now); // REQ-6.4 — re-approving succeeds with no change

        Assert.Equal(UserStatus.Active, account.Status);
        Assert.Equal(MerchantId, account.MerchantId);
    }

    [Fact]
    public void Approve_an_active_account_for_a_different_merchant_throws()
    {
        var account = NewPending();
        account.Approve(MerchantId, Now);

        Assert.Throws<InvalidOperationException>(() => account.Approve(Guid.NewGuid(), Now));
        Assert.Equal(MerchantId, account.MerchantId); // unchanged
    }

    [Fact]
    public void Approve_a_rejected_account_throws()
    {
        var account = NewPending();
        account.Reject(Now);

        Assert.Throws<InvalidOperationException>(() => account.Approve(MerchantId, Now)); // must resubmit first (REQ-6.5)
        Assert.Equal(UserStatus.Rejected, account.Status);
    }

    [Fact]
    public void Approve_a_suspended_account_throws()
    {
        var account = NewPending();
        account.Approve(MerchantId, Now);
        account.Suspend(Now);

        Assert.Throws<InvalidOperationException>(() => account.Approve(MerchantId, Now));
        Assert.Equal(UserStatus.Suspended, account.Status);
    }

    // --- Reject (PendingApproval -> Rejected) ---

    [Fact]
    public void Reject_a_pending_account_sets_rejected()
    {
        var account = NewPending();

        account.Reject(Now);

        Assert.Equal(UserStatus.Rejected, account.Status);
    }

    [Fact]
    public void Reject_an_active_account_throws()
    {
        var account = NewPending();
        account.Approve(MerchantId, Now);

        Assert.Throws<InvalidOperationException>(() => account.Reject(Now));
        Assert.Equal(UserStatus.Active, account.Status);
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

        Assert.Equal(UserStatus.PendingApproval, account.Status);
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
        account.Approve(MerchantId, Now);

        Assert.Throws<InvalidOperationException>(() => account.Resubmit(Now));
    }

    // --- Suspend (Active -> Suspended) ---

    [Fact]
    public void Suspend_an_active_account_sets_suspended()
    {
        var account = NewPending();
        account.Approve(MerchantId, Now);

        account.Suspend(Now);

        Assert.Equal(UserStatus.Suspended, account.Status);
    }

    [Fact]
    public void Suspend_a_pending_account_throws()
    {
        var account = NewPending();

        Assert.Throws<InvalidOperationException>(() => account.Suspend(Now));
        Assert.Equal(UserStatus.PendingApproval, account.Status);
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
        account.Approve(MerchantId, Now);

        Assert.Equal(UserStatus.Active, account.Status);
    }

    // --- Person details (REQ-7.1): live on the account (a "merchant" is the company/app, not the person) ---

    [Fact]
    public void SetDetails_computes_display_name_and_stores_the_optional_fields()
    {
        var account = NewPending();

        account.SetDetails(" Acme ", " Co ", PersonType.Juristic, "1234567890123", "PC-1", "LIC-9", "0812345678");

        Assert.Equal("Acme", account.FirstName);          // trimmed
        Assert.Equal("Co", account.LastName);
        Assert.Equal("Acme Co", account.DisplayName);     // computed from first + last
        Assert.Equal(PersonType.Juristic, account.PersonType);
        Assert.Equal("1234567890123", account.IdNumber);
        Assert.Equal("PC-1", account.ProducerCode);
        Assert.Equal("LIC-9", account.LicenseNumber);
        Assert.Equal("0812345678", account.Phone);
    }

    [Theory]
    [InlineData(null, "Co")]
    [InlineData("", "Co")]
    [InlineData("   ", "Co")]
    [InlineData("Acme", null)]
    [InlineData("Acme", " ")]
    public void SetDetails_rejects_a_blank_first_or_last_name(string? first, string? last) =>
        Assert.ThrowsAny<ArgumentException>(() =>
            NewPending().SetDetails(first!, last!, null, null, null, null, null));

    [Fact]
    public void SetDetails_clamps_the_computed_display_name_to_200_chars()
    {
        var account = NewPending();
        var first = new string('a', 200);
        var last = new string('b', 200);

        account.SetDetails(first, last, null, null, null, null, null); // 401 chars composed, must not throw

        Assert.Equal(200, account.DisplayName.Length);
    }

    [Fact]
    public void SetDetails_blanks_optional_fields_to_null()
    {
        var account = NewPending();

        account.SetDetails("Acme", "Co", null, "  ", "", "   ", null);

        Assert.Null(account.IdNumber);
        Assert.Null(account.ProducerCode);
        Assert.Null(account.LicenseNumber);
        Assert.Null(account.Phone);
    }

    [Fact]
    public void SetPhoto_records_the_opaque_key_and_content_type()
    {
        var account = NewPending();

        account.SetPhoto(" key-1 ", " image/jpeg ");

        Assert.Equal("key-1", account.PhotoObjectKey);
        Assert.Equal("image/jpeg", account.PhotoContentType);
    }
}
