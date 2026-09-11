using Platform.Application.Transactions;
namespace Platform.Application.Transactions;

public sealed record TransactionReturnBinding(
    Guid TransactionId,
    Guid OrderId,
    string BrowserBindingId,
    DateTime ExpiresAt);

public interface ITransactionReturnBindingService
{
    string Issue(Guid transactionId, Guid orderId, string browserBindingId, DateTime expiresAt);
    bool TryRead(string value, out TransactionReturnBinding binding);
}
