using Platform.Application.Transactions;
using Mediator;

namespace Payments.Application.Transactions;

/// <summary>Shared admin/support verify command; it never creates a new Transaction or changes routing.</summary>
public sealed record VerifyTransactionCommand(
    Guid MerchantId,
    Guid TransactionId,
    string IdempotencyKey) : ICommand<Checkouts.Application.CheckoutConfirmResult>;

public sealed class VerifyTransactionHandler(CheckoutTransactionService transactions)
    : ICommandHandler<VerifyTransactionCommand, Checkouts.Application.CheckoutConfirmResult>
{
    public ValueTask<Checkouts.Application.CheckoutConfirmResult> Handle(
        VerifyTransactionCommand command, CancellationToken cancellationToken) =>
        new(transactions.VerifyAsync(
            command.MerchantId, command.TransactionId, "admin-verify", command.IdempotencyKey, cancellationToken));
}
