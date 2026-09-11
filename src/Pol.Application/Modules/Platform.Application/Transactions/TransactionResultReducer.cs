using Orders.Domain;
using Payments.Application.Ports;
using Payments.Domain;

namespace Platform.Application.Transactions;

public sealed record TransactionReduction(
    TransactionStatus ObservedStatus,
    string ProviderStatus,
    string? EvidenceCode,
    string? Detail,
    bool OrderChangedToPaid,
    bool EmitNormalSuccess,
    bool TransactionBecameFailed);

/// <summary>
/// The single state reducer used after webhook, return-triggered verify, admin verify and inquiry fetches.
/// It is deliberately independent of transport and never treats browser status as financial evidence.
/// </summary>
public static class TransactionResultReducer
{
    public static TransactionReduction Apply(
        Transaction transaction,
        Order order,
        PspChargeConfirmation confirmation,
        ITransactionInquiryScheduler inquiry,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(inquiry);

        if (confirmation.Status == PspChargeStatus.Paid
            && confirmation.Amount is { } collected
            && collected != transaction.Amount)
        {
            transaction.MarkPendingConfirmation(
                now,
                inquiry.Next(now, transaction.InquiryAttempts),
                "amount_currency_mismatch");
            transaction.FlagNeedsReview(
                "evidence_mismatch",
                $"reported={collected.Amount:F4}:{collected.Currency};expected={transaction.Amount.Amount:F4}:{transaction.Amount.Currency}",
                now);
            return new TransactionReduction(
                TransactionStatus.PendingConfirmation,
                "amount_currency_mismatch",
                "evidence_mismatch",
                "provider amount or currency did not match the immutable Transaction amount",
                false,
                false,
                false);
        }

        var reference = transaction.ProviderReference ?? transaction.ProviderRequestReference;
        return confirmation.Status switch
        {
            PspChargeStatus.Paid => ReduceSuccess(transaction, order, reference, now),
            PspChargeStatus.Failed => ReduceFailure(transaction, order, now),
            _ => ReducePending(transaction, inquiry, now),
        };
    }

    private static TransactionReduction ReduceSuccess(
        Transaction transaction,
        Order order,
        string reference,
        DateTime now)
    {
        var needsReview = order.Status == OrderStatus.Cancelled
            || order.PaymentStatus == PaymentStatus.Paid
                && order.SuccessfulTransactionId != transaction.Id;
        var changed = transaction.MarkSucceeded(
            reference,
            "paid",
            now,
            needsReview,
            needsReview ? "paid_needs_review" : null);
        var orderChanged = order.ApplySuccessfulTransaction(transaction.Id, now);
        return new TransactionReduction(
            TransactionStatus.Succeeded,
            "paid",
            needsReview ? "paid_needs_review" : null,
            needsReview ? "verified success requires manual review" : null,
            orderChanged,
            changed && orderChanged && !needsReview,
            false);
    }

    private static TransactionReduction ReduceFailure(Transaction transaction, Order order, DateTime now)
    {
        var wasFailed = transaction.Status == TransactionStatus.Failed;
        transaction.MarkFailed("provider_failed", now);
        return new TransactionReduction(
            TransactionStatus.Failed,
            "failed",
            null,
            null,
            false,
            false,
            !wasFailed && transaction.Status == TransactionStatus.Failed);
    }

    private static TransactionReduction ReducePending(
        Transaction transaction,
        ITransactionInquiryScheduler inquiry,
        DateTime now)
    {
        transaction.MarkPendingConfirmation(
            now,
            inquiry.Next(now, transaction.InquiryAttempts),
            "pending_confirmation");
        return new TransactionReduction(
            TransactionStatus.PendingConfirmation,
            "pending_confirmation",
            null,
            null,
            false,
            false,
            false);
    }
}
