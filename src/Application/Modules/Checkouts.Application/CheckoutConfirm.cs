using BuildingBlocks.Application;
using Mediator;
using Platform.Application.Transactions;
using Payments.Domain;

namespace Checkouts.Application;

public sealed record CheckoutConfirmCommand(
    Guid MerchantId,
    Guid OrderId,
    long OrderVersion,
    string Proof,
    string CsrfToken,
    string PaymentMethod,
    string IdempotencyKey,
    string BrowserBindingId = "")
    : ICommand<CheckoutConfirmResult>;

public sealed record CheckoutConfirmResult(
    Guid TransactionId,
    TransactionStatus TransactionStatus,
    string? RedirectUrl,
    int? PollAfterSeconds);

public sealed record CheckoutStatusQuery(string Proof) : IQuery<TransactionStatusView>;

public sealed record CheckoutVerifyCommand(
    Guid MerchantId,
    Guid OrderId,
    string Proof,
    string CsrfToken,
    string IdempotencyKey)
    : ICommand<CheckoutConfirmResult>;

public sealed class CheckoutConfirmHandler(
    ICheckoutCapabilityService capabilities,
    CheckoutTransactionService transactions)
    : ICommandHandler<CheckoutConfirmCommand, CheckoutConfirmResult>
{
    public async ValueTask<CheckoutConfirmResult> Handle(
        CheckoutConfirmCommand command, CancellationToken cancellationToken)
    {
        if (!capabilities.TryRead(command.Proof, out var capability))
            throw new AccessDeniedException("Checkout capability is invalid.", "checkout_capability_invalid");
        if (!CryptographicEquals(capability.CsrfToken, command.CsrfToken))
            throw new AccessDeniedException("Checkout CSRF token is invalid.", "checkout_csrf_invalid");
        if (capability.OrderId != command.OrderId || capability.OrderVersion != command.OrderVersion)
            throw new ConflictException("Checkout context does not match the requested Order.", "checkout_context_mismatch");
        return await transactions.StartAsync(command, capability.LinkId, capability.BrowserBindingId, cancellationToken).ConfigureAwait(false);
    }

    private static bool CryptographicEquals(string expected, string actual) =>
        !string.IsNullOrWhiteSpace(actual)
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(actual));
}

public sealed class CheckoutStatusHandler(
    ICheckoutCapabilityService capabilities,
    CheckoutTransactionService transactions)
    : IQueryHandler<CheckoutStatusQuery, TransactionStatusView>
{
    public async ValueTask<TransactionStatusView> Handle(
        CheckoutStatusQuery query, CancellationToken cancellationToken)
    {
        if (!capabilities.TryRead(query.Proof, out var capability))
            throw new AccessDeniedException("Checkout capability is invalid.", "checkout_capability_invalid");
        return await transactions.GetStatusAsync(capability.OrderId, capability.LinkId, cancellationToken)
            .ConfigureAwait(false);
    }
}

public sealed class CheckoutVerifyHandler(
    ICheckoutCapabilityService capabilities,
    CheckoutTransactionService transactions)
    : ICommandHandler<CheckoutVerifyCommand, CheckoutConfirmResult>
{
    public async ValueTask<CheckoutConfirmResult> Handle(
        CheckoutVerifyCommand command, CancellationToken cancellationToken)
    {
        if (!capabilities.TryRead(command.Proof, out var capability))
            throw new AccessDeniedException("Checkout capability is invalid.", "checkout_capability_invalid");
        if (!CryptographicEquals(capability.CsrfToken, command.CsrfToken))
            throw new AccessDeniedException("Checkout CSRF token is invalid.", "checkout_csrf_invalid");
        return await transactions.VerifyOrderAsync(
            command.MerchantId, capability.OrderId, capability.LinkId, command.IdempotencyKey, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool CryptographicEquals(string expected, string actual) =>
        !string.IsNullOrWhiteSpace(actual)
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(actual));
}
