using BuildingBlocks.Application;
using Checkouts.Application;
using Checkouts.Domain;
using Mediator;
using Orders.Domain;
using SharedKernel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orders.Application;

/// <summary>ข้อมูลที่ client ขอซื้อได้ โดยไม่มีราคา ยอดรวม หรือสกุลเงินจาก client.</summary>
public sealed record OrderItemRequest(string ProductReference, int Quantity, string? Metadata = null);

/// <summary>ราคาที่ server policy ยืนยันแล้วก่อนส่งให้ Order aggregate ตรวจซ้ำ.</summary>
public sealed record TrustedOrderPricing(
    string Currency,
    IReadOnlyList<TrustedOrderLineInput> Lines,
    Money OrderDiscountAmount,
    Money OrderChargeAmount);

public interface ITrustedOrderPricingSource
{
    Task<TrustedOrderPricing> PriceAsync(
        Guid merchantId,
        string businessType,
        IReadOnlyList<OrderItemRequest> requestedItems,
        CancellationToken cancellationToken);
}

/// <summary>Fail-closed default until the Product/catalog adapter is wired by the host.</summary>
public sealed class UnconfiguredTrustedOrderPricingSource : ITrustedOrderPricingSource
{
    public Task<TrustedOrderPricing> PriceAsync(
        Guid merchantId,
        string businessType,
        IReadOnlyList<OrderItemRequest> requestedItems,
        CancellationToken cancellationToken) =>
        throw new DependencyUnavailableException(
            "Trusted Order pricing source is not configured.",
            new InvalidOperationException("No trusted pricing adapter is registered."));
}

public sealed record PaymentLinkView(
    Guid LinkId,
    Guid OrderId,
    PaymentLinkStatus Status,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? RevokedAt,
    long Version);

public sealed record OrderItemView(
    string ProductCode,
    string VariantCode,
    string? VariantName,
    int Quantity,
    Money UnitPrice,
    Money DiscountAmount,
    Money TaxAmount,
    Money LineAmount);

public sealed record OrderView(
    Guid OrderId,
    Guid MerchantId,
    Guid? CreatedByAccountId,
    string OrderNo,
    string BusinessType,
    OrderStatus OrderStatus,
    PaymentStatus PaymentStatus,
    Money SubtotalAmount,
    Money OrderDiscountAmount,
    Money OrderChargeAmount,
    Money TotalAmount,
    Guid? OwnerSaleId,
    Guid? OwnerBranchIdAtCreation,
    bool IsFrozen,
    long Version,
    IReadOnlyList<OrderItemView> Items,
    PaymentLinkView? ActivePaymentLink);

public sealed record OrderCommandResult(
    OrderView Order,
    PaymentLinkView? PaymentLink,
    string? RawToken,
    bool Replayed = false);

public sealed record CreateOrderCommand(
    Guid MerchantId,
    Guid CreatedByAccountId,
    string BusinessType,
    IReadOnlyList<OrderItemRequest> Items,
    OrderOwnerRequest Owner,
    bool IssueNow,
    string IdempotencyKey,
    CustomerContact? Customer = null,
    string? NotificationRecipient = null)
    : ICommand<OrderCommandResult>, IMerchantScoped;

public sealed record IssueOrderCommand(
    Guid MerchantId,
    Guid OrderId,
    long ExpectedVersion,
    string IdempotencyKey)
    : ICommand<OrderCommandResult>, IMerchantScoped;

public sealed record PatchDraftOrderCommand(
    Guid MerchantId,
    Guid OrderId,
    Guid AccountId,
    string BusinessType,
    IReadOnlyList<OrderItemRequest> Items,
    OrderOwnerRequest Owner,
    long ExpectedVersion)
    : ICommand<OrderCommandResult>, IMerchantScoped;

public sealed record CancelManagedOrderCommand(
    Guid MerchantId,
    Guid OrderId,
    long ExpectedVersion,
    string Reason,
    string IdempotencyKey)
    : ICommand<OrderCommandResult>, IMerchantScoped;

public sealed record RotatePaymentLinkCommand(
    Guid MerchantId,
    Guid OrderId,
    long ExpectedVersion,
    string IdempotencyKey)
    : ICommand<OrderCommandResult>, IMerchantScoped;

public sealed record RevokePaymentLinkCommand(
    Guid MerchantId,
    Guid LinkId,
    string IdempotencyKey)
    : ICommand<PaymentLinkView>, IMerchantScoped;

/// <summary>Pure owner rule shared by the command boundary and tests.</summary>
public static class OrderOwnerPolicy
{
    public static ResolvedOrderOwner Resolve(
        OrderActorKind actorKind,
        Guid merchantId,
        Guid accountId,
        Guid? derivedSaleId,
        Guid? derivedBranchId,
        OrderOwnerRequest requested)
    {
        if (merchantId == Guid.Empty || accountId == Guid.Empty)
            throw new AccessDeniedException("A verified merchant account is required.", "owner_context_missing");

        return actorKind switch
        {
            OrderActorKind.Agent when derivedSaleId is { } saleId =>
                new ResolvedOrderOwner(saleId, derivedBranchId),
            OrderActorKind.Agent => throw new AccessDeniedException(
                "The agent has no resolved Sale owner.", "owner_sale_missing"),
            OrderActorKind.Employee or OrderActorKind.System or OrderActorKind.Merchant =>
                new ResolvedOrderOwner(requested.OwnerSaleId, requested.OwnerBranchId),
            _ => throw new AccessDeniedException("The account type cannot create an Order.", "owner_context_denied"),
        };
    }
}

public enum OrderActorKind
{
    Agent = 1,
    Employee = 2,
    System = 3,
    Merchant = 4,
}

public static class OrderViewMapper
{
    public static OrderView ToView(Order order, PaymentLink? activeLink = null) => new(
        order.Id,
        order.MerchantId,
        order.CreatedByAccountId,
        order.OrderNo,
        order.BusinessType ?? string.Empty,
        order.Status,
        order.PaymentStatus,
        order.SubtotalAmount,
        order.OrderDiscountAmount,
        order.OrderChargeAmount,
        order.TotalAmount,
        order.OwnerSaleId,
        order.OwnerBranchIdAtCreation,
        order.IsFrozen,
        order.Version,
        order.Items.Select(item => new OrderItemView(
            item.ProductCode,
            item.VariantCode,
            item.VariantName,
            item.Quantity,
            item.UnitPrice,
            item.Discount,
            item.TaxAmount,
            item.LineAmount)).ToList(),
        activeLink is null ? null : ToView(activeLink));

    public static PaymentLinkView ToView(PaymentLink link) => new(
        link.Id, link.OrderId, link.Status, link.CreatedAt, link.ExpiresAt, link.RevokedAt, link.Version);
}

internal static class OrderCommandGuards
{
    public static void RequireIdempotencyKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 200 || value.Any(char.IsControl))
            throw new InvalidRequestException("Idempotency-Key is invalid.", "invalid_idempotency_key");
    }

    public static void RequireReason(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 1000)
            throw new InvalidRequestException("Cancel reason is invalid.", "invalid_reason");
    }

    public static async Task RequireFirstDeliveryAsync(
        IIdempotencyStore idempotency,
        Guid merchantId,
        string key,
        string operation,
        CancellationToken cancellationToken)
    {
        RequireIdempotencyKey(key);
        // Namespace the client-supplied key by merchant and operation before it reaches the claim
        // store, whose primary key is the key column alone. Without this a raw key collides across
        // merchants (one merchant burns another's key) and across operations (order.issue vs
        // order.cancel), returning a false 409 replay. Matches CheckoutTransactionService.ConfirmKey.
        var scopedKey = $"{merchantId:D}:{operation}:{key.Trim()}";
        if (!await idempotency.TryBeginAsync([scopedKey], operation, cancellationToken).ConfigureAwait(false))
            throw new ConflictException("The idempotent request was already processed.", "idempotency_replay");
    }
}

internal static class OrderReplayHashing
{
    public static byte[] Hash<T>(T value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    public static byte[] ForIssue(IssueOrderCommand command) => Hash(new
    {
        command.MerchantId,
        command.OrderId,
        command.ExpectedVersion,
    });

    public static byte[] ForRotate(RotatePaymentLinkCommand command) => Hash(new
    {
        command.MerchantId,
        command.OrderId,
        command.ExpectedVersion,
    });

    public static byte[] ForCreate(CreateOrderCommand command) => Hash(command);
}

public sealed class OrderLinkIssuer(
    IPaymentLinkTokenService tokens,
    IPaymentLinkStore links,
    IClock clock)
{
    public (PaymentLink Link, string RawToken) Issue(Order order, Guid? rotatedFrom = null)
    {
        var minted = tokens.Mint();
        var now = clock.UtcNow;
        var link = PaymentLink.Create(
            order.Id,
            order.MerchantId,
            minted.Hash,
            now,
            now + TimeSpan.FromHours(72),
            rotatedFrom);
        links.Add(link);
        return (link, minted.RawToken);
    }
}

public sealed class PaymentLinkReplayService(
    IPaymentLinkReplayStore replays,
    IPaymentLinkStore links,
    IOrderWorkflowStore orders,
    IPaymentLinkReplayProtector protector,
    IClock clock)
{
    public static readonly TimeSpan ReplayTtl = TimeSpan.FromMinutes(30);

    public async Task<OrderCommandResult?> TryReplayAsync(
        Guid merchantId,
        string operation,
        string idempotencyKey,
        ReadOnlyMemory<byte> requestHash,
        CancellationToken cancellationToken)
    {
        var replay = await replays.FindReplayAsync(merchantId, operation, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        if (replay is null)
            return null;
        if (replay.IsExpiredAt(clock.UtcNow)
            || replay.ProtectedRawToken is not { Length: > 0 })
            throw new ConflictException(
                "The protected idempotent result has expired; issue a new link.",
                "idempotent_secret_expired");
        if (!replay.Matches(requestHash.Span))
            throw new ConflictException(
                "The idempotency key was reused with a different intent.",
                "idempotency_conflict");
        if (replay.LinkId is not { } linkId)
            throw new ConflictException(
                "The idempotent result has no payment link.", "idempotent_secret_expired");

        var order = await orders.GetAsync(merchantId, replay.OrderId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Order was not found.");
        var link = await links.GetLinkAsync(merchantId, linkId, cancellationToken).ConfigureAwait(false);
        if (link is null || !link.IsActiveAt(clock.UtcNow))
            throw new ConflictException(
                "The idempotent payment link is no longer active.", "idempotent_secret_expired");
        var rawToken = protector.Unprotect(replay.ProtectedRawToken);
        if (rawToken is null)
            throw new ConflictException(
                "The protected idempotent result is unavailable; issue a new link.",
                "idempotent_secret_expired");
        return new OrderCommandResult(
            OrderViewMapper.ToView(order, link),
            OrderViewMapper.ToView(link),
            rawToken,
            Replayed: true);
    }

    public void Add(
        Guid merchantId,
        Guid orderId,
        Guid linkId,
        string operation,
        string idempotencyKey,
        ReadOnlyMemory<byte> requestHash,
        string rawToken,
        DateTime linkExpiresAt)
    {
        var now = clock.UtcNow;
        var expiresAt = now + ReplayTtl < linkExpiresAt ? now + ReplayTtl : linkExpiresAt;
        replays.Add(PaymentLinkReplay.Create(
            merchantId,
            orderId,
            linkId,
            operation,
            idempotencyKey,
            requestHash.Span,
            now,
            expiresAt,
            protector.Protect(rawToken, expiresAt)));
    }
}

public sealed class CreateOrderHandler : ICommandHandler<CreateOrderCommand, OrderCommandResult>
{
    private readonly ITrustedOrderPricingSource _pricing;
    private readonly IOrderOwnerResolver _owners;
    private readonly IOrderWorkflowStore _orders;
    private readonly IOrderNoSequence _numbers;
    private readonly OrderLinkIssuer _issuer;
    private readonly PaymentLinkReplayService _replays;
    private readonly IIdempotencyStore _idempotency;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public CreateOrderHandler(
        ITrustedOrderPricingSource pricing,
        IOrderOwnerResolver owners,
        IOrderWorkflowStore orders,
        IOrderNoSequence numbers,
        OrderLinkIssuer issuer,
        PaymentLinkReplayService replays,
        IIdempotencyStore idempotency,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _pricing = pricing;
        _owners = owners;
        _orders = orders;
        _numbers = numbers;
        _issuer = issuer;
        _replays = replays;
        _idempotency = idempotency;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<OrderCommandResult> Handle(
        CreateOrderCommand command,
        CancellationToken cancellationToken)
    {
        OrderCommandGuards.RequireIdempotencyKey(command.IdempotencyKey);
        if (command.MerchantId == Guid.Empty || command.CreatedByAccountId == Guid.Empty)
            throw new AccessDeniedException("A verified creator is required.", "creator_missing");
        if (command.Items is null || command.Items.Count == 0)
            throw new InvalidRequestException("At least one order item is required.", "items_required");

        var requestHash = OrderReplayHashing.ForCreate(command);
        if (command.IssueNow)
        {
            var replay = await _replays.TryReplayAsync(
                command.MerchantId, "order.create", command.IdempotencyKey.Trim(), requestHash,
                cancellationToken).ConfigureAwait(false);
            if (replay is not null)
                return replay;
        }

        var priced = await _pricing.PriceAsync(
            command.MerchantId, command.BusinessType, command.Items, cancellationToken).ConfigureAwait(false);

        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await OrderCommandGuards.RequireFirstDeliveryAsync(
                _idempotency, command.MerchantId, command.IdempotencyKey, "order.create", ct).ConfigureAwait(false);
            var owner = await _owners.ResolveAsync(
                command.MerchantId,
                command.CreatedByAccountId,
                command.Owner,
                ct).ConfigureAwait(false);
            var order = Order.CreateDraft(new OrderDraftInput(
                command.MerchantId,
                command.CreatedByAccountId,
                command.BusinessType,
                priced.Currency,
                priced.Lines,
                priced.OrderDiscountAmount,
                priced.OrderChargeAmount,
                owner.OwnerSaleId,
                owner.OwnerBranchId,
                _clock.UtcNow,
                await _numbers.NextAsync(ct).ConfigureAwait(false),
                command.Customer,
                command.NotificationRecipient));

            _orders.Add(order);
            PaymentLink? link = null;
            string? rawToken = null;
            if (command.IssueNow)
            {
                order.Issue(_clock.UtcNow);
                (link, rawToken) = _issuer.Issue(order);
                _replays.Add(
                    command.MerchantId,
                    order.Id,
                    link.Id,
                    "order.create",
                    command.IdempotencyKey.Trim(),
                    requestHash,
                    rawToken!,
                    link.ExpiresAt);
            }

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            return new OrderCommandResult(OrderViewMapper.ToView(order, link),
                link is null ? null : OrderViewMapper.ToView(link), rawToken);
        }, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class IssueOrderHandler : ICommandHandler<IssueOrderCommand, OrderCommandResult>
{
    private readonly IOrderWorkflowStore _orders;
    private readonly OrderLinkIssuer _issuer;
    private readonly PaymentLinkReplayService _replays;
    private readonly IIdempotencyStore _idempotency;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public IssueOrderHandler(
        IOrderWorkflowStore orders,
        OrderLinkIssuer issuer,
        PaymentLinkReplayService replays,
        IIdempotencyStore idempotency,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _orders = orders;
        _issuer = issuer;
        _replays = replays;
        _idempotency = idempotency;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<OrderCommandResult> Handle(
        IssueOrderCommand command,
        CancellationToken cancellationToken)
    {
        OrderCommandGuards.RequireIdempotencyKey(command.IdempotencyKey);
        var requestHash = OrderReplayHashing.ForIssue(command);
        var replay = await _replays.TryReplayAsync(
            command.MerchantId, "order.issue", command.IdempotencyKey.Trim(), requestHash,
            cancellationToken).ConfigureAwait(false);
        if (replay is not null)
            return replay;

        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await OrderCommandGuards.RequireFirstDeliveryAsync(
                _idempotency, command.MerchantId, command.IdempotencyKey, "order.issue", ct).ConfigureAwait(false);
            var order = await _orders.GetForUpdateAsync(command.MerchantId, command.OrderId, ct)
                .ConfigureAwait(false)
                ?? throw new NotFoundException("Order was not found.");
            if (order.Version != command.ExpectedVersion)
                throw new ConcurrencyConflictException("Order changed after it was read.");
            if (order.Status == OrderStatus.Open)
                throw new ConflictException("The order is already issued.", "order_already_issued");
            if (order.Status != OrderStatus.Draft)
                throw new ConflictException("Only a draft order can be issued.", "order_state_conflict");

            order.Issue(_clock.UtcNow);
            var (link, rawToken) = _issuer.Issue(order);
            _replays.Add(
                command.MerchantId,
                order.Id,
                link.Id,
                "order.issue",
                command.IdempotencyKey.Trim(),
                requestHash,
                rawToken,
                link.ExpiresAt);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            return new OrderCommandResult(OrderViewMapper.ToView(order, link),
                OrderViewMapper.ToView(link), rawToken);
        }, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class RotatePaymentLinkHandler
    : ICommandHandler<RotatePaymentLinkCommand, OrderCommandResult>
{
    private readonly IOrderWorkflowStore _orders;
    private readonly IPaymentLinkStore _links;
    private readonly OrderLinkIssuer _issuer;
    private readonly PaymentLinkReplayService _replays;
    private readonly IIdempotencyStore _idempotency;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public RotatePaymentLinkHandler(
        IOrderWorkflowStore orders,
        IPaymentLinkStore links,
        OrderLinkIssuer issuer,
        PaymentLinkReplayService replays,
        IIdempotencyStore idempotency,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _orders = orders;
        _links = links;
        _issuer = issuer;
        _replays = replays;
        _idempotency = idempotency;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<OrderCommandResult> Handle(
        RotatePaymentLinkCommand command,
        CancellationToken cancellationToken)
    {
        OrderCommandGuards.RequireIdempotencyKey(command.IdempotencyKey);
        var requestHash = OrderReplayHashing.ForRotate(command);
        var replay = await _replays.TryReplayAsync(
            command.MerchantId, "payment-link.rotate", command.IdempotencyKey.Trim(), requestHash,
            cancellationToken).ConfigureAwait(false);
        if (replay is not null)
            return replay;

        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await OrderCommandGuards.RequireFirstDeliveryAsync(
                _idempotency, command.MerchantId, command.IdempotencyKey, "payment-link.rotate", ct).ConfigureAwait(false);
            var order = await _orders.GetForUpdateAsync(command.MerchantId, command.OrderId, ct)
                .ConfigureAwait(false)
                ?? throw new NotFoundException("Order was not found.");
            if (order.Version != command.ExpectedVersion)
                throw new ConcurrencyConflictException("Order changed after it was read.");
            if (order.Status != OrderStatus.Open || order.PaymentStatus == PaymentStatus.Paid)
                throw new ConflictException("Only an unpaid issued order can rotate a link.", "order_state_conflict");

            var active = await _links.GetActiveForOrderAsync(command.MerchantId, command.OrderId, ct)
                .ConfigureAwait(false)
                ?? throw new ConflictException("The order has no active payment link.", "payment_link_missing");
            active.Revoke(_clock.UtcNow);
            order.RegisterLinkRotation(_clock.UtcNow);
            var (link, rawToken) = _issuer.Issue(order, active.Id);
            _replays.Add(
                command.MerchantId,
                order.Id,
                link.Id,
                "payment-link.rotate",
                command.IdempotencyKey.Trim(),
                requestHash,
                rawToken,
                link.ExpiresAt);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            return new OrderCommandResult(
                OrderViewMapper.ToView(order, link), OrderViewMapper.ToView(link), rawToken);
        }, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class RevokePaymentLinkHandler
    : ICommandHandler<RevokePaymentLinkCommand, PaymentLinkView>
{
    private readonly IPaymentLinkStore _links;
    private readonly IOrderWorkflowStore _orders;
    private readonly IIdempotencyStore _idempotency;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public RevokePaymentLinkHandler(
        IPaymentLinkStore links,
        IOrderWorkflowStore orders,
        IIdempotencyStore idempotency,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _links = links;
        _orders = orders;
        _idempotency = idempotency;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<PaymentLinkView> Handle(
        RevokePaymentLinkCommand command,
        CancellationToken cancellationToken) =>
        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await OrderCommandGuards.RequireFirstDeliveryAsync(
                _idempotency, command.MerchantId, command.IdempotencyKey, "payment-link.revoke", ct).ConfigureAwait(false);
            var link = await _links.GetLinkAsync(command.MerchantId, command.LinkId, ct)
                .ConfigureAwait(false)
                ?? throw new NotFoundException("Payment link was not found.");
            var order = await _orders.GetForUpdateAsync(command.MerchantId, link.OrderId, ct)
                .ConfigureAwait(false)
                ?? throw new NotFoundException("Order was not found.");
            link.Revoke(_clock.UtcNow);
            order.RegisterLinkRotation(_clock.UtcNow);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            return OrderViewMapper.ToView(link);
        }, cancellationToken).ConfigureAwait(false);
}

public sealed class PatchDraftOrderHandler
    : ICommandHandler<PatchDraftOrderCommand, OrderCommandResult>
{
    private readonly ITrustedOrderPricingSource _pricing;
    private readonly IOrderOwnerResolver _owners;
    private readonly IOrderWorkflowStore _orders;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public PatchDraftOrderHandler(
        ITrustedOrderPricingSource pricing,
        IOrderOwnerResolver owners,
        IOrderWorkflowStore orders,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _pricing = pricing;
        _owners = owners;
        _orders = orders;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<OrderCommandResult> Handle(
        PatchDraftOrderCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ExpectedVersion <= 0)
            throw new InvalidRequestException("If-Match version is required.", "if_match_required");
        if (command.Items is null || command.Items.Count == 0)
            throw new InvalidRequestException("At least one order item is required.", "items_required");
        var priced = await _pricing.PriceAsync(
            command.MerchantId, command.BusinessType, command.Items, cancellationToken).ConfigureAwait(false);

        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var order = await _orders.GetForUpdateAsync(command.MerchantId, command.OrderId, ct)
                .ConfigureAwait(false)
                ?? throw new NotFoundException("Order was not found.");
            if (order.Version != command.ExpectedVersion)
                throw new ConcurrencyConflictException("Order changed after it was read.");
            if (order.Status != OrderStatus.Draft || order.IsFrozen)
                throw new ConflictException("Only a draft order can be patched.", "order_not_draft");
            var owner = await _owners.ResolveAsync(command.MerchantId, command.AccountId, command.Owner, ct)
                .ConfigureAwait(false);
            order.PatchDraft(new OrderDraftInput(
                command.MerchantId,
                command.AccountId,
                command.BusinessType,
                priced.Currency,
                priced.Lines,
                priced.OrderDiscountAmount,
                priced.OrderChargeAmount,
                owner.OwnerSaleId,
                owner.OwnerBranchId,
                _clock.UtcNow,
                order.OrderNo,
                order.Customer,
                order.NotificationRecipient),
                _clock.UtcNow);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            return new OrderCommandResult(OrderViewMapper.ToView(order), null, null);
        }, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class CancelManagedOrderHandler
    : ICommandHandler<CancelManagedOrderCommand, OrderCommandResult>
{
    private readonly IOrderWorkflowStore _orders;
    private readonly IPaymentLinkStore _links;
    private readonly IPaymentSessionProbe _blockingPayments;
    private readonly IIdempotencyStore _idempotency;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public CancelManagedOrderHandler(
        IOrderWorkflowStore orders,
        IPaymentLinkStore links,
        IPaymentSessionProbe blockingPayments,
        IIdempotencyStore idempotency,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _orders = orders;
        _links = links;
        _blockingPayments = blockingPayments;
        _idempotency = idempotency;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<OrderCommandResult> Handle(
        CancelManagedOrderCommand command,
        CancellationToken cancellationToken)
    {
        OrderCommandGuards.RequireReason(command.Reason);
        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await OrderCommandGuards.RequireFirstDeliveryAsync(
                _idempotency, command.MerchantId, command.IdempotencyKey, "order.cancel", ct).ConfigureAwait(false);
            var order = await _orders.GetForUpdateAsync(command.MerchantId, command.OrderId, ct)
                .ConfigureAwait(false)
                ?? throw new NotFoundException("Order was not found.");
            if (order.Status == OrderStatus.Cancelled)
                return new OrderCommandResult(OrderViewMapper.ToView(order), null, null);
            if (order.Version != command.ExpectedVersion)
                throw new ConcurrencyConflictException("Order changed after it was read.");
            if (order.PaymentStatus == PaymentStatus.Paid || order.Status is OrderStatus.Paid or OrderStatus.Refunded)
                throw new ConflictException("A paid order cannot be cancelled.", "order_already_paid");
            if (await _blockingPayments.HasBlockingSessionAsync(order.Id, ct).ConfigureAwait(false))
                throw new ConflictException(
                    "Payment verification is still pending.", "payment_pending_verification");

            order.Cancel(_clock.UtcNow);
            var active = await _links.GetActiveForOrderAsync(command.MerchantId, command.OrderId, ct)
                .ConfigureAwait(false);
            active?.Revoke(_clock.UtcNow);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            return new OrderCommandResult(OrderViewMapper.ToView(order),
                active is null ? null : OrderViewMapper.ToView(active), null);
        }, cancellationToken).ConfigureAwait(false);
    }
}
