using System.Text.Json;
using BuildingBlocks.Application;
using Checkouts.Application;
using Checkouts.Domain;
using Contracts;
using Orders.Domain;
using Payments.Application.Capabilities;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Platform.Application.Transactions;

/// <summary>
/// Coordinates the checkout Transaction lifecycle. The database transaction ends before provider I/O; all
/// retries use the same persisted Transaction and provider request reference.
/// </summary>
public sealed class CheckoutTransactionService
{
    private readonly ICheckoutTransactionStore _checkout;
    private readonly ITransactionRepository _transactions;
    private readonly IPaymentRouteSelector _routes;
    private readonly IConnectionRepository _connections;
    private readonly IPspAdapterFactory _adapters;
    private readonly IVaultSecretStore _vault;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IIdempotencyStore _idempotency;
    private readonly IOutbox _outbox;
    private readonly IClock _clock;
    private readonly ITransactionInquiryScheduler _inquiry;
    private readonly ITransactionReturnBindingService _returnBindings;

    public CheckoutTransactionService(
        ICheckoutTransactionStore checkout,
        ITransactionRepository transactions,
        IPaymentRouteSelector routes,
        IConnectionRepository connections,
        IPspAdapterFactory adapters,
        IVaultSecretStore vault,
        IUnitOfWork unitOfWork,
        IIdempotencyStore idempotency,
        IOutbox outbox,
        IClock clock,
        ITransactionInquiryScheduler inquiry,
        ITransactionReturnBindingService returnBindings)
    {
        _checkout = checkout;
        _transactions = transactions;
        _routes = routes;
        _connections = connections;
        _adapters = adapters;
        _vault = vault;
        _unitOfWork = unitOfWork;
        _idempotency = idempotency;
        _outbox = outbox;
        _clock = clock;
        _inquiry = inquiry;
        _returnBindings = returnBindings;
    }

    public async Task<CheckoutConfirmResult> StartAsync(
        CheckoutConfirmCommand command,
        Guid linkId,
        string browserBindingId,
        CancellationToken cancellationToken)
    {
        RequireIdempotency(command.IdempotencyKey);
        var method = PaymentMethods.Normalize(command.PaymentMethod);
        var now = _clock.UtcNow;

        var plan = await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var context = await _checkout.GetCheckoutForUpdateAsync(
                command.MerchantId, command.OrderId, linkId, ct).ConfigureAwait(false)
                ?? throw new NotFoundException("Checkout order was not found.");
            var order = context.Order;
            var link = context.Link ?? throw new NotFoundException("Checkout link was not found.");

            if (link.Status != PaymentLinkStatus.Active)
                throw new AccessDeniedException("Checkout link is no longer active.", "checkout_link_revoked");
            if (now >= link.ExpiresAt)
                throw new GoneException("Payment link has expired.");
            if (order.Status != OrderStatus.Open)
                throw new ConflictException("Order is not open for checkout.", "order_not_payable");
            if (order.PaymentStatus == PaymentStatus.Paid)
                throw new ConflictException("Order is already paid.", "order_already_paid");
            if (!string.Equals(order.PaymentChannel, method, StringComparison.Ordinal))
                throw new ConflictException("Payment method does not match the Order.", "payment_method_mismatch");

            // The capability version is a hard precondition. A second tab with a stale snapshot cannot
            // silently switch to whatever Order state the first tab committed.
            if (order.Version != command.OrderVersion)
                throw new ConflictException("Checkout context is stale.", "checkout_context_stale");

            var idempotencyKey = ConfirmKey(command.OrderId, command.IdempotencyKey);
            var first = await _idempotency.TryBeginAsync(
                [idempotencyKey], "checkout.confirm", ct).ConfigureAwait(false);
            var existing = await _transactions.GetPotentialForOrderAsync(command.MerchantId, command.OrderId, ct)
                .ConfigureAwait(false);

            if (!first)
            {
                if (existing is null)
                    throw new ConflictException("The idempotent checkout result is unavailable.", "idempotency_replay");
                var claimed = existing.ProviderReference is null && existing.TryClaimProviderCall(now);
                if (claimed)
                    await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
                return new StartPlan(existing.Id, claimed, VerifyExisting: existing.ProviderReference is not null);
            }

            if (existing is not null)
            {
                var claimed = existing.ProviderReference is null && existing.TryClaimProviderCall(now);
                if (claimed)
                    await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
                return new StartPlan(existing.Id, claimed, VerifyExisting: existing.ProviderReference is not null);
            }

            var route = await _routes.SelectAsync(command.MerchantId, command.OrderId, method, ct)
                .ConfigureAwait(false);
            var connection = await _connections.GetByIdAsync(route.PspConnectionId, ct).ConfigureAwait(false)
                ?? throw new ConflictException("Payment provider account is unavailable.", "payment_capability_unavailable");
            if (connection.MerchantId != command.MerchantId || connection.Psp != route.Psp)
                throw new ConflictException("Payment provider account does not match the Order.", "payment_capability_unavailable");
            connection.EnsureEligible(method);

            var transactionId = Guid.CreateVersion7();
            var transaction = Transaction.Create(
                transactionId,
                command.MerchantId,
                command.OrderId,
                $"TXN-{transactionId:N}",
                await _transactions.NextAttemptNoAsync(command.MerchantId, command.OrderId, ct).ConfigureAwait(false),
                order.Amount,
                method,
                route.Psp,
                route.PspConnectionId,
                route.Environment,
                route.SecretVersionId,
                connection.Version,
                transactionId.ToString("N"),
                OrderSnapshotBuilder.Build(order, now),
                now);
            if (!transaction.TryClaimProviderCall(now))
                throw new ConflictException("Transaction provider call could not be claimed.", "transaction_claim_conflict");
            if (!string.IsNullOrWhiteSpace(browserBindingId))
                transaction.SetReturnBinding(_returnBindings.Issue(
                    transaction.Id, order.Id, browserBindingId, link.ExpiresAt));

            order.MarkPaymentProcessing(now);
            _transactions.Add(transaction);
            AddEventIfNew(
                transaction,
                source: "checkout",
                eventReference: command.IdempotencyKey,
                status: TransactionStatus.Created,
                providerStatus: "created",
                evidenceCode: null,
                safeDetails: null,
                now,
                ct);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            return new StartPlan(transaction.Id, true, false);
        }, cancellationToken).ConfigureAwait(false);

        return await ExecuteProviderAsync(
            command.MerchantId, plan, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CheckoutConfirmResult> VerifyOrderAsync(
        Guid merchantId,
        Guid orderId,
        Guid linkId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        RequireIdempotency(idempotencyKey);
        var transaction = await _transactions.GetLatestForOrderAsync(merchantId, orderId, cancellationToken)
            .ConfigureAwait(false);
        if (transaction is null)
            throw new ConflictException("Checkout has no Transaction to verify.", "transaction_missing");
        return await VerifyAsync(merchantId, transaction.Id, "checkout-verify", idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CheckoutConfirmResult> VerifyAsync(
        Guid merchantId,
        Guid transactionId,
        string source,
        string? eventReference,
        CancellationToken cancellationToken)
    {
        var transaction = await _transactions.GetByIdAsync(merchantId, transactionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("Transaction was not found.");
        if (transaction.Status == TransactionStatus.Succeeded)
            return string.IsNullOrWhiteSpace(eventReference)
                ? ToResult(transaction)
                : await RecordDuplicateEvidenceAsync(
                    merchantId, transaction.Id, source, eventReference, cancellationToken).ConfigureAwait(false);
        if (transaction.ProviderReference is not { Length: > 0 })
            return ToResult(transaction);

        var connection = await _connections.GetByIdAsync(transaction.ProviderAccountId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ConflictException("Pinned provider account is unavailable.", "payment_capability_unavailable");
        if (connection.MerchantId != transaction.MerchantId || connection.Psp != transaction.Provider)
            throw new ConflictException("Pinned provider account does not match the Transaction.", "payment_evidence_mismatch");

        string secret;
        try
        {
            secret = await _vault.ReadVersionForServerAsync(
                transaction.MerchantId, transaction.CredentialVersionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await MarkPendingAsync(
                transaction.MerchantId, transaction.Id, source, eventReference,
                cancellationToken, "credential_unavailable", "credential_unavailable").ConfigureAwait(false);
        }

        PspChargeConfirmation confirmation;
        try
        {
            confirmation = await _adapters.For(transaction.Provider).FetchChargeAsync(
                transaction.ProviderReference, secret, transaction.Environment, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await MarkPendingAsync(
                transaction.MerchantId, transaction.Id, source, eventReference,
                cancellationToken, "provider_timeout").ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return await MarkPendingAsync(
                transaction.MerchantId, transaction.Id, source, eventReference,
                cancellationToken, "provider_inquiry_ambiguous").ConfigureAwait(false);
        }

        return await ApplyVerificationAsync(
            transaction.MerchantId, transaction.Id, source, eventReference, confirmation, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TransactionStatusView> GetStatusAsync(
        Guid orderId,
        Guid linkId,
        CancellationToken cancellationToken)
    {
        var transaction = await _transactions.GetLatestForOrderAsync(
            merchantId: await ResolveMerchantAsync(orderId, linkId, cancellationToken).ConfigureAwait(false),
            orderId,
            cancellationToken).ConfigureAwait(false);
        var context = await ResolveCheckoutAsync(orderId, linkId, cancellationToken).ConfigureAwait(false);
        return new TransactionStatusView(
            orderId,
            context.Order.PaymentStatus.ToString().ToLowerInvariant(),
            transaction?.Id,
            transaction?.Status.ToString().ToLowerInvariant(),
            context.Order.PaymentStatus != PaymentStatus.Paid
                && transaction?.IsPotentiallyChargeable != true,
            transaction?.NeedsReview == true,
            transaction?.UpdatedAt ?? context.Order.UpdatedAt);
    }

    public async Task<TransactionStatusView> GetStatusForTransactionAsync(
        Guid merchantId,
        Guid transactionId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var transaction = await _transactions.GetByIdAsync(merchantId, transactionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("Transaction was not found.");
        if (transaction.OrderId != orderId)
            throw new AccessDeniedException("Transaction does not match the return binding.", "checkout_context_mismatch");
        var context = await _checkout.GetCheckoutForUpdateAsync(
            merchantId, orderId, Guid.Empty, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Checkout order was not found.");
        return new TransactionStatusView(
            orderId,
            context.Order.PaymentStatus.ToString().ToLowerInvariant(),
            transaction.Id,
            transaction.Status.ToString().ToLowerInvariant(),
            context.Order.PaymentStatus != PaymentStatus.Paid && !transaction.IsPotentiallyChargeable,
            transaction.NeedsReview,
            transaction.UpdatedAt);
    }

    public async Task<CheckoutConfirmResult> MarkPendingAsync(
        Guid merchantId,
        Guid transactionId,
        string source,
        string? eventReference,
        CancellationToken cancellationToken,
        string reason = "pending_confirmation",
        string? reviewCode = null)
    {
        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var candidate = await _transactions.GetByIdAsync(merchantId, transactionId, ct).ConfigureAwait(false)
                ?? throw new NotFoundException("Transaction was not found.");
            _ = await _checkout.GetCheckoutForUpdateAsync(
                merchantId, candidate.OrderId, Guid.Empty, ct).ConfigureAwait(false)
                ?? throw new NotFoundException("Checkout order was not found.");
            var tx = await _transactions.GetForUpdateTransactionAsync(merchantId, transactionId, ct).ConfigureAwait(false)
                ?? throw new NotFoundException("Transaction was not found.");
            var now = _clock.UtcNow;
            tx.MarkPendingConfirmation(
                now,
                _inquiry.Next(now, tx.InquiryAttempts),
                providerStatus: reason);
            if (reviewCode is not null)
                tx.FlagNeedsReview(reviewCode, reviewCode, now);
            AddEventIfNew(tx, source, eventReference ?? $"pending:{tx.Id:N}:{Guid.CreateVersion7():N}",
                TransactionStatus.PendingConfirmation, reason, null, null, now, ct);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToResult(tx);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CheckoutConfirmResult> RecordDuplicateEvidenceAsync(
        Guid merchantId,
        Guid transactionId,
        string source,
        string eventReference,
        CancellationToken cancellationToken) =>
        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var candidate = await _transactions.GetByIdAsync(merchantId, transactionId, ct)
                .ConfigureAwait(false) ?? throw new NotFoundException("Transaction was not found.");
            _ = await _checkout.GetCheckoutForUpdateAsync(
                merchantId, candidate.OrderId, Guid.Empty, ct).ConfigureAwait(false)
                ?? throw new NotFoundException("Checkout order was not found.");
            var tx = await _transactions.GetForUpdateTransactionAsync(merchantId, transactionId, ct)
                .ConfigureAwait(false) ?? throw new NotFoundException("Transaction was not found.");
            if (!await _transactions.EventExistsAsync(merchantId, tx.Id, source, eventReference, ct)
                    .ConfigureAwait(false))
            {
                var now = _clock.UtcNow;
                AddEventIfNew(tx, source, eventReference, TransactionStatus.Succeeded,
                    "duplicate", null, null, now, ct);
                await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            return ToResult(tx);
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>Runs the due provider recovery path from backend worker state, independent of a browser.</summary>
    public async Task<CheckoutConfirmResult> ResumeDueAsync(
        Guid merchantId,
        Guid transactionId,
        CancellationToken cancellationToken)
    {
        var transaction = await _transactions.GetByIdAsync(merchantId, transactionId, cancellationToken)
            .ConfigureAwait(false) ?? throw new NotFoundException("Transaction was not found.");
        if (!transaction.IsPotentiallyChargeable)
            return ToResult(transaction);
        if (transaction.ProviderReference is not null)
            return await VerifyAsync(merchantId, transaction.Id, "inquiry", null, cancellationToken)
                .ConfigureAwait(false);

        var plan = await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var context = await _checkout.GetCheckoutForUpdateAsync(
                merchantId, transaction.OrderId, Guid.Empty, ct).ConfigureAwait(false)
                ?? throw new NotFoundException("Checkout order was not found.");
            var current = await _transactions.GetForUpdateTransactionAsync(merchantId, transactionId, ct)
                .ConfigureAwait(false) ?? throw new NotFoundException("Transaction was not found.");
            var now = _clock.UtcNow;
            if (!current.TryClaimProviderCall(now))
                return new StartPlan(current.Id, false, false);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            return new StartPlan(current.Id, true, false);
        }, cancellationToken).ConfigureAwait(false);
        return await ExecuteProviderAsync(merchantId, plan, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(Guid MerchantId, Guid TransactionId)>> ListDueAsync(
        DateTime now, int limit, CancellationToken cancellationToken) =>
        await _transactions.ListDueAsync(now, limit, cancellationToken).ConfigureAwait(false);

    public async Task<CheckoutConfirmResult> MarkEvidenceMismatchAsync(
        Guid merchantId,
        Guid transactionId,
        string source,
        string eventReference,
        string detail,
        CancellationToken cancellationToken)
    {
        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var candidate = await _transactions.GetByIdAsync(merchantId, transactionId, ct)
                .ConfigureAwait(false) ?? throw new NotFoundException("Transaction was not found.");
            _ = await _checkout.GetCheckoutForUpdateAsync(
                merchantId, candidate.OrderId, Guid.Empty, ct).ConfigureAwait(false)
                ?? throw new NotFoundException("Checkout order was not found.");
            var tx = await _transactions.GetForUpdateTransactionAsync(merchantId, transactionId, ct)
                .ConfigureAwait(false) ?? throw new NotFoundException("Transaction was not found.");
            var now = _clock.UtcNow;
            if (tx.Status != TransactionStatus.Succeeded)
                tx.MarkPendingConfirmation(now, _inquiry.Next(now, tx.InquiryAttempts), "evidence_mismatch");
            tx.FlagNeedsReview("evidence_mismatch", detail, now);
            AddEventIfNew(tx, source, eventReference, TransactionStatus.PendingConfirmation,
                "evidence_mismatch", "evidence_mismatch", detail, now, ct);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToResult(tx);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CheckoutConfirmResult> ExecuteProviderAsync(
        Guid merchantId,
        StartPlan plan,
        CancellationToken cancellationToken)
    {
        var transaction = await _transactions.GetByIdAsync(merchantId, plan.TransactionId, cancellationToken)
            .ConfigureAwait(false) ?? throw new NotFoundException("Transaction was not found.");
        if (transaction.Status == TransactionStatus.Succeeded
            || transaction.Status is TransactionStatus.Failed or TransactionStatus.Cancelled or TransactionStatus.Expired)
            return ToResult(transaction);
        if (!plan.ShouldCallProvider)
            return ToResult(transaction);
        if (plan.VerifyExisting)
            return await VerifyAsync(merchantId, transaction.Id, "checkout-confirm", null, cancellationToken)
                .ConfigureAwait(false);

        var connection = await _connections.GetByIdAsync(transaction.ProviderAccountId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ConflictException("Pinned provider account is unavailable.", "payment_capability_unavailable");
        if (connection.MerchantId != merchantId || connection.Psp != transaction.Provider)
            throw new ConflictException("Pinned provider account does not match the Transaction.", "payment_capability_unavailable");

        string secret;
        try
        {
            secret = await _vault.ReadVersionForServerAsync(
                merchantId, transaction.CredentialVersionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await MarkPendingAsync(merchantId, transaction.Id, "checkout-confirm", null, cancellationToken)
                .ConfigureAwait(false);
        }

        PspCharge charge;
        try
        {
            charge = await _adapters.For(transaction.Provider).CreateRedirectChargeAsync(
                new PspChargeRequest(
                    transaction.Id,
                    transaction.MerchantId,
                    transaction.OrderId,
                    transaction.Amount,
                    transaction.PaymentMethod,
                    transaction.Provider,
                    transaction.ProviderAccountId,
                    transaction.Environment,
                    transaction.ProviderRequestReference),
                secret,
                cancellationToken).ConfigureAwait(false);
        }
        catch (PspRejectedException)
        {
            return await MarkFailedAsync(merchantId, transaction.Id, "provider_rejected", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await MarkPendingAsync(merchantId, transaction.Id, "checkout-confirm", null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return await MarkPendingAsync(merchantId, transaction.Id, "checkout-confirm", null, cancellationToken)
                .ConfigureAwait(false);
        }

        return await BindRedirectAsync(merchantId, transaction.Id, charge, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CheckoutConfirmResult> BindRedirectAsync(
        Guid merchantId,
        Guid transactionId,
        PspCharge charge,
        CancellationToken cancellationToken) =>
        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var candidate = await _transactions.GetByIdAsync(merchantId, transactionId, ct)
                .ConfigureAwait(false) ?? throw new NotFoundException("Transaction was not found.");
            _ = await _checkout.GetCheckoutForUpdateAsync(
                merchantId, candidate.OrderId, Guid.Empty, ct).ConfigureAwait(false)
                ?? throw new NotFoundException("Checkout order was not found.");
            var transaction = await _transactions.GetForUpdateTransactionAsync(merchantId, transactionId, ct)
                .ConfigureAwait(false) ?? throw new NotFoundException("Transaction was not found.");
            var now = _clock.UtcNow;
            transaction.BindRedirect(charge.ExternalChargeId, charge.RedirectUrl, now);
            AddEventIfNew(transaction, "provider", charge.ExternalChargeId,
                TransactionStatus.Created, "redirect_created", null, null, now, ct);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToResult(transaction);
        }, cancellationToken).ConfigureAwait(false);

    private async Task<CheckoutConfirmResult> MarkFailedAsync(
        Guid merchantId,
        Guid transactionId,
        string reason,
        CancellationToken cancellationToken) =>
        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var candidate = await _transactions.GetByIdAsync(merchantId, transactionId, ct)
                .ConfigureAwait(false) ?? throw new NotFoundException("Transaction was not found.");
            var context = await _checkout.GetCheckoutForUpdateAsync(
                merchantId, candidate.OrderId, Guid.Empty, ct).ConfigureAwait(false)
                ?? throw new NotFoundException("Checkout order was not found.");
            var transaction = await _transactions.GetForUpdateTransactionAsync(merchantId, transactionId, ct)
                .ConfigureAwait(false) ?? throw new NotFoundException("Transaction was not found.");
            // Provider rejection is chargeless; the order may be retried, but no new provider is selected
            // for this Transaction.
            var now = _clock.UtcNow;
            transaction.MarkFailed(reason, now);
            AddEventIfNew(transaction, "provider", $"failed:{transaction.Id:N}",
                TransactionStatus.Failed, reason, reason, null, now, ct);
            if (context.Order.PaymentStatus != PaymentStatus.Paid
                && !await HasPotentialTransactionAsync(merchantId, transaction.OrderId, transaction.Id, ct)
                    .ConfigureAwait(false))
                context.Order.ReturnToUnpaidIfNoPotentialTransaction(now);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToResult(transaction);
        }, cancellationToken).ConfigureAwait(false);

    private async Task<CheckoutConfirmResult> ApplyVerificationAsync(
        Guid merchantId,
        Guid transactionId,
        string source,
        string? eventReference,
        PspChargeConfirmation confirmation,
        CancellationToken cancellationToken)
    {
        var candidate = await _transactions.GetByIdAsync(merchantId, transactionId, cancellationToken)
            .ConfigureAwait(false) ?? throw new NotFoundException("Transaction was not found.");
        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var context = await _checkout.GetCheckoutForUpdateAsync(
                merchantId, candidate.OrderId, linkId: Guid.Empty, ct).ConfigureAwait(false)
                ?? throw new NotFoundException("Checkout order was not found.");
            var transaction = await _transactions.GetForUpdateTransactionAsync(merchantId, transactionId, ct)
                .ConfigureAwait(false) ?? throw new NotFoundException("Transaction was not found.");
            var order = context.Order;
            var now = _clock.UtcNow;
            var reference = transaction.ProviderReference ?? transaction.ProviderRequestReference;

            if (eventReference is not null && await _transactions.EventExistsAsync(
                    merchantId, transaction.Id, source, eventReference, ct).ConfigureAwait(false))
                return ToResult(transaction);

            var reduction = TransactionResultReducer.Apply(transaction, order, confirmation, _inquiry, now);
            AddEventIfNew(transaction, source, eventReference ?? $"{reduction.ProviderStatus}:{transaction.Id:N}:{Guid.CreateVersion7():N}",
                reduction.ObservedStatus, reduction.ProviderStatus, reduction.EvidenceCode,
                reduction.Detail, now, ct);
            if (reduction.TransactionBecameFailed
                && order.PaymentStatus != PaymentStatus.Paid
                && !await HasPotentialTransactionAsync(merchantId, order.Id, transaction.Id, ct)
                    .ConfigureAwait(false))
                order.ReturnToUnpaidIfNoPotentialTransaction(now);
            if (reduction.EmitNormalSuccess)
                _outbox.Enqueue(new TransactionSucceeded(
                    Guid.CreateVersion7(), transaction.Id, order.Id, merchantId, transaction.Amount,
                    transaction.PaymentMethod, transaction.Provider.ToCode(), reference, false, now));

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToResult(transaction);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> HasPotentialTransactionAsync(
        Guid merchantId, Guid orderId, Guid excludedTransactionId, CancellationToken cancellationToken)
    {
        var candidate = await _transactions.GetPotentialForOrderAsync(merchantId, orderId, cancellationToken)
            .ConfigureAwait(false);
        return candidate is not null && candidate.Id != excludedTransactionId;
    }

    private async Task<CheckoutTransactionContext> ResolveCheckoutAsync(
        Guid orderId, Guid linkId, CancellationToken cancellationToken)
    {
        // The store resolves by both identifiers; the merchant is discovered from the capability-bound row.
        var merchant = await ResolveMerchantAsync(orderId, linkId, cancellationToken).ConfigureAwait(false);
        return await _checkout.GetCheckoutForUpdateAsync(merchant, orderId, linkId, cancellationToken)
            .ConfigureAwait(false) ?? throw new NotFoundException("Checkout order was not found.");
    }

    private async Task<Guid> ResolveMerchantAsync(
        Guid orderId, Guid linkId, CancellationToken cancellationToken)
    {
        var context = await _checkout.GetCheckoutForUpdateAsync(
            Guid.Empty, orderId, linkId, cancellationToken).ConfigureAwait(false);
        return context?.Order.MerchantId
            ?? throw new NotFoundException("Checkout order was not found.");
    }

    private void AddEventIfNew(
        Transaction transaction,
        string source,
        string? eventReference,
        TransactionStatus? status,
        string? providerStatus,
        string? evidenceCode,
        string? safeDetails,
        DateTime now,
        CancellationToken _)
    {
        if (string.IsNullOrWhiteSpace(eventReference))
            eventReference = $"event:{Guid.CreateVersion7():N}";
        _transactions.AddEvent(TransactionEvent.Create(
            transaction.MerchantId, transaction.Id, source, eventReference, status,
            providerStatus, evidenceCode, safeDetails, now, now));
    }

    private static CheckoutConfirmResult ToResult(Transaction transaction) => new(
        transaction.Id,
        transaction.Status,
        transaction.RedirectUrl,
        transaction.Status == TransactionStatus.PendingConfirmation ? 30 : null);

    private static string ConfirmKey(Guid orderId, string idempotencyKey) =>
        $"checkout-confirm:{orderId:D}:{idempotencyKey.Trim()}";

    private static void RequireIdempotency(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl))
            throw new InvalidRequestException("Idempotency-Key is invalid.", "invalid_idempotency_key");
    }

    private sealed record StartPlan(Guid TransactionId, bool ShouldCallProvider, bool VerifyExisting);
}
