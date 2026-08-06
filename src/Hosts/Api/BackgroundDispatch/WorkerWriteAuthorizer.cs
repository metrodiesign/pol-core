using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Outbox;
using MerchantRegistrationNotice = Merchants.Domain.Users.RegistrationNotice;
using OrderAggregate = Orders.Domain.Order;
using OrderItem = Orders.Domain.Items.Item;

namespace Api.BackgroundDispatch;

/// <summary>
/// Background-dispatch write capability (multi-tier-deployment task 1 — moved in as-is from the standalone
/// Worker host per design.md's "minimal diff" decision; class name kept). Covers the dispatchers draining
/// <c>MerchantRuntimeDbContext</c>'s outbox (payment/checkout events) and <c>MerchantUserDbContext</c>'s
/// outbox (registration events) across ANY merchant — cross-merchant dispatch is inherent to draining, so
/// unlike <see cref="Api.Persistence.MerchantRequestWriteAuthorizer"/> this one does NOT compare against a
/// single bound actor. Allows Update on the two outbox entity types (lease claim, mark-processed,
/// mark-failed — the drain ports' own tracked EF writes) — never Delete (outbox rows are never physically
/// removed). ALSO allows Insert on <see cref="MerchantRegistrationNotice"/>, <see cref="OrderAggregate"/>,
/// <see cref="OrderItem"/> and <see cref="OutboxMessage"/>, and Update on <see cref="OrderAggregate"/>
/// — writes a message HANDLER performs mid-dispatch
/// (<c>Merchants.Application.Users.RegistrationConsumer</c> inserts a notice;
/// <c>Orders.Application.CheckoutConfirmedConsumer</c> inserts an order + its items and, when a recipient was
/// carried, enqueues a follow-on <c>CustomerOrderNotification</c> (an OutboxMessage insert);
/// <c>Orders.Application.OrderPaidConsumer</c> updates the order via <c>Order.MarkPaid</c> — all invoked via
/// <c>IPublisher.Publish</c> from inside an outbox drain cycle):
/// none of these are themselves a drain-port write, so each needs its own explicit allowlist entry. Chained
/// OutboxMessage inserts stay merchant-scoped (EfOutbox stamps the drained message's own merchant), so
/// allowing them here does not widen cross-merchant exposure — only Delete on the outbox stays denied.
/// </summary>
internal sealed class WorkerWriteAuthorizer : IWriteAuthorizer
{
    private static readonly HashSet<Type> DrainableOutboxTypes = [typeof(OutboxMessage), typeof(MerchantUserOutbox)];
    private static readonly HashSet<Type> MidDispatchInsertTypes =
        [typeof(MerchantRegistrationNotice), typeof(OrderAggregate), typeof(OrderItem), typeof(OutboxMessage)];
    private static readonly HashSet<Type> MidDispatchUpdateTypes = [typeof(OrderAggregate)];

    public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) =>
        (operation == WriteOperation.Update && (DrainableOutboxTypes.Contains(entityType) || MidDispatchUpdateTypes.Contains(entityType)))
        || (operation == WriteOperation.Insert && MidDispatchInsertTypes.Contains(entityType));
}
