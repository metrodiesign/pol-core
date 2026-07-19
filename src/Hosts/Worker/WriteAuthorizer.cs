using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Outbox;
using MerchantRegistrationNotice = Merchants.Domain.Users.RegistrationNotice;

namespace Worker;

/// <summary>
/// Worker capability (rls-to-query-filter task 8.5.6): the dispatchers draining <c>MerchantRuntimeDbContext</c>'s
/// outbox (payment/checkout events) and <c>MerchantUserDbContext</c>'s outbox (registration events) across
/// ANY merchant — cross-merchant dispatch is inherent to draining, so unlike the Api host's merchant-request
/// capability (<c>Api.Persistence.MerchantRequestWriteAuthorizer</c>) this one does NOT compare against a
/// single bound actor. Allows Update on the two outbox entity types (lease claim, mark-processed,
/// mark-failed — the drain ports' own tracked EF writes) — never Delete (outbox rows are never physically
/// removed). ALSO allows Insert on <see cref="MerchantRegistrationNotice"/> — the one write a message
/// HANDLER performs mid-dispatch (<c>Merchants.Application.Users.RegistrationConsumer</c>, invoked via
/// <c>IPublisher.Publish</c> from inside the registration-outbox drain cycle): it is not itself a drain-port
/// write, so it needs its own explicit allowlist entry.
/// <para>
/// Lives here, not in the Api host: stateless (zero dependencies) and Worker-only, so a cross-host reference
/// just to reach this one class would pull Worker's process dependency graph into Api's entire web surface
/// for no reason (see <c>Api/Persistence/WriteAuthorizers.cs</c>'s matching note).
/// </para>
/// </summary>
internal sealed class WorkerWriteAuthorizer : IWriteAuthorizer
{
    private static readonly HashSet<Type> DrainableOutboxTypes = [typeof(OutboxMessage), typeof(MerchantUserOutbox)];

    public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) =>
        (operation == WriteOperation.Update && DrainableOutboxTypes.Contains(entityType))
        || (operation == WriteOperation.Insert && entityType == typeof(MerchantRegistrationNotice));
}
