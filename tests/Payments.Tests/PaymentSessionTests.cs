using Payments.Domain;
using SharedKernel;

namespace Payments.Tests;

/// <summary>
/// Pure domain tests for <see cref="PaymentSession"/> — no DB. They pin the redirect-only lifecycle
/// invariants: a charge binds exactly once (PLAN #11), MarkPaid is guarded by status and idempotent
/// for the same charge id, and Create binds order + amount up-front (PLAN #15).
/// </summary>
public sealed class PaymentSessionTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime At = new(2026, 6, 21, 9, 0, 0, DateTimeKind.Utc);

    private static PaymentSession NewSession() =>
        PaymentSession.Create(TenantId, OrderId, Money.Of(15000, "THB"), "card", PspCode.Omise, At);

    [Fact]
    public void Create_binds_order_amount_method_psp_and_tenant_up_front()
    {
        var session = NewSession();

        Assert.Equal(TenantId, session.TenantId);
        Assert.Equal(OrderId, session.OrderId);
        Assert.Equal(Money.Of(15000, "THB"), session.Amount);
        Assert.Equal("card", session.Method);
        Assert.Equal(PspCode.Omise, session.Psp);
        Assert.Equal(PaymentStatus.Created, session.Status);
        Assert.Null(session.PspExternalChargeId);
        Assert.Null(session.RedirectUrl);
    }

    [Fact]
    public void Create_rejects_empty_tenant()
    {
        Assert.Throws<ArgumentException>(() =>
            PaymentSession.Create(Guid.Empty, OrderId, Money.Of(1, "THB"), "card", PspCode.Omise, At));
    }

    [Fact]
    public void Create_rejects_empty_order()
    {
        Assert.Throws<ArgumentException>(() =>
            PaymentSession.Create(TenantId, Guid.Empty, Money.Of(1, "THB"), "card", PspCode.Omise, At));
    }

    [Fact]
    public void AttachPspCharge_moves_to_Redirected_and_records_charge_and_url()
    {
        var session = NewSession();

        session.AttachPspCharge("chrg_abc", "https://hosted.example/redirect", At);

        Assert.Equal(PaymentStatus.Redirected, session.Status);
        Assert.Equal("chrg_abc", session.PspExternalChargeId);
        Assert.Equal("https://hosted.example/redirect", session.RedirectUrl);
    }

    [Fact]
    public void AttachPspCharge_is_bind_once_second_attach_throws()
    {
        var session = NewSession();
        session.AttachPspCharge("chrg_abc", "https://hosted.example/r1", At);

        // A second attach (even with a brand-new charge id) must be rejected — no double-charge.
        Assert.Throws<InvalidOperationException>(() =>
            session.AttachPspCharge("chrg_def", "https://hosted.example/r2", At));

        Assert.Equal("chrg_abc", session.PspExternalChargeId);
        Assert.Equal(PaymentStatus.Redirected, session.Status);
    }

    [Fact]
    public void MarkPaid_from_Redirected_with_matching_charge_transitions_to_Paid()
    {
        var session = NewSession();
        session.AttachPspCharge("chrg_abc", "https://hosted.example/r", At);

        session.MarkPaid("chrg_abc", At.AddMinutes(1));

        Assert.Equal(PaymentStatus.Paid, session.Status);
        Assert.Equal("chrg_abc", session.PspExternalChargeId);
    }

    [Fact]
    public void MarkPaid_with_charge_mismatch_against_attached_charge_throws()
    {
        var session = NewSession();
        session.AttachPspCharge("chrg_abc", "https://hosted.example/r", At);

        Assert.Throws<InvalidOperationException>(() => session.MarkPaid("chrg_other", At));
        Assert.Equal(PaymentStatus.Redirected, session.Status);
    }

    [Fact]
    public void MarkPaid_is_idempotent_for_same_charge_id_when_already_Paid()
    {
        var session = NewSession();
        session.AttachPspCharge("chrg_abc", "https://hosted.example/r", At);
        session.MarkPaid("chrg_abc", At.AddMinutes(1));

        // Replayed webhook with the same confirmed charge id: a no-op, no throw.
        session.MarkPaid("chrg_abc", At.AddMinutes(5));

        Assert.Equal(PaymentStatus.Paid, session.Status);
    }

    [Fact]
    public void MarkPaid_when_already_Paid_under_a_different_charge_throws()
    {
        var session = NewSession();
        session.AttachPspCharge("chrg_abc", "https://hosted.example/r", At);
        session.MarkPaid("chrg_abc", At);

        Assert.Throws<InvalidOperationException>(() => session.MarkPaid("chrg_other", At));
        Assert.Equal(PaymentStatus.Paid, session.Status);
    }

    [Fact]
    public void MarkPaid_cannot_transition_from_terminal_Failed_status()
    {
        var session = NewSession();
        session.MarkFailed("declined", At);

        Assert.Throws<InvalidOperationException>(() => session.MarkPaid("chrg_abc", At));
        Assert.Equal(PaymentStatus.Failed, session.Status);
    }

    [Fact]
    public void MarkPaid_rejects_blank_charge_id()
    {
        var session = NewSession();

        Assert.Throws<ArgumentException>(() => session.MarkPaid("   ", At));
    }
}
