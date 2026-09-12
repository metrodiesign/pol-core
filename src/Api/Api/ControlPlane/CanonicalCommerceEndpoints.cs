using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Accounts.Application;
using Admins.Application;
using Api.Iam;
using BuildingBlocks.Application;
using Checkouts.Application;
using Iam.Domain.Permissions;
using Mediator;
using Orders.Application;
using Payments.Application.Capabilities;
using Platform.Application.Transactions;
using Payments.Domain;
using SharedKernel;
using VersionedMetadata = Orders.Domain.VersionedMetadata;

namespace Api.ControlPlane;

/// <summary>Canonical Task8 C1 commerce reads and support writes. Child resources resolve their parent Order
/// first, then reuse the existing owner repositories; no independent child authorization surface is created.</summary>
internal static class CanonicalCommerceEndpoints
{
    public static void MapCanonicalCommerceEndpoints(this RouteGroupBuilder api)
    {
        var routes = api.AddEndpointFilter(HandleKnownErrors);
        MapDraftOrder(routes);
        MapOrderChildren(routes);
        MapCheckoutMethods(routes);
        MapTransactions(routes);
    }

    private static void MapDraftOrder(RouteGroupBuilder api)
    {
        api.MapPatch("/orders/{orderId:guid}", async (
            Guid orderId,
            CanonicalPatchDraftOrderRequest body,
            HttpContext http,
            IActorContext requestActor,
            IAdminScope scope,
            IAdminOrderReader orders,
            IOrderRepository identityOrders,
            IIdentityAccessQuery identities,
            IActorScope actorScope,
            IAdminOperationExecutor operations,
            IMediator mediator,
            CancellationToken ct) =>
        {
            if (IdentityPermissionAuthorization.IsIdentityRequest(http))
            {
                var accountId = requestActor.UserId
                    ?? throw new AccessDeniedException("No verified Account identity is bound.", "account_context_missing");
                var existing = await identityOrders.GetAsync(orderId, ct)
                    ?? throw new NotFoundException("Order was not found.");
                var authorization = await identities.ResolveAuthorizationAsync(
                    accountId, requestActor.MerchantId, null, ct);
                if (authorization is null
                    || !AccessEvaluator.CanReadOrder(
                        authorization,
                        requestActor.MerchantId,
                        existing.OwnerSaleId,
                        existing.OwnerBranchIdAtCreation).Allowed)
                    throw new NotFoundException("Order was not found.");
                var requestedOwner = body.OwnerSaleId is null && body.OwnerBranchId is null
                    ? null
                    : new OrderOwnerRequest(body.OwnerSaleId, body.OwnerBranchId);
                var identityResult = await mediator.Send(new PatchDraftOrderCommand(
                    requestActor.MerchantId, orderId, accountId, body.BusinessType,
                    body.Items, requestedOwner,
                    VersionEtags.Require(http),
                    IdentityPermissionAuthorization.GetCommerceAuthorizationProof(http),
                    body.OrderDiscountAmount, body.OrderChargeAmount, body.Metadata,
                    body.NotificationIntent is null
                        ? null
                        : new NotificationIntentPatch(
                            body.NotificationIntent.Send,
                            body.NotificationIntent.Email,
                            body.NotificationIntent.PhoneNumber)), ct);
                VersionEtags.Set(http, identityResult.Order.Version);
                return Results.Ok(identityResult.Order);
            }
            var resource = await ResolveOrderAsync(orders, scope, orderId, ct);
            var expected = VersionEtags.Require(http);
            using var actorBinding = actorScope.Begin(resource.MerchantId);
            var suppliedKey = http.Request.Headers["Idempotency-Key"].FirstOrDefault();
            var operationKey = string.IsNullOrWhiteSpace(suppliedKey)
                ? $"order.patch:{orderId:D}:v{expected}"
                : suppliedKey.Trim();
            var requestedAdminOwner = body.OwnerSaleId is null && body.OwnerBranchId is null
                ? null
                : new OrderOwnerRequest(body.OwnerSaleId, body.OwnerBranchId);
            var request = new AdminOperationRequest(
                resource.MerchantId, scope.Current.AdminId, "order.patch",
                operationKey,
                SerializePatchIntent(resource.MerchantId, orderId, expected, body), 200);
            var result = await operations.ExecuteAsync(request,
                token => mediator.Send(new PatchDraftOrderCommand(
                    resource.MerchantId, orderId, scope.Current.AdminId, body.BusinessType,
                    body.Items, requestedAdminOwner, expected,
                    OrderDiscountAmount: body.OrderDiscountAmount, OrderChargeAmount: body.OrderChargeAmount,
                    Metadata: body.Metadata,
                    NotificationIntent: body.NotificationIntent is null
                        ? null
                        : new NotificationIntentPatch(
                            body.NotificationIntent.Send,
                            body.NotificationIntent.Email,
                            body.NotificationIntent.PhoneNumber)), token).AsTask(),
                value => value.Order.OrderId.ToString("D"), ct);
            VersionEtags.Set(http, result.Value.Order.Version);
            return Results.Ok(result.Value.Order);
        }).RequireAdminOrIdentityCsrf().RequireAuthorization(ConsoleSessionAuthentication.AdminOrIdentityOrderPolicyName)
            .RequireOrderIdentityPermission(Keys.PaymentCreate, "order.write")
            .WithMetadata(new IfMatchMutationMarker("200"))
            .WithTags("คำสั่งซื้อ").WithName("PatchCanonicalDraftOrder")
            .WithSummary("แก้ไข Draft Order")
            .WithDescription("ใช้ trusted repricing และ owner guard ของ OrderWorkflow; รับเฉพาะ draft และต้องส่ง If-Match")
            .Accepts<CanonicalPatchDraftOrderRequest>("application/json")
            .Produces<OrderView>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);
    }

    private static void MapOrderChildren(RouteGroupBuilder api)
    {
        api.MapGet("/orders/{orderId:guid}/items", async (
            Guid orderId,
            HttpContext http,
            IActorContext actor,
            IAdminScope scope,
            IAdminOrderReader orders,
            IActorScope actorScope,
            IIdentityAccessQuery identities,
            IOrderRepository repository,
            int page = 1,
            int limit = 25,
            CancellationToken ct = default) =>
        {
            ValidatePage(page, limit);
            if (IdentityPermissionAuthorization.IsIdentityRequest(http))
            {
                var accountId = actor.UserId
                    ?? throw new AccessDeniedException("No verified Account identity is bound.", "account_context_missing");
                var identityOrder = await repository.GetAsync(orderId, ct)
                    ?? throw new NotFoundException("Order was not found.");
                var authorization = await identities.ResolveAuthorizationAsync(
                    accountId, actor.MerchantId, null, ct);
                if (authorization is null
                    || !AccessEvaluator.CanReadOrder(
                        authorization, actor.MerchantId, identityOrder.OwnerSaleId, identityOrder.OwnerBranchIdAtCreation).Allowed)
                    throw new NotFoundException("Order was not found.");
                var identityItems = identityOrder.Items.Skip((page - 1) * limit).Take(limit)
                    .Select(x => new CanonicalOrderItemView(
                        x.Id, x.ProductCode, x.VariantCode, x.VariantName, x.Quantity,
                        x.UnitPrice, x.Discount, x.TaxAmount, x.LineAmount)).ToArray();
                return Results.Ok(new PagedResult<CanonicalOrderItemView>(
                    identityItems, page, limit, identityOrder.Items.Count));
            }
            var resource = await ResolveOrderAsync(orders, scope, orderId, ct);
            using var actorBinding = actorScope.Begin(resource.MerchantId);
            var order = await repository.GetAsync(orderId, ct)
                ?? throw new NotFoundException("Order was not found.");
            var items = order.Items.Skip((page - 1) * limit).Take(limit)
                .Select(x => new CanonicalOrderItemView(
                    x.Id, x.ProductCode, x.VariantCode, x.VariantName, x.Quantity,
                    x.UnitPrice, x.Discount, x.TaxAmount, x.LineAmount)).ToArray();
            return Results.Ok(new PagedResult<CanonicalOrderItemView>(items, page, limit, order.Items.Count));
        }).RequireAuthorization(ConsoleSessionAuthentication.AdminOrIdentityOrderPolicyName)
            .RequireOrderIdentityPermission(Keys.PaymentView, "order.read")
            .WithMetadata(new SfsQueryParamsMarker(100))
            .WithTags("คำสั่งซื้อ").WithName("ListCanonicalOrderItems")
            .WithSummary("รายการ Order items")
            .WithDescription("ตรวจสิทธิ์ผ่าน Order parent ก่อนอ่าน child และคืนเฉพาะข้อมูลสินค้า/ยอดที่ปลอดภัย")
            .Produces<PagedResult<CanonicalOrderItemView>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/orders/{orderId:guid}/history", async (
            Guid orderId,
            HttpContext http,
            IActorContext actor,
            IAdminScope scope,
            IAdminOrderReader orders,
            IActorScope actorScope,
            IIdentityAccessQuery identities,
            IOrderRepository repository,
            CancellationToken ct) =>
        {
            if (IdentityPermissionAuthorization.IsIdentityRequest(http))
            {
                var accountId = actor.UserId
                    ?? throw new AccessDeniedException("No verified Account identity is bound.", "account_context_missing");
                var identityOrder = await repository.GetAsync(orderId, ct)
                    ?? throw new NotFoundException("Order was not found.");
                var authorization = await identities.ResolveAuthorizationAsync(
                    accountId, actor.MerchantId, null, ct);
                if (authorization is null
                    || !AccessEvaluator.CanReadOrder(
                        authorization, actor.MerchantId, identityOrder.OwnerSaleId, identityOrder.OwnerBranchIdAtCreation).Allowed)
                    throw new NotFoundException("Order was not found.");
                var identityHistory = new List<CanonicalOrderHistoryView>
                {
                    new("created", identityOrder.CreatedAt),
                };
                if (identityOrder.UpdatedAt != identityOrder.CreatedAt)
                    identityHistory.Add(new(identityOrder.Status.ToString().ToLowerInvariant(), identityOrder.UpdatedAt));
                return Results.Ok(identityHistory);
            }
            var resource = await ResolveOrderAsync(orders, scope, orderId, ct);
            using var actorBinding = actorScope.Begin(resource.MerchantId);
            var order = await repository.GetAsync(orderId, ct)
                ?? throw new NotFoundException("Order was not found.");
            var history = new List<CanonicalOrderHistoryView>
            {
                new("created", order.CreatedAt),
            };
            if (order.UpdatedAt != order.CreatedAt)
                history.Add(new(order.Status.ToString().ToLowerInvariant(), order.UpdatedAt));
            return Results.Ok(history);
        }).RequireAuthorization(ConsoleSessionAuthentication.AdminOrIdentityOrderPolicyName)
            .RequireOrderIdentityPermission(Keys.PaymentView, "order.read")
            .WithTags("คำสั่งซื้อ").WithName("GetCanonicalOrderHistory")
            .WithSummary("ประวัติ Order")
            .WithDescription("อ่าน lifecycle history จาก Order parent โดยไม่เปิด provider payload หรือ secret")
            .Produces<IReadOnlyList<CanonicalOrderHistoryView>>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static void MapCheckoutMethods(RouteGroupBuilder api)
    {
        api.MapGet("/checkout/payment-methods", async (
            IActorContext actor,
            IEffectivePaymentCapabilityResolver resolver,
            CancellationToken ct) =>
        {
            if (!actor.HasActor || actor.UserId is null)
                return Results.NotFound();
            var methods = await resolver.ListMethodsAsync(
                new PaymentCapabilitySubject(actor.MerchantId, PaymentAudience.User, actor.UserId), ct);
            return Results.Ok(new CanonicalCheckoutPaymentMethodsResponse(
                methods.Select(x => x.Method).ToArray()));
        }).RequireAuthorization("merchant-user").RequirePermission(Keys.PaymentView)
            .WithTags("การชำระเงิน").WithName("ListCanonicalCheckoutPaymentMethods")
            .WithSummary("รายการ Payment methods ของ Checkout")
            .WithDescription("resolve capability จาก Merchant/User context โดยไม่เรียก PSP หรือ network")
            .Produces<CanonicalCheckoutPaymentMethodsResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static void MapTransactions(RouteGroupBuilder api)
    {
        api.MapGet("/transactions", async (
            IAdminScope scope,
            IAdminOrderReader orders,
            ITransactionRepository transactions,
            int page = 1,
            int limit = 25,
            string? status = null,
            Guid? merchantId = null,
            CancellationToken ct = default) =>
        {
            ValidatePage(page, limit);
            var selected = merchantId ?? (scope.Accessible.IsUnrestricted
                ? throw new InvalidRequestException("merchantId is required for unrestricted transaction reads.", "invalid_filter")
                : scope.Accessible.Merchants.SingleOrDefault());
            if (selected == Guid.Empty || !scope.Accessible.Allows(selected))
                throw new AccessDeniedException("Merchant is outside the current Admin scope.", "merchant_scope_forbidden");
            var result = await transactions.ListAsync(selected, page, limit, status, ct);
            return Results.Ok(new PagedResult<TransactionView>(
                result.Items.Select(TransactionViewMapper.ToView).ToArray(), result.Page, result.Limit, result.Total));
        }).RequireAuthorization("admin").RequirePermission(Keys.PaymentView)
            .WithMetadata(new SfsQueryParamsMarker(100))
            .WithTags("ธุรกรรม").WithName("ListCanonicalTransactions")
            .WithSummary("รายการ Transaction")
            .WithDescription("คืน Transaction ใน Merchant scope พร้อม pinned provider context และ safe snapshot")
            .Produces<PagedResult<TransactionView>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapGet("/transactions/{transactionId:guid}", async (
            Guid transactionId,
            HttpContext http,
            IAdminScope scope,
            IAdminOrderReader orders,
            ITransactionRepository transactions,
            Guid? merchantId,
            CancellationToken ct) =>
        {
            var transaction = await ResolveTransactionAsync(transactionId, merchantId, scope, orders, transactions, ct);
            if (merchantId is { } selected && selected != transaction.MerchantId)
                return Results.NotFound();
            VersionEtags.Set(http, transaction.Version);
            return Results.Ok(TransactionViewMapper.ToView(transaction));
        }).RequireAuthorization("admin").RequirePermission(Keys.PaymentView)
            .WithTags("ธุรกรรม").WithName("GetCanonicalTransaction")
            .WithSummary("อ่าน Transaction")
            .WithDescription("ตรวจ Order parent ownership ก่อนคืน transaction snapshot และ safe provider references")
            .Produces<TransactionView>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/transactions/{transactionId:guid}/events", async (
            Guid transactionId,
            Guid? merchantId,
            IAdminScope scope,
            IAdminOrderReader orders,
            ITransactionRepository transactions,
            CancellationToken ct) =>
        {
            var transaction = await ResolveTransactionAsync(transactionId, merchantId, scope, orders, transactions, ct);
            var events = await transactions.ListEventsAsync(transaction.MerchantId, transactionId, ct);
            return Results.Ok(events.Select(x => new CanonicalTransactionEventView(
                x.Id, x.Source, x.EventReference, x.Status?.ToString(), x.ProviderStatus,
                x.EvidenceCode, x.SafeDetails, x.OccurredAt, x.ReceivedAt)).ToArray());
        }).RequireAuthorization("admin").RequirePermission(Keys.PaymentView)
            .WithTags("ธุรกรรม").WithName("ListCanonicalTransactionEvents")
            .WithSummary("รายการ Transaction events")
            .WithDescription("ตรวจ parent visibility ก่อนอ่าน append-only events และไม่คืน raw webhook payload")
            .Produces<IReadOnlyList<CanonicalTransactionEventView>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/transactions/{transactionId:guid}/verify", async (
            Guid transactionId,
            Guid? merchantId,
            HttpContext http,
            IAdminScope scope,
            IAdminOrderReader orders,
            ITransactionRepository transactions,
            CheckoutTransactionService verification,
            IActorScope actorScope,
            CancellationToken ct) =>
        {
            var transaction = await ResolveTransactionAsync(transactionId, merchantId, scope, orders, transactions, ct);
            using var actor = actorScope.Begin(transaction.MerchantId);
            var expected = VersionEtags.Require(http);
            if (transaction.Version != expected)
                throw new ConcurrencyConflictException("Transaction changed after it was read.");
            await verification.VerifyAsync(transaction.MerchantId, transactionId, "admin-verify",
                IdempotencyKeys.Require(http), ct);
            var updated = await transactions.GetByIdAsync(transaction.MerchantId, transactionId, ct)
                ?? throw new NotFoundException("Transaction was not found.");
            VersionEtags.Set(http, updated.Version);
            return Results.Ok(TransactionViewMapper.ToView(updated));
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.PaymentView)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("ธุรกรรม").WithName("VerifyCanonicalTransaction")
            .WithSummary("ตรวจสอบ Transaction")
            .WithDescription("เป็น explicit support inquiry ด้วย pinned provider context ไม่สร้าง charge/attempt ใหม่และไม่ force-paid")
            .Produces<TransactionView>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);

        api.MapPost("/transactions/{transactionId:guid}/review-notes", async (
            Guid transactionId,
            Guid? merchantId,
            CanonicalTransactionReviewRequest body,
            HttpContext http,
            IAdminScope scope,
            IAdminOrderReader orders,
            ITransactionRepository transactions,
            IUnitOfWork unitOfWork,
            IClock clock,
            IActorScope actorScope,
            CancellationToken ct) =>
        {
            var transaction = await ResolveTransactionAsync(transactionId, merchantId, scope, orders, transactions, ct);
            using var actor = actorScope.Begin(transaction.MerchantId);
            var expected = VersionEtags.Require(http);
            if (transaction.Version != expected)
                throw new ConcurrencyConflictException("Transaction changed after it was read.");
            if (string.IsNullOrWhiteSpace(body.Note) || body.Note.Trim().Length > 2000)
                throw new InvalidRequestException("Review note is invalid.", "validation_failed");
            var key = IdempotencyKeys.Require(http);
            if (await transactions.EventExistsAsync(transaction.MerchantId, transaction.Id, "admin-review", key, ct))
            {
                VersionEtags.Set(http, transaction.Version);
                return Results.Ok(new CanonicalTransactionReviewView(key, body.Note.Trim(), transaction.Version));
            }
            transactions.AddEvent(TransactionEvent.Create(
                transaction.MerchantId, transaction.Id, "admin-review", key, transaction.Status,
                null, "review_note", body.Note.Trim(), clock.UtcNow, clock.UtcNow));
            await unitOfWork.SaveChangesAsync(ct);
            var updated = await transactions.GetByIdAsync(transaction.MerchantId, transactionId, ct)
                ?? throw new NotFoundException("Transaction was not found.");
            VersionEtags.Set(http, updated.Version);
            return Results.Ok(new CanonicalTransactionReviewView(key, body.Note.Trim(), updated.Version));
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.PaymentView)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("ธุรกรรม").WithName("AddCanonicalTransactionReviewNote")
            .WithSummary("เพิ่ม Transaction review note")
            .WithDescription("เพิ่ม append-only support event โดยไม่แก้ financial fields และไม่เก็บ raw provider payload")
            .Accepts<CanonicalTransactionReviewRequest>("application/json")
            .Produces<CanonicalTransactionReviewView>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);
    }

    private static async Task<AdminOrderResource> ResolveOrderAsync(
        IAdminOrderReader orders, IAdminScope scope, Guid orderId, CancellationToken ct) =>
        await orders.ResolveAsync(orderId, new AdminOrderAccess(
            scope.Accessible.IsUnrestricted, scope.Accessible.Merchants), ct)
        ?? throw new NotFoundException("Order was not found.");

    private static async Task<Transaction> ResolveTransactionAsync(
        Guid transactionId,
        Guid? selectedMerchantId,
        IAdminScope scope,
        IAdminOrderReader orders,
        ITransactionRepository transactions,
        CancellationToken ct)
    {
        if (selectedMerchantId is { } selected)
        {
            if (!scope.Accessible.Allows(selected))
                throw new AccessDeniedException("Merchant is outside the current Admin scope.", "merchant_scope_forbidden");
            var selectedTransaction = await transactions.GetByIdAsync(selected, transactionId, ct)
                ?? throw new NotFoundException("Transaction was not found.");
            var selectedParent = await orders.ResolveAsync(
                selectedTransaction.OrderId, new AdminOrderAccess(false, new HashSet<Guid> { selected }), ct);
            if (selectedParent is null)
                throw new NotFoundException("Order was not found.");
            return selectedTransaction;
        }
        var merchantIds = scope.Accessible.IsUnrestricted ? null : scope.Accessible.Merchants;
        if (merchantIds is null)
            throw new InvalidRequestException("merchantId is required for unrestricted transaction reads.", "invalid_filter");
        foreach (var merchantId in merchantIds)
        {
            var transaction = await transactions.GetByIdAsync(merchantId, transactionId, ct);
            if (transaction is null)
                continue;
            var parent = await orders.ResolveAsync(
                transaction.OrderId, new AdminOrderAccess(false, new HashSet<Guid> { merchantId }), ct);
            if (parent is null)
                throw new NotFoundException("Order was not found.");
            return transaction;
        }
        throw new NotFoundException("Transaction was not found.");
    }

    private static void ValidatePage(int page, int limit)
    {
        if (page < 1 || limit is < 1 or > 100)
            throw new InvalidRequestException("Page and limit are invalid.", "invalid_filter");
    }

    private static string SerializePatchIntent(
        Guid merchantId,
        Guid orderId,
        long expectedVersion,
        CanonicalPatchDraftOrderRequest body)
    {
        var items = body.Items?.Select(item => new
        {
            item.ProductReference,
            item.Quantity,
            Metadata = CanonicalMetadata(item.Metadata),
            item.ClientSnapshot,
        }).ToArray();
        return JsonSerializer.Serialize(new
        {
            merchantId,
            orderId,
            expectedVersion,
            patch = new
            {
                body.BusinessType,
                items,
                body.OwnerSaleId,
                body.OwnerBranchId,
                body.OrderDiscountAmount,
                body.OrderChargeAmount,
                Metadata = CanonicalMetadata(body.Metadata),
                body.NotificationIntent,
            },
        });
    }

    private static string? CanonicalMetadata(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
            return null;
        try
        {
            return VersionedMetadata.Parse(metadata)?.ToCanonicalJson();
        }
        catch (ArgumentException)
        {
            throw new InvalidRequestException(
                "Metadata must use the supported VersionedMetadata envelope.", "metadata_invalid");
        }
    }

    private static string? CanonicalMetadata(JsonElement? metadata)
    {
        if (metadata is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return CanonicalMetadata(value.GetRawText());
    }

    private static async ValueTask<object?> HandleKnownErrors(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (ConcurrencyConflictException)
        {
            return Results.Problem(statusCode: StatusCodes.Status412PreconditionFailed,
                extensions: new Dictionary<string, object?> { ["code"] = "precondition_failed", ["correlationId"] = context.HttpContext.TraceIdentifier });
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CanonicalPatchDraftOrderRequest(
    string? BusinessType = null,
    IReadOnlyList<OrderItemRequest>? Items = null,
    Guid? OwnerSaleId = null,
    Guid? OwnerBranchId = null,
    string? OrderDiscountAmount = null,
    string? OrderChargeAmount = null,
    JsonElement? Metadata = null,
    NotificationIntentRequest? NotificationIntent = null);

internal sealed record CanonicalOrderItemView(
    Guid ItemId,
    string ProductCode,
    string VariantCode,
    string? VariantName,
    int Quantity,
    Money UnitPrice,
    Money Discount,
    Money Tax,
    Money LineAmount);

internal sealed record CanonicalOrderHistoryView(string Status, DateTime OccurredAt);

internal sealed record CanonicalCheckoutPaymentMethodsResponse(IReadOnlyList<string> Methods);

internal sealed record CanonicalTransactionEventView(
    Guid EventId,
    string Source,
    string EventReference,
    string? Status,
    string? ProviderStatus,
    string? EvidenceCode,
    string? SafeDetails,
    DateTime OccurredAt,
    DateTime ReceivedAt);

internal sealed record CanonicalTransactionReviewRequest([property: Required] string Note);

internal sealed record CanonicalTransactionReviewView(string EventReference, string Note, long Version);
