using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Outbox;
using MerchantRegistrationNotice = Merchants.Domain.Users.RegistrationNotice;
using OrderAggregate = Orders.Domain.Order;

namespace Worker;

/// <summary>
/// Worker capability (rls-to-query-filter task 8.5.6): the dispatchers draining <c>MerchantRuntimeDbContext</c>'s
/// outbox (payment/checkout events) and <c>MerchantUserDbContext</c>'s outbox (registration events) across
/// ANY merchant — cross-merchant dispatch is inherent to draining, so unlike the Api host's merchant-request
/// capability (<c>Api.Persistence.MerchantRequestWriteAuthorizer</c>) this one does NOT compare against a
/// single bound actor. Allows Update on the two outbox entity types (lease claim, mark-processed,
/// mark-failed — the drain ports' own tracked EF writes) — never Delete (outbox rows are never physically
/// removed). ALSO allows Insert on <see cref="MerchantRegistrationNotice"/> and <see cref="OrderAggregate"/>,
/// and Update on <see cref="OrderAggregate"/> — writes a message HANDLER performs mid-dispatch
/// (<c>Merchants.Application.Users.RegistrationConsumer</c> inserts a notice;
/// <c>Orders.Application.CheckoutConfirmedConsumer</c> inserts an order; <c>Orders.Application.
/// OrderPaidConsumer</c> updates one via <c>Order.MarkPaid</c> — all invoked via <c>IPublisher.Publish</c>
/// from inside an outbox drain cycle): none of these are themselves a drain-port write, so each needs its
/// own explicit allowlist entry. (insurance-pivot task 0 — Order was missing from this authorizer entirely
/// before this fix, for both operations; a pre-existing gap unrelated to insurance, surfaced while adding
/// `OrderLine` to the same dispatch path.)
/// <para>
/// Lives here, not in the Api host: stateless (zero dependencies) and Worker-only, so a cross-host reference
/// just to reach this one class would pull Worker's process dependency graph into Api's entire web surface
/// for no reason (see <c>Api/Persistence/WriteAuthorizers.cs</c>'s matching note).
/// </para>
/// </summary>
internal sealed class WorkerWriteAuthorizer : IWriteAuthorizer
{
    private static readonly HashSet<Type> DrainableOutboxTypes = [typeof(OutboxMessage), typeof(MerchantUserOutbox)];
    private static readonly HashSet<Type> MidDispatchInsertTypes = [typeof(MerchantRegistrationNotice), typeof(OrderAggregate)];

    public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) =>
        (operation == WriteOperation.Update && (DrainableOutboxTypes.Contains(entityType) || entityType == typeof(OrderAggregate)))
        || (operation == WriteOperation.Insert && MidDispatchInsertTypes.Contains(entityType));
}
