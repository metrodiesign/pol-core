using Platform.Application.Transactions;
using BuildingBlocks.Application;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Domain.Psp;

namespace Payments.Application.Transactions;

public enum TransactionWebhookOutcome
{
    Processed,
    Pending,
    Duplicate,
    Rejected,
    Deferred,
}

public sealed record TransactionWebhookResult(
    TransactionWebhookOutcome Outcome,
    Guid? TransactionId = null,
    string? Code = null);

public sealed record HandleTransactionWebhookCommand(
    Guid ProviderAccountId,
    string RawPayload,
    string Signature) : Mediator.ICommand<TransactionWebhookResult>;

/// <summary>Provider callback boundary for the new Transaction model.</summary>
public sealed class HandleTransactionWebhookHandler(
    IConnectionRepository connections,
    ITransactionRepository transactions,
    IVaultSecretStore vault,
    IPspAdapterFactory adapters,
    CheckoutTransactionService processor)
    : Mediator.ICommandHandler<HandleTransactionWebhookCommand, TransactionWebhookResult>
{
    public async ValueTask<TransactionWebhookResult> Handle(
        HandleTransactionWebhookCommand command,
        CancellationToken cancellationToken)
    {
        var connection = await connections.GetByIdAsync(command.ProviderAccountId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("Provider account was not found.");
        var adapter = adapters.For(connection.Psp);
        var reference = adapter.ExtractWebhookReference(command.RawPayload);
        var transaction = await transactions.GetByProviderReferenceAsync(
            connection.MerchantId, connection.Id, environment: null,
            reference.ExternalChargeId, cancellationToken).ConfigureAwait(false);
        if (transaction is null)
            return new TransactionWebhookResult(TransactionWebhookOutcome.Deferred, Code: "transaction_unresolved");
        if (transaction.ProviderAccountId != connection.Id
            || transaction.Provider != connection.Psp)
            return new TransactionWebhookResult(TransactionWebhookOutcome.Rejected, transaction.Id, "binding_mismatch");
        if (transaction.ProviderReference is null)
            return new TransactionWebhookResult(TransactionWebhookOutcome.Deferred, transaction.Id, "charge_unbound");
        if (await transactions.EventExistsAsync(
                transaction.MerchantId, transaction.Id, "webhook", reference.ExternalEventId, cancellationToken)
            .ConfigureAwait(false))
            return new TransactionWebhookResult(TransactionWebhookOutcome.Duplicate, transaction.Id);

        string secret;
        try
        {
            secret = await vault.ReadVersionForServerAsync(
                transaction.MerchantId, transaction.CredentialVersionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await processor.MarkPendingAsync(
                transaction.MerchantId, transaction.Id, "webhook", reference.ExternalEventId,
                cancellationToken, "credential_unavailable", "credential_unavailable").ConfigureAwait(false);
            return new TransactionWebhookResult(TransactionWebhookOutcome.Deferred, transaction.Id, "credential_unavailable");
        }

        if (!adapter.VerifyWebhook(command.RawPayload, command.Signature, secret))
            return new TransactionWebhookResult(TransactionWebhookOutcome.Rejected, transaction.Id, "signature_invalid");

        WebhookEvent webhook;
        try
        {
            webhook = adapter.ParseWebhook(command.RawPayload);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or System.Text.Json.JsonException)
        {
            return new TransactionWebhookResult(TransactionWebhookOutcome.Rejected, transaction.Id, "payload_invalid");
        }

        if (!string.Equals(webhook.ExternalChargeId, transaction.ProviderReference, StringComparison.Ordinal)
            && !string.Equals(webhook.ExternalChargeId, transaction.ProviderRequestReference, StringComparison.Ordinal))
        {
            await processor.MarkEvidenceMismatchAsync(
                transaction.MerchantId, transaction.Id, "webhook", webhook.EventId,
                "provider reference did not match the pinned Transaction reference", cancellationToken)
                .ConfigureAwait(false);
            return new TransactionWebhookResult(TransactionWebhookOutcome.Processed, transaction.Id, "reference_mismatch");
        }

        var result = await processor.VerifyAsync(
            transaction.MerchantId, transaction.Id, "webhook", webhook.EventId, cancellationToken)
            .ConfigureAwait(false);
        return result.TransactionStatus switch
        {
            Payments.Domain.TransactionStatus.Succeeded => new TransactionWebhookResult(
                TransactionWebhookOutcome.Processed, transaction.Id),
            Payments.Domain.TransactionStatus.PendingConfirmation => new TransactionWebhookResult(
                TransactionWebhookOutcome.Pending, transaction.Id),
            _ => new TransactionWebhookResult(TransactionWebhookOutcome.Processed, transaction.Id),
        };
    }
}
