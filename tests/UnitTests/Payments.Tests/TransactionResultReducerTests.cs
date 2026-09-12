using Orders.Domain;
using Payments.Application.Ports;
using Platform.Application.Transactions;
using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Tests;

[Trait("Capability", "CheckoutTransactions")]
public sealed class TransactionResultReducerTests
{
    private static readonly Guid MerchantId = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 9, 10, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait("Requirement", "REQ-8.4")]
    [Trait("Requirement", "REQ-8.6")]
    public void First_verified_success_marks_order_paid_and_keeps_first_pointer()
    {
        var order = NewOrder();
        var transaction = NewTransaction(order.Id, 1);
        transaction.BindRedirect("charge-1", "https://psp.example/redirect", Now);

        var reduction = TransactionResultReducer.Apply(
            transaction,
            order,
            new PspChargeConfirmation(PspChargeStatus.Paid, Money.Of(100m, "THB")),
            new TransactionInquiryScheduler(),
            Now);

        Assert.Equal(TransactionStatus.Succeeded, transaction.Status);
        Assert.Equal(PaymentStatus.Paid, order.PaymentStatus);
        Assert.Equal(transaction.Id, order.SuccessfulTransactionId);
        Assert.True(reduction.EmitNormalSuccess);
    }

    [Fact]
    [Trait("Requirement", "REQ-8.7")]
    [Trait("Requirement", "REQ-8.12")]
    public void Pending_or_failed_after_success_does_not_downgrade_transaction_or_order()
    {
        var order = NewOrder();
        var transaction = NewTransaction(order.Id, 1);
        transaction.BindRedirect("charge-1", "https://psp.example/redirect", Now);
        _ = TransactionResultReducer.Apply(
            transaction, order, new PspChargeConfirmation(PspChargeStatus.Paid, null),
            new TransactionInquiryScheduler(), Now);

        _ = TransactionResultReducer.Apply(
            transaction, order, new PspChargeConfirmation(PspChargeStatus.Pending, null),
            new TransactionInquiryScheduler(), Now.AddMinutes(1));
        _ = TransactionResultReducer.Apply(
            transaction, order, new PspChargeConfirmation(PspChargeStatus.Failed, null),
            new TransactionInquiryScheduler(), Now.AddMinutes(2));

        Assert.Equal(TransactionStatus.Succeeded, transaction.Status);
        Assert.Equal(PaymentStatus.Paid, order.PaymentStatus);
        Assert.Equal(transaction.Id, order.SuccessfulTransactionId);
    }

    [Fact]
    [Trait("Requirement", "REQ-8.8")]
    public void Success_after_cancel_is_retained_for_review_without_reopening_order_or_normal_event()
    {
        var order = NewOrder();
        order.Cancel(Now);
        var transaction = NewTransaction(order.Id, 1);
        transaction.BindRedirect("charge-late", "https://psp.example/redirect", Now);

        var reduction = TransactionResultReducer.Apply(
            transaction, order, new PspChargeConfirmation(PspChargeStatus.Paid, null),
            new TransactionInquiryScheduler(), Now.AddMinutes(1));

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(PaymentStatus.Paid, order.PaymentStatus);
        Assert.Equal(transaction.Id, order.SuccessfulTransactionId);
        Assert.Equal(TransactionStatus.Succeeded, transaction.Status);
        Assert.True(transaction.NeedsReview);
        Assert.Equal("paid_needs_review", transaction.ReviewCode);
        Assert.False(reduction.EmitNormalSuccess);
    }

    [Fact]
    [Trait("Requirement", "REQ-8.9")]
    public void Second_verified_success_stays_succeeded_and_keeps_canonical_first_pointer()
    {
        var order = NewOrder();
        var first = NewTransaction(order.Id, 1);
        first.BindRedirect("charge-first", "https://psp.example/first", Now);
        _ = TransactionResultReducer.Apply(
            first, order, new PspChargeConfirmation(PspChargeStatus.Paid, null),
            new TransactionInquiryScheduler(), Now);

        var second = NewTransaction(order.Id, 2);
        second.BindRedirect("charge-second", "https://psp.example/second", Now.AddMinutes(1));
        var reduction = TransactionResultReducer.Apply(
            second, order, new PspChargeConfirmation(PspChargeStatus.Paid, null),
            new TransactionInquiryScheduler(), Now.AddMinutes(1));

        Assert.Equal(TransactionStatus.Succeeded, first.Status);
        Assert.Equal(TransactionStatus.Succeeded, second.Status);
        Assert.Equal(first.Id, order.SuccessfulTransactionId);
        Assert.True(second.NeedsReview);
        Assert.False(reduction.EmitNormalSuccess);
    }

    [Fact]
    [Trait("Requirement", "REQ-8.10")]
    public void Amount_or_currency_mismatch_stays_pending_and_records_review_without_paid_order()
    {
        var order = NewOrder();
        var transaction = NewTransaction(order.Id, 1);
        transaction.BindRedirect("charge-mismatch", "https://psp.example/redirect", Now);

        var reduction = TransactionResultReducer.Apply(
            transaction, order, new PspChargeConfirmation(PspChargeStatus.Paid, Money.Of(99m, "THB")),
            new TransactionInquiryScheduler(), Now);

        Assert.Equal(TransactionStatus.PendingConfirmation, transaction.Status);
        Assert.True(transaction.NeedsReview);
        Assert.Equal("evidence_mismatch", transaction.ReviewCode);
        Assert.Equal(PaymentStatus.Unpaid, order.PaymentStatus);
        Assert.Equal(TransactionStatus.PendingConfirmation, reduction.ObservedStatus);
        Assert.False(reduction.EmitNormalSuccess);
    }

    [Fact]
    [Trait("Requirement", "REQ-8.11")]
    public void Inquiry_schedule_is_bounded_and_repeats_at_hourly_cadence()
    {
        var scheduler = new TransactionInquiryScheduler();
        Assert.Equal(Now.AddMinutes(1), scheduler.Next(Now, 0));
        Assert.Equal(Now.AddMinutes(5), scheduler.Next(Now, 1));
        Assert.Equal(Now.AddMinutes(15), scheduler.Next(Now, 2));
        Assert.Equal(Now.AddHours(1), scheduler.Next(Now, 3));
        Assert.Equal(Now.AddHours(1), scheduler.Next(Now, 50));
    }

    [Fact]
    [Trait("Requirement", "REQ-8.7")]
    [Trait("Requirement", "REQ-8.12")]
    public void Pending_result_does_not_reopen_a_terminal_failed_transaction()
    {
        var order = NewOrder();
        var transaction = NewTransaction(order.Id, 1);
        transaction.BindRedirect("charge-failed", "https://psp.example/redirect", Now);
        transaction.MarkFailed("provider_failed", Now);

        _ = TransactionResultReducer.Apply(
            transaction, order, new PspChargeConfirmation(PspChargeStatus.Pending, null),
            new TransactionInquiryScheduler(), Now.AddMinutes(1));

        Assert.Equal(TransactionStatus.Failed, transaction.Status);
        Assert.False(transaction.IsPotentiallyChargeable);
    }

    private static Order NewOrder()
    {
        var order = Order.CreateDraft(new OrderDraftInput(
            MerchantId,
            Guid.NewGuid(),
            "insurance",
            "THB",
            [new TrustedOrderLineInput(
                "DOC-1", "VMI", "Document", 1, Money.Of(100m, "THB"),
                Money.Zero("THB"), Money.Zero("THB"), Money.Of(100m, "THB"), "catalog")],
            Money.Zero("THB"), Money.Zero("THB"), null, null, Now, "ORD6900000101"));
        order.Issue(Now);
        return order;
    }

    private static Transaction NewTransaction(Guid orderId, int attempt) => Transaction.Create(
        Guid.NewGuid(), MerchantId, orderId, $"TXN-{attempt}-{Guid.NewGuid():N}", attempt,
        Money.Of(100m, "THB"), "card", Code.TwoCTwoP, Guid.NewGuid(), PspEnvironment.Sandbox,
        Guid.NewGuid(), 1, Guid.NewGuid().ToString("N"), "{\"schemaVersion\":1}", Now);
}
