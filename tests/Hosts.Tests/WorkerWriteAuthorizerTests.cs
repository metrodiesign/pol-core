extern alias ApiHost;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Outbox;
using Merchants.Domain;
using MerchantEntity = Merchants.Domain.Merchant;
using MerchantRegistrationNotice = Merchants.Domain.Users.RegistrationNotice;
using OrderAggregate = Orders.Domain.Order;

namespace Hosts.Tests;

/// <summary>
/// rls-to-query-filter task 8.5.6: <c>Api.BackgroundDispatch.WorkerWriteAuthorizer</c> — stateless, used for
/// every background-dispatch (outbox-driven) write regardless of which host process runs the dispatcher
/// (multi-tier-deployment task 2: Worker host retired, dispatchers now run inside Api).
/// </summary>
public sealed class WorkerWriteAuthorizerTests
{
    private static readonly Guid MerchantA = Guid.NewGuid();
    private static readonly Guid MerchantB = Guid.NewGuid();

    [Theory]
    [InlineData(typeof(OutboxMessage))]
    [InlineData(typeof(MerchantUserOutbox))]
    public void Worker_allows_update_on_either_outbox_type_across_any_merchant(Type outboxType)
    {
        var authorizer = new ApiHost::Api.BackgroundDispatch.WorkerWriteAuthorizer();

        Assert.True(authorizer.CanWrite(outboxType, WriteOperation.Update, MerchantA));
        Assert.True(authorizer.CanWrite(outboxType, WriteOperation.Update, MerchantB));
    }

    // A mid-dispatch handler may enqueue a follow-on integration event (CustomerOrderNotification), which is an
    // OutboxMessage insert into the drained message's own merchant — allowed. Delete never is.
    [Fact]
    public void Worker_allows_insert_but_denies_delete_on_the_runtime_outbox()
    {
        var authorizer = new ApiHost::Api.BackgroundDispatch.WorkerWriteAuthorizer();

        Assert.True(authorizer.CanWrite(typeof(OutboxMessage), WriteOperation.Insert, MerchantA));
        Assert.True(authorizer.CanWrite(typeof(OutboxMessage), WriteOperation.Insert, Guid.Empty));
        Assert.False(authorizer.CanWrite(typeof(OutboxMessage), WriteOperation.Delete, MerchantA));
    }

    // The MerchantUser outbox has no mid-drain chained enqueue — its insert stays denied.
    [Fact]
    public void Worker_denies_insert_and_delete_on_the_merchant_user_outbox()
    {
        var authorizer = new ApiHost::Api.BackgroundDispatch.WorkerWriteAuthorizer();

        Assert.False(authorizer.CanWrite(typeof(MerchantUserOutbox), WriteOperation.Insert, MerchantA));
        Assert.False(authorizer.CanWrite(typeof(MerchantUserOutbox), WriteOperation.Delete, MerchantA));
    }

    [Fact]
    public void Worker_allows_insert_on_registration_notice_across_any_merchant()
    {
        var authorizer = new ApiHost::Api.BackgroundDispatch.WorkerWriteAuthorizer();

        Assert.True(authorizer.CanWrite(typeof(MerchantRegistrationNotice), WriteOperation.Insert, MerchantA));
        Assert.True(authorizer.CanWrite(typeof(MerchantRegistrationNotice), WriteOperation.Insert, Guid.Empty));
    }

    [Fact]
    public void Worker_denies_update_and_delete_on_registration_notice()
    {
        var authorizer = new ApiHost::Api.BackgroundDispatch.WorkerWriteAuthorizer();

        Assert.False(authorizer.CanWrite(typeof(MerchantRegistrationNotice), WriteOperation.Update, MerchantA));
        Assert.False(authorizer.CanWrite(typeof(MerchantRegistrationNotice), WriteOperation.Delete, MerchantA));
    }

    [Fact]
    public void Worker_denies_an_unrelated_entity_type()
    {
        var authorizer = new ApiHost::Api.BackgroundDispatch.WorkerWriteAuthorizer();

        Assert.False(authorizer.CanWrite(typeof(MerchantEntity), WriteOperation.Update, MerchantA));
    }

    /// <summary>Order creation is request-owned after Checkout retirement, so background dispatch cannot
    /// insert an Order.</summary>
    [Fact]
    public void Worker_denies_insert_on_order()
    {
        var authorizer = new ApiHost::Api.BackgroundDispatch.WorkerWriteAuthorizer();

        Assert.False(authorizer.CanWrite(typeof(OrderAggregate), WriteOperation.Insert, MerchantA));
        Assert.False(authorizer.CanWrite(typeof(OrderAggregate), WriteOperation.Insert, Guid.Empty));
    }

    /// <summary>OrderPaidConsumer updates the order via Order.MarkPaid while dispatched the same way —
    /// same pre-existing gap, same fix, so both operations are covered together.</summary>
    [Fact]
    public void Worker_allows_update_on_order_across_any_merchant()
    {
        var authorizer = new ApiHost::Api.BackgroundDispatch.WorkerWriteAuthorizer();

        Assert.True(authorizer.CanWrite(typeof(OrderAggregate), WriteOperation.Update, MerchantA));
        Assert.True(authorizer.CanWrite(typeof(OrderAggregate), WriteOperation.Update, Guid.Empty));
    }

    [Fact]
    public void Worker_denies_delete_on_order()
    {
        var authorizer = new ApiHost::Api.BackgroundDispatch.WorkerWriteAuthorizer();

        Assert.False(authorizer.CanWrite(typeof(OrderAggregate), WriteOperation.Delete, MerchantA));
    }

    // The Product aggregate and its mid-dispatch mark-paid update were retired with the catalogue mirror
    // (products-external-source-of-truth REQ-6.5/6.7), so the Worker no longer allows any write on it — the
    // former Product allow/deny tests went away with the type.
}
