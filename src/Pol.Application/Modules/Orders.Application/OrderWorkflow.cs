using BuildingBlocks.Application;
using Checkouts.Application;
using Checkouts.Domain;
using Contracts;
using Mediator;
using Orders.Domain;
using SharedKernel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orders.Application;

/// <summary>ข้อมูลที่ client ขอซื้อได้ โดยไม่มีราคา ยอดรวม หรือสกุลเงินจาก client.</summary>
public sealed record OrderItemRequest(
    string ProductReference,
    int Quantity,
    string? Metadata = null,
    OrderItemClientSnapshot? ClientSnapshot = null);

/// <summary>Presence-aware PATCH notification intent. Null on the command means the client omitted the
/// property and the existing persisted intent must be preserved.</summary>
public sealed record NotificationIntentPatch(bool Send, string? Email = null, string? PhoneNumber = null);

/// <summary>Shared canonical notification validation for create and PATCH. Stored recipients are trimmed and
/// bounded by the Order columns; send=false always clears both channels.</summary>
public static class NotificationIntentNormalizer
{
    public static NotificationIntentPatch Normalize(bool send, string? email, string? phoneNumber)
    {
        if (!send)
            return new NotificationIntentPatch(false);

        var normalizedEmail = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        if (normalizedEmail is not null
            && (normalizedEmail.Length > 320
                || !System.Net.Mail.MailAddress.TryCreate(normalizedEmail, out var parsed)
                || parsed.Address != normalizedEmail))
            throw new InvalidRequestException("Notification email is invalid.", "validation_failed");

        var normalizedPhone = string.IsNullOrWhiteSpace(phoneNumber) ? null : phoneNumber.Trim();
        if (normalizedPhone is not null)
        {
            var digits = normalizedPhone.Count(char.IsAsciiDigit);
            if (normalizedPhone.Length > 32
                || digits is < 8 or > 15
                || normalizedPhone.Any(c => !char.IsAsciiDigit(c) && c is not ('+' or '-' or ' ')))
                throw new InvalidRequestException("Notification phone number is invalid.", "validation_failed");
        }

        if (normalizedEmail is null && normalizedPhone is null)
            throw new InvalidRequestException(
                "A notification recipient is required when send is true.", "notification_recipient_required");
        return new NotificationIntentPatch(true, normalizedEmail, normalizedPhone);
    }
}

/// <summary>Optional canonical HTTP facts that must match the trusted pricing result before any write.</summary>
public sealed record OrderItemClientSnapshot(
    string ProductCode,
    string ProductName,
    string UnitPrice,
    string DiscountAmount,
    string TaxAmount,
    string LineAmount);

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

/// <summary>Trusted source policy input. Owner and source context are resolved server-side before pricing.</summary>
public sealed record TrustedOrderSourceContext(
    Guid MerchantId,
    string BusinessType,
    IReadOnlyList<OrderItemRequest> RequestedItems,
    ResolvedOrderOwner Owner);

/// <summary>
/// Optional production source-policy seam. Generic application fakes keep using
/// <see cref="ITrustedOrderPricingSource"/>; the production adapter additionally receives the resolved owner.
/// </summary>
public interface IOrderSourcePolicy
{
    Task<TrustedOrderPricing> PriceAsync(
        TrustedOrderSourceContext context,
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
    Money LineAmount,
    VersionedMetadata? RequestMetadata = null);

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
    PaymentLinkView? ActivePaymentLink,
    VersionedMetadata? Metadata = null);

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
    string? NotificationRecipient = null,
    bool NotifyOnIssue = false,
    string? NotificationEmail = null,
    string? NotificationPhoneNumber = null,
    string? Currency = null,
    string? OrderDiscountAmount = null,
    string? OrderChargeAmount = null,
    JsonElement? Metadata = null,
    CommerceAuthorizationProof? Authorization = null)
    : ICommand<OrderCommandResult>, IMerchantScoped;

public sealed record IssueOrderCommand(
    Guid MerchantId,
    Guid OrderId,
    long ExpectedVersion,
    string IdempotencyKey,
    CommerceAuthorizationProof? Authorization = null)
    : ICommand<OrderCommandResult>, IMerchantScoped;

public sealed record PatchDraftOrderCommand(
    Guid MerchantId,
    Guid OrderId,
    Guid AccountId,
    string? BusinessType,
    IReadOnlyList<OrderItemRequest>? Items,
    OrderOwnerRequest? Owner,
    long ExpectedVersion,
    CommerceAuthorizationProof? Authorization = null,
    string? OrderDiscountAmount = null,
    string? OrderChargeAmount = null,
    JsonElement? Metadata = null,
    NotificationIntentPatch? NotificationIntent = null)
    : ICommand<OrderCommandResult>, IMerchantScoped;

public sealed record CancelManagedOrderCommand(
    Guid MerchantId,
    Guid OrderId,
    long ExpectedVersion,
    string Reason,
    string IdempotencyKey,
    CommerceAuthorizationProof? Authorization = null)
    : ICommand<OrderCommandResult>, IMerchantScoped;

public sealed record RotatePaymentLinkCommand(
    Guid MerchantId,
    Guid OrderId,
    long ExpectedVersion,
    string IdempotencyKey,
    CommerceAuthorizationProof? Authorization = null,
    bool SendNotification = false)
    : ICommand<OrderCommandResult>, IMerchantScoped;

public sealed record RevokePaymentLinkCommand(
    Guid MerchantId,
    Guid LinkId,
    string IdempotencyKey,
    CommerceAuthorizationProof? Authorization = null)
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
            item.LineAmount,
            VersionedMetadata.Parse(item.RequestMetadata))).ToList(),
        activeLink is null ? null : ToView(activeLink),
        VersionedMetadata.Parse(order.Metadata));

    public static PaymentLinkView ToView(PaymentLink link) => new(
        link.Id, link.OrderId, link.Status, link.CreatedAt, link.ExpiresAt, link.RevokedAt, link.Version);
}

internal static class OrderCommandGuards
{
    public static VersionedMetadata? ParseMetadata(JsonElement? element)
    {
        if (element is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return ParseMetadata(value.GetRawText());
    }

    public static VersionedMetadata? ParseMetadata(string? json)
    {
        try
        {
            return VersionedMetadata.Parse(json);
        }
        catch (ArgumentException)
        {
            throw new InvalidRequestException(
                "Metadata must use the supported VersionedMetadata envelope.", "metadata_invalid");
        }
    }

    public static void ValidateRequestedAdjustment(string? requested, Money trusted, string name)
    {
        if (requested is null)
            return;
        if (!decimal.TryParse(requested, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var amount)
            || amount != trusted.Amount)
            throw new ConflictException($"{name} does not match trusted pricing.", "pricing_mismatch");
    }

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
        command.SendNotification,
    });

    public static byte[] ForCreate(CreateOrderCommand command, ResolvedOrderOwner effectiveOwner) => Hash(new
    {
        command.MerchantId,
        command.CreatedByAccountId,
        BusinessType = CanonicalText(command.BusinessType),
        Items = command.Items.Select(item => new
        {
            ProductReference = CanonicalText(item.ProductReference),
            item.Quantity,
            Metadata = CanonicalMetadata(item.Metadata),
            ClientSnapshot = item.ClientSnapshot is null
                ? null
                : new
                {
                    ProductCode = CanonicalText(item.ClientSnapshot.ProductCode),
                    ProductName = CanonicalText(item.ClientSnapshot.ProductName),
                    UnitPrice = CanonicalText(item.ClientSnapshot.UnitPrice),
                    DiscountAmount = CanonicalText(item.ClientSnapshot.DiscountAmount),
                    TaxAmount = CanonicalText(item.ClientSnapshot.TaxAmount),
                    LineAmount = CanonicalText(item.ClientSnapshot.LineAmount),
                },
        }).ToArray(),
        RequestedOwner = new
        {
            command.Owner.OwnerSaleId,
            command.Owner.OwnerBranchId,
        },
        EffectiveOwner = new
        {
            effectiveOwner.OwnerSaleId,
            effectiveOwner.OwnerBranchId,
        },
        Currency = CanonicalText(command.Currency),
        command.IssueNow,
        OrderDiscountAmount = CanonicalText(command.OrderDiscountAmount),
        OrderChargeAmount = CanonicalText(command.OrderChargeAmount),
        Notification = new
        {
            command.NotifyOnIssue,
            Recipient = CanonicalText(command.NotificationRecipient),
            Email = CanonicalText(command.NotificationEmail),
            PhoneNumber = CanonicalText(command.NotificationPhoneNumber),
        },
        Customer = command.Customer is null
            ? null
            : new
            {
                Name = CanonicalText(command.Customer.Name),
                Phone = CanonicalText(command.Customer.Phone),
                Email = CanonicalText(command.Customer.Email),
            },
        Metadata = CanonicalMetadata(command.Metadata),
    });

    private static string? CanonicalText(string? value) =>
        value is null ? null : value.Trim();

    private static object CanonicalMetadata(string? value) => new
    {
        Present = value is not null,
        Value = VersionedMetadata.Parse(value)?.ToCanonicalJson(),
    };

    private static object CanonicalMetadata(JsonElement? value) => new
    {
        Present = value.HasValue,
        Value = value is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : VersionedMetadata.Parse(element.GetRawText())?.ToCanonicalJson(),
    };
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
    private static readonly JsonSerializerOptions ReplayJson = BuildReplayJson();

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
        if (replay.IsExpiredAt(clock.UtcNow))
            throw new ConflictException(
                "The protected idempotent result has expired; issue a new link.",
                "idempotent_secret_expired");
        if (!replay.Matches(requestHash.Span))
            throw new ConflictException(
                "The idempotency key was reused with a different intent.",
                "idempotency_conflict");
        if (replay.LinkId is not { } linkId)
        {
            if (replay.ProtectedRawToken is not { Length: > 0 })
                throw new ConflictException(
                    "The protected idempotent result is unavailable; issue a new order.",
                    "idempotent_secret_expired");
            var protectedDraft = protector.Unprotect(replay.ProtectedRawToken);
            var draft = protectedDraft is null
                ? null
                : JsonSerializer.Deserialize<OrderCommandResult>(protectedDraft, ReplayJson);
            return draft is null
                ? throw new ConflictException(
                    "The protected idempotent result is unavailable; issue a new order.",
                    "idempotent_secret_expired")
                : draft with { Replayed = true };
        }
        if (replay.ProtectedRawToken is not { Length: > 0 })
            throw new ConflictException(
                "The protected idempotent result has expired; issue a new link.",
                "idempotent_secret_expired");
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

    public void AddDraft(
        Guid merchantId,
        Guid orderId,
        string operation,
        string idempotencyKey,
        ReadOnlyMemory<byte> requestHash,
        OrderCommandResult result)
    {
        var now = clock.UtcNow;
        replays.Add(PaymentLinkReplay.Create(
            merchantId,
            orderId,
            linkId: null,
            operation,
            idempotencyKey,
            requestHash.Span,
            now,
            now + ReplayTtl,
            protector.Protect(JsonSerializer.Serialize(result, ReplayJson), now + ReplayTtl)));
    }

    private static JsonSerializerOptions BuildReplayJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new MoneyJsonConverter());
        return options;
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
    private readonly ICommerceAuthorizationLease _authorizationLease;
    private readonly IOutbox? _outbox;
    private readonly IPaymentLinkNotificationProtector? _notificationProtector;

    public CreateOrderHandler(
        ITrustedOrderPricingSource pricing,
        IOrderOwnerResolver owners,
        IOrderWorkflowStore orders,
        IOrderNoSequence numbers,
        OrderLinkIssuer issuer,
        PaymentLinkReplayService replays,
        IIdempotencyStore idempotency,
        IUnitOfWork unitOfWork,
        IClock clock,
        ICommerceAuthorizationLease? authorizationLease = null,
        IOutbox? outbox = null,
        IPaymentLinkNotificationProtector? notificationProtector = null)
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
        _authorizationLease = authorizationLease ?? new NoopCommerceAuthorizationLease();
        _outbox = outbox;
        _notificationProtector = notificationProtector;
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
        var notification = NotificationIntentNormalizer.Normalize(
            command.NotifyOnIssue, command.NotificationEmail, command.NotificationPhoneNumber);
        command = command with
        {
            NotificationRecipient = notification.PhoneNumber ?? notification.Email,
            NotificationEmail = notification.Email,
            NotificationPhoneNumber = notification.PhoneNumber,
        };
        var orderMetadata = OrderCommandGuards.ParseMetadata(command.Metadata);
        var requestedMetadata = command.Items
            .Select(item => OrderCommandGuards.ParseMetadata(item.Metadata))
            .ToArray();
        ResolvedOrderOwner? trustedOwner = null;
        TrustedOrderPricing priced;
        if (_pricing is IOrderSourcePolicy sourcePolicy)
        {
            trustedOwner = await _owners.ResolveAsync(
                command.MerchantId, command.CreatedByAccountId, command.Owner, cancellationToken)
                .ConfigureAwait(false);
            priced = await sourcePolicy.PriceAsync(
                new TrustedOrderSourceContext(command.MerchantId, command.BusinessType, command.Items, trustedOwner),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            trustedOwner = await _owners.ResolveAsync(
                command.MerchantId, command.CreatedByAccountId, command.Owner, cancellationToken)
                .ConfigureAwait(false);
            priced = await _pricing.PriceAsync(
                command.MerchantId, command.BusinessType, command.Items, cancellationToken).ConfigureAwait(false);
        }
        priced = AttachRequestMetadata(priced, requestedMetadata);
        if (!string.IsNullOrWhiteSpace(command.Currency)
            && !string.Equals(priced.Currency, command.Currency.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidRequestException("Requested currency does not match trusted pricing.", "currency_mismatch");
        ValidateClientSnapshots(command.Items, priced);
        OrderCommandGuards.ValidateRequestedAdjustment(command.OrderDiscountAmount, priced.OrderDiscountAmount, "orderDiscountAmount");
        OrderCommandGuards.ValidateRequestedAdjustment(command.OrderChargeAmount, priced.OrderChargeAmount, "orderChargeAmount");
        var requestHash = OrderReplayHashing.ForCreate(command, trustedOwner!);

        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await _authorizationLease.VerifyAsync(command.Authorization, ct).ConfigureAwait(false);
            var replay = await _replays.TryReplayAsync(
                command.MerchantId, "order.create", command.IdempotencyKey.Trim(), requestHash,
                ct).ConfigureAwait(false);
            if (replay is not null)
                return replay;

            await OrderCommandGuards.RequireFirstDeliveryAsync(
                _idempotency, command.MerchantId, command.IdempotencyKey, "order.create", ct).ConfigureAwait(false);
            var owner = trustedOwner!;
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
                command.NotificationRecipient,
                orderMetadata,
                notification.Send,
                notification.Email,
                notification.PhoneNumber));

            _orders.Add(order);
            PaymentLink? link = null;
            string? rawToken = null;
            OrderCommandResult response;
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
                response = new OrderCommandResult(OrderViewMapper.ToView(order, link),
                    OrderViewMapper.ToView(link), rawToken);
                EnqueueNotification(order, link, rawToken!, ct);
            }
            else
            {
                response = new OrderCommandResult(OrderViewMapper.ToView(order), null, null);
                _replays.AddDraft(
                    command.MerchantId,
                    order.Id,
                    "order.create",
                    command.IdempotencyKey.Trim(),
                    requestHash,
                    response);
            }

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            return response;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateClientSnapshots(
        IReadOnlyList<OrderItemRequest> requestedItems, TrustedOrderPricing priced)
    {
        if (requestedItems.All(item => item.ClientSnapshot is null))
            return;
        if (requestedItems.Count != priced.Lines.Count)
            throw new ConflictException("Trusted pricing did not return all requested lines.", "pricing_mismatch");

        for (var i = 0; i < requestedItems.Count; i++)
        {
            var snapshot = requestedItems[i].ClientSnapshot;
            if (snapshot is null)
                throw new ConflictException("Every order line must include a complete trusted snapshot.", "pricing_mismatch");
            var trusted = priced.Lines[i];
            if (!string.Equals(snapshot.ProductCode, trusted.ProductCode, StringComparison.Ordinal)
                || !string.Equals(snapshot.ProductName, trusted.VariantName, StringComparison.Ordinal)
                || !MoneyTextMatches(snapshot.UnitPrice, trusted.UnitPrice)
                || !MoneyTextMatches(snapshot.DiscountAmount, trusted.DiscountAmount)
                || !MoneyTextMatches(snapshot.TaxAmount, trusted.TaxAmount)
                || !MoneyTextMatches(snapshot.LineAmount, trusted.LineAmount))
                throw new ConflictException("Order line does not match trusted pricing.", "pricing_mismatch");
        }
    }

    private static TrustedOrderPricing AttachRequestMetadata(
        TrustedOrderPricing priced,
        IReadOnlyList<VersionedMetadata?> metadata)
    {
        if (priced.Lines.Count != metadata.Count)
            throw new ConflictException("Trusted pricing did not return all requested lines.", "pricing_mismatch");
        return priced with
        {
            Lines = priced.Lines.Select((line, index) => line with
            {
                RequestMetadata = metadata[index],
            }).ToArray(),
        };
    }

    private void EnqueueNotification(Order order, PaymentLink link, string rawToken, CancellationToken cancellationToken)
    {
        if (!order.NotifyOnIssue)
            return;
        if (string.IsNullOrWhiteSpace(order.NotificationEmail)
            && string.IsNullOrWhiteSpace(order.NotificationPhoneNumber))
            throw new ConflictException(
                "A notification recipient is required when notification intent is enabled.",
                "notification_recipient_required");
        if (_outbox is null || _notificationProtector is null)
            throw new DependencyUnavailableException(
                "Payment-link notification delivery is not configured.",
                new InvalidOperationException("Payment-link notification ports are not registered."));
        _outbox.Enqueue(new PaymentLinkNotificationRequestedV1(
            link.Id,
            order.MerchantId,
            order.Id,
            link.Id,
            order.NotificationEmail,
            order.NotificationPhoneNumber,
            _notificationProtector.Protect(rawToken, link.ExpiresAt),
            _clock.UtcNow));
    }

    private static bool MoneyTextMatches(string text, Money trusted) =>
        decimal.TryParse(text, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var amount)
        && amount == trusted.Amount;

}

public sealed class IssueOrderHandler : ICommandHandler<IssueOrderCommand, OrderCommandResult>
{
    private readonly IOrderWorkflowStore _orders;
    private readonly OrderLinkIssuer _issuer;
    private readonly PaymentLinkReplayService _replays;
    private readonly IIdempotencyStore _idempotency;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly ICommerceAuthorizationLease _authorizationLease;
    private readonly IOutbox? _outbox;
    private readonly IPaymentLinkNotificationProtector? _notificationProtector;

    public IssueOrderHandler(
        IOrderWorkflowStore orders,
        OrderLinkIssuer issuer,
        PaymentLinkReplayService replays,
        IIdempotencyStore idempotency,
        IUnitOfWork unitOfWork,
        IClock clock,
        ICommerceAuthorizationLease? authorizationLease = null,
        IOutbox? outbox = null,
        IPaymentLinkNotificationProtector? notificationProtector = null)
    {
        _orders = orders;
        _issuer = issuer;
        _replays = replays;
        _idempotency = idempotency;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _authorizationLease = authorizationLease ?? new NoopCommerceAuthorizationLease();
        _outbox = outbox;
        _notificationProtector = notificationProtector;
    }

    public async ValueTask<OrderCommandResult> Handle(
        IssueOrderCommand command,
        CancellationToken cancellationToken)
    {
        OrderCommandGuards.RequireIdempotencyKey(command.IdempotencyKey);
        var requestHash = OrderReplayHashing.ForIssue(command);
        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await _authorizationLease.VerifyAsync(command.Authorization, ct).ConfigureAwait(false);
            var replay = await _replays.TryReplayAsync(
                command.MerchantId, "order.issue", command.IdempotencyKey.Trim(), requestHash,
                ct).ConfigureAwait(false);
            if (replay is not null)
                return replay;

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
            EnqueueNotification(order, link, rawToken, ct);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            return new OrderCommandResult(OrderViewMapper.ToView(order, link),
                OrderViewMapper.ToView(link), rawToken);
        }, cancellationToken).ConfigureAwait(false);
    }

    private void EnqueueNotification(Order order, PaymentLink link, string rawToken, CancellationToken cancellationToken)
    {
        if (!order.NotifyOnIssue)
            return;
        if (string.IsNullOrWhiteSpace(order.NotificationEmail)
            && string.IsNullOrWhiteSpace(order.NotificationPhoneNumber))
            throw new ConflictException(
                "A notification recipient is required when notification intent is enabled.",
                "notification_recipient_required");
        if (_outbox is null || _notificationProtector is null)
            throw new DependencyUnavailableException(
                "Payment-link notification delivery is not configured.",
                new InvalidOperationException("Payment-link notification ports are not registered."));
        _outbox.Enqueue(new PaymentLinkNotificationRequestedV1(
            link.Id,
            order.MerchantId,
            order.Id,
            link.Id,
            order.NotificationEmail,
            order.NotificationPhoneNumber,
            _notificationProtector.Protect(rawToken, link.ExpiresAt),
            _clock.UtcNow));
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
    private readonly ICommerceAuthorizationLease _authorizationLease;
    private readonly IOutbox? _outbox;
    private readonly IPaymentLinkNotificationProtector? _notificationProtector;

    public RotatePaymentLinkHandler(
        IOrderWorkflowStore orders,
        IPaymentLinkStore links,
        OrderLinkIssuer issuer,
        PaymentLinkReplayService replays,
        IIdempotencyStore idempotency,
        IUnitOfWork unitOfWork,
        IClock clock,
        ICommerceAuthorizationLease? authorizationLease = null,
        IOutbox? outbox = null,
        IPaymentLinkNotificationProtector? notificationProtector = null)
    {
        _orders = orders;
        _links = links;
        _issuer = issuer;
        _replays = replays;
        _idempotency = idempotency;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _authorizationLease = authorizationLease ?? new NoopCommerceAuthorizationLease();
        _outbox = outbox;
        _notificationProtector = notificationProtector;
    }

    public async ValueTask<OrderCommandResult> Handle(
        RotatePaymentLinkCommand command,
        CancellationToken cancellationToken)
    {
        OrderCommandGuards.RequireIdempotencyKey(command.IdempotencyKey);
        var requestHash = OrderReplayHashing.ForRotate(command);
        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await _authorizationLease.VerifyAsync(command.Authorization, ct).ConfigureAwait(false);
            var replay = await _replays.TryReplayAsync(
                command.MerchantId, "payment-link.rotate", command.IdempotencyKey.Trim(), requestHash,
                ct).ConfigureAwait(false);
            if (replay is not null)
                return replay;

            await OrderCommandGuards.RequireFirstDeliveryAsync(
                _idempotency, command.MerchantId, command.IdempotencyKey, "payment-link.rotate", ct).ConfigureAwait(false);
            var order = await _orders.GetForUpdateAsync(command.MerchantId, command.OrderId, ct)
                .ConfigureAwait(false)
                ?? throw new NotFoundException("Order was not found.");
            if (order.Version != command.ExpectedVersion)
                throw new ConcurrencyConflictException("Order changed after it was read.");
            if (order.Status != OrderStatus.Open || order.PaymentStatus == PaymentStatus.Paid)
                throw new ConflictException("Only an unpaid issued order can rotate a link.", "order_state_conflict");
            if (command.SendNotification
                && string.IsNullOrWhiteSpace(order.NotificationEmail)
                && string.IsNullOrWhiteSpace(order.NotificationPhoneNumber))
                throw new ConflictException(
                    "A notification recipient is required when notification intent is enabled.",
                    "notification_recipient_required");

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
            if (command.SendNotification)
                EnqueueNotification(order, link, rawToken, ct, force: true);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            return new OrderCommandResult(
                OrderViewMapper.ToView(order, link), OrderViewMapper.ToView(link), rawToken);
        }, cancellationToken).ConfigureAwait(false);
    }

    private void EnqueueNotification(
        Order order,
        PaymentLink link,
        string rawToken,
        CancellationToken cancellationToken,
        bool force = false)
    {
        if (!force && !order.NotifyOnIssue)
            return;
        if (string.IsNullOrWhiteSpace(order.NotificationEmail)
            && string.IsNullOrWhiteSpace(order.NotificationPhoneNumber))
            throw new ConflictException(
                "A notification recipient is required when notification intent is enabled.",
                "notification_recipient_required");
        if (_outbox is null || _notificationProtector is null)
            throw new DependencyUnavailableException(
                "Payment-link notification delivery is not configured.",
                new InvalidOperationException("Payment-link notification ports are not registered."));
        _outbox.Enqueue(new PaymentLinkNotificationRequestedV1(
            link.Id,
            order.MerchantId,
            order.Id,
            link.Id,
            order.NotificationEmail,
            order.NotificationPhoneNumber,
            _notificationProtector.Protect(rawToken, link.ExpiresAt),
            _clock.UtcNow));
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
    private readonly ICommerceAuthorizationLease _authorizationLease;

    public RevokePaymentLinkHandler(
        IPaymentLinkStore links,
        IOrderWorkflowStore orders,
        IIdempotencyStore idempotency,
        IUnitOfWork unitOfWork,
        IClock clock,
        ICommerceAuthorizationLease? authorizationLease = null)
    {
        _links = links;
        _orders = orders;
        _idempotency = idempotency;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _authorizationLease = authorizationLease ?? new NoopCommerceAuthorizationLease();
    }

    public async ValueTask<PaymentLinkView> Handle(
        RevokePaymentLinkCommand command,
        CancellationToken cancellationToken) =>
        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await _authorizationLease.VerifyAsync(command.Authorization, ct).ConfigureAwait(false);
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
    private readonly ICommerceAuthorizationLease _authorizationLease;

    public PatchDraftOrderHandler(
        ITrustedOrderPricingSource pricing,
        IOrderOwnerResolver owners,
        IOrderWorkflowStore orders,
        IUnitOfWork unitOfWork,
        IClock clock,
        ICommerceAuthorizationLease? authorizationLease = null)
    {
        _pricing = pricing;
        _owners = owners;
        _orders = orders;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _authorizationLease = authorizationLease ?? new NoopCommerceAuthorizationLease();
    }

    public async ValueTask<OrderCommandResult> Handle(
        PatchDraftOrderCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ExpectedVersion <= 0)
            throw new InvalidRequestException("If-Match version is required.", "if_match_required");
        var existing = await _orders.GetAsync(command.MerchantId, command.OrderId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("Order was not found.");
        var effectiveBusinessType = command.BusinessType is null
            ? existing.BusinessType
            : command.BusinessType.Trim();
        if (string.IsNullOrWhiteSpace(effectiveBusinessType))
            throw new InvalidRequestException("Business type is required.", "validation_failed");
        var effectiveItems = command.Items ?? existing.Items
            .Select(item => new OrderItemRequest(
                item.ProductCode,
                item.Quantity,
                item.RequestMetadata))
            .ToArray();
        if (effectiveItems.Count == 0)
            throw new InvalidRequestException("At least one order item is required.", "items_required");
        var requestedMetadata = effectiveItems
            .Select(item => OrderCommandGuards.ParseMetadata(item.Metadata))
            .ToArray();
        var requestedOrderMetadata = command.Metadata.HasValue
            ? OrderCommandGuards.ParseMetadata(command.Metadata)
            : null;
        var normalizedNotification = command.NotificationIntent is null
            ? null
            : NotificationIntentNormalizer.Normalize(
                command.NotificationIntent.Send,
                command.NotificationIntent.Email,
                command.NotificationIntent.PhoneNumber);
        var reprice = command.Items is not null
            || command.BusinessType is not null
            || command.Owner is not null
            || command.OrderDiscountAmount is not null
            || command.OrderChargeAmount is not null;
        var trustedOwner = command.Owner is null
            ? new ResolvedOrderOwner(existing.OwnerSaleId, existing.OwnerBranchIdAtCreation)
            : await _owners.ResolveAsync(
                command.MerchantId, command.AccountId, command.Owner, cancellationToken)
                .ConfigureAwait(false);
        TrustedOrderPricing priced;
        if (!reprice)
        {
            priced = PersistedPricing(existing);
        }
        else if (_pricing is IOrderSourcePolicy sourcePolicy)
        {
            priced = await sourcePolicy.PriceAsync(
                new TrustedOrderSourceContext(command.MerchantId, effectiveBusinessType, effectiveItems, trustedOwner),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            priced = await _pricing.PriceAsync(
                command.MerchantId, effectiveBusinessType, effectiveItems, cancellationToken).ConfigureAwait(false);
        }
        priced = AttachRequestMetadata(priced, requestedMetadata);
        OrderCommandGuards.ValidateRequestedAdjustment(command.OrderDiscountAmount, priced.OrderDiscountAmount, "orderDiscountAmount");
        OrderCommandGuards.ValidateRequestedAdjustment(command.OrderChargeAmount, priced.OrderChargeAmount, "orderChargeAmount");

        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await _authorizationLease.VerifyAsync(command.Authorization, ct).ConfigureAwait(false);
            var order = await _orders.GetForUpdateAsync(command.MerchantId, command.OrderId, ct)
                .ConfigureAwait(false)
                ?? throw new NotFoundException("Order was not found.");
            if (order.Version != command.ExpectedVersion)
                throw new ConcurrencyConflictException("Order changed after it was read.");
            if (order.Status != OrderStatus.Draft || order.IsFrozen)
                throw new ConflictException("Only a draft order can be patched.", "order_not_draft");
            var owner = trustedOwner!;
            var orderDiscount = command.OrderDiscountAmount is null
                ? order.OrderDiscountAmount
                : priced.OrderDiscountAmount;
            var orderCharge = command.OrderChargeAmount is null
                ? order.OrderChargeAmount
                : priced.OrderChargeAmount;
            var existingItems = order.Items.ToArray();
            var requestLines = priced.Lines.Select((line, index) =>
            {
                var existingItem = command.Items is null ? existingItems[index] : null;
                return line with
                {
                    ProductCode = existingItem?.ProductCode ?? line.ProductCode,
                    VariantCode = existingItem?.VariantCode ?? line.VariantCode,
                    VariantName = existingItem?.VariantName ?? line.VariantName,
                    Quantity = existingItem?.Quantity ?? line.Quantity,
                    Metadata = existingItem is null
                        ? line.Metadata
                        : existingItem.Metadata is null
                            ? null
                            : CommerceItemMetadataCodec.Parse(existingItem.Metadata),
                    RequestMetadata = requestedMetadata[index]
                        ?? PreserveRequestMetadata(effectiveItems[index].ProductReference, order),
                };
            }).ToArray();
            var orderMetadata = command.Metadata.HasValue
                ? requestedOrderMetadata
                : OrderCommandGuards.ParseMetadata(order.Metadata);
            var notification = normalizedNotification
                ?? new NotificationIntentPatch(
                    order.NotifyOnIssue, order.NotificationEmail, order.NotificationPhoneNumber);
            order.PatchDraft(new OrderDraftInput(
                command.MerchantId,
                order.CreatedByAccountId ?? command.AccountId,
                effectiveBusinessType,
                priced.Currency,
                requestLines,
                orderDiscount,
                orderCharge,
                owner.OwnerSaleId,
                owner.OwnerBranchId,
                _clock.UtcNow,
                order.OrderNo,
                order.Customer,
                order.NotificationRecipient,
                orderMetadata,
                notification.Send,
                notification.Email,
                notification.PhoneNumber,
                PreserveItemIdentity: command.Items is null),
                _clock.UtcNow);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            return new OrderCommandResult(OrderViewMapper.ToView(order), null, null);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static TrustedOrderPricing AttachRequestMetadata(
        TrustedOrderPricing priced,
        IReadOnlyList<VersionedMetadata?> metadata)
    {
        if (priced.Lines.Count != metadata.Count)
            throw new ConflictException("Trusted pricing did not return all requested lines.", "pricing_mismatch");
        return priced with
        {
            Lines = priced.Lines.Select((line, index) => line with
            {
                RequestMetadata = metadata[index],
            }).ToArray(),
        };
    }

    private static TrustedOrderPricing PersistedPricing(Order order) =>
        new(
            order.Amount.Currency,
            order.Items.Select(item => new TrustedOrderLineInput(
                item.ProductCode,
                item.VariantCode,
                item.VariantName,
                item.Quantity,
                item.UnitPrice,
                item.Discount,
                item.TaxAmount,
                item.LineAmount,
                "persisted",
                item.Metadata is null ? null : CommerceItemMetadataCodec.Parse(item.Metadata),
                OrderCommandGuards.ParseMetadata(item.RequestMetadata))).ToArray(),
            order.OrderDiscountAmount,
            order.OrderChargeAmount);

    private static VersionedMetadata? PreserveRequestMetadata(string productReference, Order order)
    {
        var matches = order.Items
            .Where(item => string.Equals(item.ProductCode, productReference, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length > 1)
            throw new ConflictException(
                "Request metadata cannot be matched uniquely after an item reorder.", "metadata_ambiguous");
        return matches.Length == 1
            ? OrderCommandGuards.ParseMetadata(matches[0].RequestMetadata)
            : null;
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
    private readonly ICommerceAuthorizationLease _authorizationLease;

    public CancelManagedOrderHandler(
        IOrderWorkflowStore orders,
        IPaymentLinkStore links,
        IPaymentSessionProbe blockingPayments,
        IIdempotencyStore idempotency,
        IUnitOfWork unitOfWork,
        IClock clock,
        ICommerceAuthorizationLease? authorizationLease = null)
    {
        _orders = orders;
        _links = links;
        _blockingPayments = blockingPayments;
        _idempotency = idempotency;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _authorizationLease = authorizationLease ?? new NoopCommerceAuthorizationLease();
    }

    public async ValueTask<OrderCommandResult> Handle(
        CancelManagedOrderCommand command,
        CancellationToken cancellationToken)
    {
        OrderCommandGuards.RequireReason(command.Reason);
        return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await _authorizationLease.VerifyAsync(command.Authorization, ct).ConfigureAwait(false);
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
