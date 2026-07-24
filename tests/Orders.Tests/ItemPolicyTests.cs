using Orders.Domain.Items;
using SharedKernel;

namespace Orders.Tests;

/// <summary>
/// Pure domain tests for <see cref="ItemPolicy.Create"/>/<see cref="ItemPolicy.Apply"/> (REQ-1, REQ-2) — no
/// DB. Every 400-branch in <c>Apply</c> gets a test, plus the all-unset happy path (1.7/1.11) and duplicate
/// <c>ReferenceNumber</c> across items being allowed at the domain level (1.10).
/// </summary>
public sealed class ItemPolicyTests
{
    private static readonly Guid MerchantId = Guid.NewGuid();

    // Chosen so its Thai-local date (UTC+7) differs from its raw UTC date — 2026-07-23T20:00Z is still
    // 2026-07-23 in UTC but already 2026-07-24 in Thailand. Several tests below rely on this gap to prove
    // DeductedAt is checked against the Thai date, not nowUtc.Date.
    private static readonly DateTime At = new(2026, 7, 23, 20, 0, 0, DateTimeKind.Utc);

    private static ItemPolicy NewPolicy() => ItemPolicy.Create(Guid.NewGuid(), Guid.NewGuid(), MerchantId, At);

    private static ItemPolicyInput Empty() => new(
        InsuranceCategory: null,
        ReferenceNumberType: null,
        ReferenceNumber: null,
        EndorsementNumber: null,
        RenewalReminderNumber: null,
        InsuredObjectReference: null,
        NetPremium: null,
        GrossPremium: null,
        PremiumRemittanceStatus: PremiumRemittanceStatus.NotApplicable,
        DeductedAt: null);

    [Fact]
    public void Create_starts_with_every_reference_field_unset()
    {
        var policy = NewPolicy();

        Assert.Null(policy.InsuranceCategory);
        Assert.Null(policy.ReferenceNumberType);
        Assert.Null(policy.ReferenceNumber);
        Assert.Null(policy.EndorsementNumber);
        Assert.Null(policy.RenewalReminderNumber);
        Assert.Null(policy.InsuredObjectReference);
        Assert.Null(policy.NetPremium);
        Assert.Null(policy.GrossPremium);
        Assert.Equal(PremiumRemittanceStatus.NotApplicable, policy.PremiumRemittanceStatus);
        Assert.Null(policy.DeductedAt);
    }

    [Fact]
    public void Create_rejects_an_empty_merchant_id() =>
        Assert.Throws<ArgumentException>(() => ItemPolicy.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, At));

    [Fact]
    public void Create_rejects_an_empty_order_item_id() =>
        Assert.Throws<ArgumentException>(() => ItemPolicy.Create(Guid.NewGuid(), Guid.Empty, MerchantId, At));

    [Fact]
    public void Apply_with_every_field_unset_succeeds()
    {
        var policy = NewPolicy();

        policy.Apply(Empty(), At);

        Assert.Equal(At, policy.UpdatedAt);
        Assert.Equal(PremiumRemittanceStatus.NotApplicable, policy.PremiumRemittanceStatus);
        Assert.Null(policy.DeductedAt);
    }

    [Fact]
    public void Apply_rejects_a_null_input()
    {
        var policy = NewPolicy();
        Assert.Throws<ArgumentNullException>(() => policy.Apply(null!, At));
    }

    [Fact]
    public void Apply_trims_a_reference_number_and_stores_the_pair()
    {
        var policy = NewPolicy();

        policy.Apply(
            Empty() with { ReferenceNumberType = ReferenceNumberType.PolicyNumber, ReferenceNumber = "  POL-123  " },
            At);

        Assert.Equal(ReferenceNumberType.PolicyNumber, policy.ReferenceNumberType);
        Assert.Equal("POL-123", policy.ReferenceNumber);
    }

    [Fact]
    public void Apply_rejects_a_reference_number_type_without_a_reference_number()
    {
        var policy = NewPolicy();
        var ex = Assert.Throws<ArgumentException>(
            () => policy.Apply(Empty() with { ReferenceNumberType = ReferenceNumberType.PolicyNumber }, At));
        Assert.Equal(nameof(ItemPolicyInput.ReferenceNumberType), ex.ParamName);
    }

    [Fact]
    public void Apply_treats_a_blank_reference_number_as_unset_and_still_rejects_a_set_type()
    {
        var policy = NewPolicy();
        Assert.Throws<ArgumentException>(() => policy.Apply(
            Empty() with { ReferenceNumberType = ReferenceNumberType.PolicyNumber, ReferenceNumber = "   " }, At));
    }

    [Fact]
    public void Apply_rejects_a_reference_number_without_a_type()
    {
        var policy = NewPolicy();
        var ex = Assert.Throws<ArgumentException>(
            () => policy.Apply(Empty() with { ReferenceNumber = "POL-123" }, At));
        Assert.Equal(nameof(ItemPolicyInput.ReferenceNumber), ex.ParamName);
    }

    [Fact]
    public void Apply_rejects_an_endorsement_number_without_a_base_reference()
    {
        var policy = NewPolicy();
        Assert.Throws<ArgumentException>(() => policy.Apply(Empty() with { EndorsementNumber = "END-1" }, At));
    }

    [Fact]
    public void Apply_rejects_a_renewal_reminder_number_without_a_base_reference()
    {
        var policy = NewPolicy();
        Assert.Throws<ArgumentException>(() => policy.Apply(Empty() with { RenewalReminderNumber = "REN-1" }, At));
    }

    [Fact]
    public void Apply_allows_endorsement_and_renewal_numbers_once_a_base_reference_is_set()
    {
        var policy = NewPolicy();

        policy.Apply(Empty() with
        {
            ReferenceNumberType = ReferenceNumberType.PolicyNumber,
            ReferenceNumber = "POL-123",
            EndorsementNumber = "END-1",
            RenewalReminderNumber = "REN-1",
        }, At);

        Assert.Equal("END-1", policy.EndorsementNumber);
        Assert.Equal("REN-1", policy.RenewalReminderNumber);
    }

    [Fact]
    public void Apply_rejects_a_net_premium_without_a_gross_premium()
    {
        var policy = NewPolicy();
        var ex = Assert.Throws<ArgumentException>(
            () => policy.Apply(Empty() with { NetPremium = Money.Of(100m, "THB") }, At));
        Assert.Equal(nameof(ItemPolicyInput.NetPremium), ex.ParamName);
    }

    [Fact]
    public void Apply_rejects_a_gross_premium_without_a_net_premium() =>
        Assert.Throws<ArgumentException>(
            () => NewPolicy().Apply(Empty() with { GrossPremium = Money.Of(100m, "THB") }, At));

    [Fact]
    public void Apply_rejects_net_premium_greater_than_gross_premium() =>
        Assert.Throws<ArgumentException>(() => NewPolicy().Apply(Empty() with
        {
            NetPremium = Money.Of(200m, "THB"),
            GrossPremium = Money.Of(100m, "THB"),
        }, At));

    [Fact]
    public void Apply_allows_net_premium_equal_to_gross_premium()
    {
        var policy = NewPolicy();

        policy.Apply(Empty() with { NetPremium = Money.Of(100m, "THB"), GrossPremium = Money.Of(100m, "THB") }, At);

        Assert.Equal(Money.Of(100m, "THB"), policy.NetPremium);
        Assert.Equal(Money.Of(100m, "THB"), policy.GrossPremium);
    }

    [Fact]
    public void Apply_rejects_a_non_thb_net_premium() =>
        Assert.Throws<ArgumentException>(() => NewPolicy().Apply(Empty() with
        {
            NetPremium = Money.Of(100m, "USD"),
            GrossPremium = Money.Of(200m, "USD"),
        }, At));

    [Fact]
    public void Apply_rejects_a_non_thb_gross_premium_even_when_net_is_thb() =>
        Assert.Throws<ArgumentException>(() => NewPolicy().Apply(Empty() with
        {
            NetPremium = Money.Of(100m, "THB"),
            GrossPremium = Money.Of(200m, "USD"),
        }, At));

    [Fact]
    public void Apply_rejects_deducted_status_without_a_deducted_at_date()
    {
        var policy = NewPolicy();
        var ex = Assert.Throws<ArgumentException>(() => policy.Apply(
            Empty() with { PremiumRemittanceStatus = PremiumRemittanceStatus.Deducted }, At));
        Assert.Equal(nameof(ItemPolicyInput.DeductedAt), ex.ParamName);
    }

    [Fact]
    public void Apply_does_not_require_a_deducted_at_date_while_not_applicable()
    {
        var policy = NewPolicy();

        policy.Apply(Empty() with { InsuredObjectReference = "1กก-1234" }, At);

        Assert.Equal(PremiumRemittanceStatus.NotApplicable, policy.PremiumRemittanceStatus);
        Assert.Null(policy.DeductedAt);
    }

    [Fact]
    public void Apply_rejects_a_future_deducted_at_date()
    {
        var policy = NewPolicy();
        var futureThai = DateOnly.FromDateTime(At.AddHours(7)).AddDays(1);

        Assert.Throws<ArgumentException>(() => policy.Apply(Empty() with
        {
            PremiumRemittanceStatus = PremiumRemittanceStatus.Deducted,
            DeductedAt = futureThai,
        }, At));
    }

    [Fact]
    public void Apply_accepts_a_deducted_at_date_that_is_today_in_thai_local_time_but_tomorrow_in_utc()
    {
        // At is 2026-07-23T20:00Z (raw UTC date 2026-07-23) but already 2026-07-24 in Thailand (UTC+7).
        // A naive UTC-date comparison would wrongly reject 2026-07-24 as "future" — proves the Thai basis.
        var policy = NewPolicy();
        var todayThai = DateOnly.FromDateTime(At.AddHours(7));
        Assert.Equal(new DateOnly(2026, 7, 24), todayThai);

        policy.Apply(Empty() with
        {
            PremiumRemittanceStatus = PremiumRemittanceStatus.Deducted,
            DeductedAt = todayThai,
        }, At);

        Assert.Equal(todayThai, policy.DeductedAt);
    }

    [Fact]
    public void Apply_clears_deducted_at_when_reverting_from_deducted_to_not_applicable()
    {
        var policy = NewPolicy();
        var deductedAt = DateOnly.FromDateTime(At.AddHours(7));
        policy.Apply(Empty() with { PremiumRemittanceStatus = PremiumRemittanceStatus.Deducted, DeductedAt = deductedAt }, At);
        Assert.Equal(deductedAt, policy.DeductedAt);

        policy.Apply(Empty(), At);

        Assert.Equal(PremiumRemittanceStatus.NotApplicable, policy.PremiumRemittanceStatus);
        Assert.Null(policy.DeductedAt);
    }

    [Fact]
    public void Apply_allows_the_same_reference_number_on_two_different_items()
    {
        var input = Empty() with { ReferenceNumberType = ReferenceNumberType.PolicyNumber, ReferenceNumber = "POL-SAME" };

        var first = NewPolicy();
        var second = NewPolicy();
        first.Apply(input, At);
        second.Apply(input, At);

        Assert.Equal("POL-SAME", first.ReferenceNumber);
        Assert.Equal("POL-SAME", second.ReferenceNumber);
    }
}
