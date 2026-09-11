using BuildingBlocks.Application;
using Checkouts.Domain;
using Orders.Domain;
using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Platform.Application.Transactions;

public sealed record CheckoutTransactionContext(Order Order, PaymentLink? Link);

public interface ICheckoutTransactionStore
{
    Task<CheckoutTransactionContext?> GetCheckoutForUpdateAsync(
        Guid merchantId, Guid orderId, Guid linkId, CancellationToken cancellationToken);
}

public interface ITransactionRepository
{
    void Add(Transaction transaction);
    void AddEvent(TransactionEvent transactionEvent);
    Task<Transaction?> GetByIdAsync(Guid merchantId, Guid transactionId, CancellationToken cancellationToken);
    Task<PagedResult<Transaction>> ListAsync(
        Guid merchantId, int page, int limit, string? status, CancellationToken cancellationToken);
    Task<Transaction?> GetForUpdateTransactionAsync(Guid merchantId, Guid transactionId, CancellationToken cancellationToken);
    Task<Transaction?> GetPotentialForOrderAsync(Guid merchantId, Guid orderId, CancellationToken cancellationToken);
    Task<Transaction?> GetLatestForOrderAsync(Guid merchantId, Guid orderId, CancellationToken cancellationToken);
    Task<Transaction?> GetByProviderReferenceAsync(
        Guid merchantId, Guid providerAccountId, PspEnvironment? environment, string reference,
        CancellationToken cancellationToken);
    Task<Transaction?> GetByReturnBindingAsync(string returnBinding, CancellationToken cancellationToken);
    Task<IReadOnlyList<(Guid MerchantId, Guid TransactionId)>> ListDueAsync(
        DateTime now, int limit, CancellationToken cancellationToken);
    Task<int> NextAttemptNoAsync(Guid merchantId, Guid orderId, CancellationToken cancellationToken);
    Task<bool> EventExistsAsync(Guid merchantId, Guid transactionId, string source, string eventReference,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<TransactionEvent>> ListEventsAsync(
        Guid merchantId, Guid transactionId, CancellationToken cancellationToken);
}

public interface ITransactionInquiryScheduler
{
    DateTime Next(DateTime now, int attempt);
}

public sealed class TransactionInquiryScheduler : ITransactionInquiryScheduler
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(60),
    ];

    public DateTime Next(DateTime now, int attempt) => now + Delays[Math.Min(Math.Max(attempt, 0), Delays.Length - 1)];
}

public sealed record TransactionStatusView(
    Guid OrderId,
    string PaymentStatus,
    Guid? TransactionId,
    string? TransactionStatus,
    bool CanRetry,
    bool NeedsReview,
    DateTime UpdatedAt);

public sealed record TransactionView(
    Guid TransactionId,
    Guid OrderId,
    Guid MerchantId,
    string TransactionNo,
    int AttemptNo,
    Money Amount,
    string PaymentMethod,
    string Provider,
    Guid ProviderAccountId,
    PspEnvironment Environment,
    Guid CredentialVersionId,
    long ConfigurationVersion,
    string ProviderRequestReference,
    string? ProviderReference,
    string? RedirectUrl,
    TransactionStatus Status,
    string? ProviderStatus,
    string OrderSnapshot,
    bool NeedsReview,
    string? ReviewCode,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? NextInquiryAt,
    long Version);

public static class TransactionViewMapper
{
    public static TransactionView ToView(Transaction transaction) => new(
        transaction.Id,
        transaction.OrderId,
        transaction.MerchantId,
        transaction.TransactionNo,
        transaction.AttemptNo,
        transaction.Amount,
        transaction.PaymentMethod,
        transaction.Provider.ToCode(),
        transaction.ProviderAccountId,
        transaction.Environment,
        transaction.CredentialVersionId,
        transaction.ConfigurationVersion,
        transaction.ProviderRequestReference,
        transaction.ProviderReference,
        transaction.RedirectUrl,
        transaction.Status,
        transaction.ProviderStatus,
        transaction.OrderSnapshot,
        transaction.NeedsReview,
        transaction.ReviewCode,
        transaction.CreatedAt,
        transaction.UpdatedAt,
        transaction.NextInquiryAt,
        transaction.Version);
}
