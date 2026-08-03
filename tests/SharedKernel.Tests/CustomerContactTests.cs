using SharedKernel;

namespace SharedKernel.Tests;

/// <summary>purchase-flow-completion REQ-6.6/6.7 — the ONE place a customer contact is validated, shared by
/// Checkouts and Orders. Name and phone are required, the phone must read as a phone number, an email is
/// optional but must parse when given, and no rejection message echoes the offending value.</summary>
public sealed class CustomerContactTests
{
    [Fact]
    public void A_valid_contact_is_trimmed_and_kept()
    {
        var contact = CustomerContact.Of("  Somchai Jaidee  ", " 081-234-5678 ", " buyer@example.com ");

        Assert.Equal("Somchai Jaidee", contact.Name);
        Assert.Equal("081-234-5678", contact.Phone);
        Assert.Equal("buyer@example.com", contact.Email);
    }

    [Fact]
    public void Email_is_optional()
    {
        var contact = CustomerContact.Of("Somchai Jaidee", "0812345678", null);

        Assert.Null(contact.Email);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Name_is_required(string? name) =>
        Assert.Throws<ArgumentException>(() => CustomerContact.Of(name, "0812345678", null));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Phone_is_required(string? phone) =>
        Assert.Throws<ArgumentException>(() => CustomerContact.Of("Somchai Jaidee", phone, null));

    [Theory]
    [InlineData("0812345")]              // 7 digits — too short
    [InlineData("08123456789012345")]    // 17 digits — too long (and over the column width)
    [InlineData("081-234-5678 ext 2")]   // letters are not phone characters
    [InlineData("(081) 234-5678")]       // parentheses are not in the accepted separator set
    public void A_phone_that_is_not_a_phone_number_is_rejected(string phone) =>
        Assert.Throws<ArgumentException>(() => CustomerContact.Of("Somchai Jaidee", phone, null));

    [Theory]
    [InlineData("0812345678")]
    [InlineData("081-234-5678")]
    [InlineData("+66 81 234 5678")]
    public void A_phone_that_is_a_phone_number_is_accepted(string phone) =>
        Assert.Equal(phone, CustomerContact.Of("Somchai Jaidee", phone, null).Phone);

    [Theory]
    [InlineData("buyer")]
    [InlineData("buyer@")]
    [InlineData("@example.com")]
    [InlineData("Somchai <buyer@example.com>")]   // a display-name form is not an address
    public void A_malformed_email_is_rejected(string email) =>
        Assert.Throws<ArgumentException>(() => CustomerContact.Of("Somchai Jaidee", "0812345678", email));

    [Fact]
    public void The_rejection_never_echoes_the_offending_value()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => CustomerContact.Of("Somchai Jaidee", "0812345678", "leaked-secret@@@"));

        Assert.DoesNotContain("leaked-secret", ex.Message, StringComparison.Ordinal);
    }

    // F-03 — the notification goes to the phone first, the email second, and nowhere when there is neither.
    [Fact]
    public void The_notification_recipient_prefers_the_phone()
    {
        Assert.Equal("0812345678",
            CustomerContact.Of("Somchai Jaidee", "0812345678", "buyer@example.com").NotificationRecipient);
        Assert.Equal("buyer@example.com",
            CustomerContact.FromStorage("Somchai Jaidee", "", "buyer@example.com").NotificationRecipient);
        Assert.Null(CustomerContact.Unspecified.NotificationRecipient);
    }

    // The placeholder must match the column DEFAULT the migration writes, or a backfilled row and a
    // code-created one would disagree about what "unknown" looks like.
    [Fact]
    public void The_unspecified_contact_matches_the_column_defaults()
    {
        Assert.Equal("(ไม่ระบุ)", CustomerContact.Unspecified.Name);
        Assert.Equal(string.Empty, CustomerContact.Unspecified.Phone);
        Assert.Null(CustomerContact.Unspecified.Email);
    }
}
