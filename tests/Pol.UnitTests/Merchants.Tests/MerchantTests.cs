using Merchants.Domain;
using Merchants.Domain.Users;
using Merchants.Domain.Users.Roles;

namespace Merchants.Tests;

public sealed class MerchantTests
{
    private static readonly DateTime Now = new(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc);

    private static Merchant CreateValid(string code = "vcommerce", string currency = "THB") =>
        Merchant.Create(code, "vCommerce Co., Ltd.", "0105560000000", "TH", currency,
            ["card", "promptpay"], """{"branding":{"statementName":"VCOMMERCE"}}""", Now);

    [Fact]
    public void Create_valid_merchant_is_active_with_normalized_fields()
    {
        var m = CreateValid(code: "VCommerce", currency: "thb");

        Assert.NotEqual(Guid.Empty, m.Id);
        Assert.Equal("vcommerce", m.Code);          // normalized lowercase
        Assert.Equal("vCommerce Co., Ltd.", m.Name);
        Assert.Equal("0105560000000", m.Note);
        Assert.Equal("THB", m.Currency);            // normalized upper
        Assert.Equal("TH", m.Country);
        Assert.Equal(MerchantStatus.Active, m.Status);
        Assert.Equal("card,promptpay", m.EnabledChannels);
        Assert.Equal(Now, m.CreatedAt);
        Assert.Equal(1, m.Version);
    }

    [Fact]
    public void Admin_mutations_advance_version_only_when_state_changes()
    {
        var merchant = CreateValid();

        merchant.Update(" Updated ", " note ", ["card"], "{}");
        merchant.Suspend();
        merchant.Suspend();
        merchant.Reactivate();
        merchant.Reactivate();

        Assert.Equal("Updated", merchant.Name);
        Assert.Equal("note", merchant.Note);
        Assert.Equal(MerchantStatus.Active, merchant.Status);
        Assert.Equal(4, merchant.Version);
    }

    [Fact]
    public void Create_rejects_code_not_in_allowlist() =>
        Assert.Throws<ArgumentException>(() => CreateValid(code: "evilcorp"));

    [Fact]
    public void Create_rejects_unsupported_currency() =>
        Assert.Throws<ArgumentException>(() => CreateValid(currency: "EUR"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_blank_name(string name) =>
        Assert.Throws<ArgumentException>(() => Merchant.Create(
            "vcommerce", name, "note", "TH", "THB", ["card"], null, Now));

    [Theory]
    [InlineData("Thailand")] // not alpha-2
    [InlineData("T")]
    public void Create_rejects_non_alpha2_country(string country) =>
        Assert.Throws<ArgumentException>(() => Merchant.Create(
            "vcommerce", "vCommerce", "0105560000000", country, "THB", ["card"], null, Now));

    [Fact]
    public void Create_with_null_channels_stores_empty_string()
    {
        var m = Merchant.Create("vsouvenir", "vSouvenir", "0105560000001", "TH", "THB", null, null, Now);

        Assert.Equal(string.Empty, m.EnabledChannels);
        Assert.Equal("{}", m.Metadata); // null metadata defaults to empty JSON object
    }

    [Theory]
    [InlineData("{\"branding\":{\"secret\":\"x\"}}")]
    [InlineData("{\"token\":\"x\"}")]
    [InlineData("{\"unknown\":true}")]
    [InlineData("not-json")]
    public void Create_rejects_non_allowlisted_or_invalid_metadata(string metadata) =>
        Assert.ThrowsAny<Exception>(() => Merchant.Create(
            "vcommerce", "vCommerce", null, "TH", "THB", [], metadata, Now));

    [Fact]
    public void New_merchant_starts_in_sandbox_with_no_pending_environment_switch()
    {
        var m = CreateValid();

        // merchant-psp-settings REQ-2.1: the merchant is the single source of truth and the safe default is sandbox.
        Assert.Equal(SharedKernel.PspEnvironment.Sandbox, m.PaymentEnvironment);
        Assert.Null(m.PendingPaymentEnvironment);
        Assert.Null(m.PendingPaymentEnvironmentApprovalId);
        Assert.Equal(Now, m.PaymentEnvironmentUpdatedAt);
    }

    [Fact]
    public void Environment_switch_is_staged_then_activated_or_rejected_atomically_on_the_aggregate()
    {
        var m = CreateValid();
        var approval = Guid.NewGuid();

        m.StagePaymentEnvironment(SharedKernel.PspEnvironment.Live, approval);
        Assert.Equal(SharedKernel.PspEnvironment.Sandbox, m.PaymentEnvironment);   // not yet applied
        Assert.Equal(SharedKernel.PspEnvironment.Live, m.PendingPaymentEnvironment);
        Assert.Equal(approval, m.PendingPaymentEnvironmentApprovalId);
        Assert.Throws<InvalidOperationException>(() =>
            m.StagePaymentEnvironment(SharedKernel.PspEnvironment.Live, Guid.NewGuid())); // one pending at a time

        var activatedAt = Now.AddHours(1);
        Assert.Equal(SharedKernel.PspEnvironment.Live, m.ActivatePendingPaymentEnvironment(activatedAt));
        Assert.Equal(SharedKernel.PspEnvironment.Live, m.PaymentEnvironment);
        Assert.Equal(activatedAt, m.PaymentEnvironmentUpdatedAt);
        Assert.Null(m.PendingPaymentEnvironment);
        Assert.Null(m.PendingPaymentEnvironmentApprovalId);

        Assert.Throws<InvalidOperationException>(() =>
            m.StagePaymentEnvironment(SharedKernel.PspEnvironment.Live, Guid.NewGuid())); // already live

        m.StagePaymentEnvironment(SharedKernel.PspEnvironment.Sandbox, Guid.NewGuid());
        m.RejectPendingPaymentEnvironment();
        Assert.Equal(SharedKernel.PspEnvironment.Live, m.PaymentEnvironment);        // REQ-2.15: unchanged
        Assert.Null(m.PendingPaymentEnvironment);
    }
}
