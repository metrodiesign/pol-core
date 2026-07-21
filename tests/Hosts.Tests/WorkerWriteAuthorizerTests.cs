extern alias WorkerHost;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Outbox;
using Merchants.Domain;
using MerchantEntity = Merchants.Domain.Merchant;
using MerchantRegistrationNotice = Merchants.Domain.Users.RegistrationNotice;
using OrderAggregate = Orders.Domain.Order;

namespace Hosts.Tests;

/// <summary>
/// rls-to-query-filter task 8.5.6: <c>Worker.WorkerWriteAuthorizer</c> — moved out of
/// <see cref="WriteAuthorizersTests"/> alongside the class itself (it lives in <c>Hosts/Worker</c>, not the
/// Api host, since it is stateless and Worker-only).
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
        var authorizer = new WorkerHost::Worker.WorkerWriteAuthorizer();

        Assert.True(authorizer.CanWrite(outboxType, WriteOperation.Update, MerchantA));
        Assert.True(authorizer.CanWrite(outboxType, WriteOperation.Update, MerchantB));
    }

    [Fact]
    public void Worker_denies_insert_and_delete_on_the_outbox()
    {
        var authorizer = new WorkerHost::Worker.WorkerWriteAuthorizer();

        Assert.False(authorizer.CanWrite(typeof(OutboxMessage), WriteOperation.Insert, MerchantA));
        Assert.False(authorizer.CanWrite(typeof(OutboxMessage), WriteOperation.Delete, MerchantA));
    }

    [Fact]
    public void Worker_allows_insert_on_registration_notice_across_any_merchant()
    {
        var authorizer = new WorkerHost::Worker.WorkerWriteAuthorizer();

        Assert.True(authorizer.CanWrite(typeof(MerchantRegistrationNotice), WriteOperation.Insert, MerchantA));
        Assert.True(authorizer.CanWrite(typeof(MerchantRegistrationNotice), WriteOperation.Insert, Guid.Empty));
    }

    [Fact]
    public void Worker_denies_update_and_delete_on_registration_notice()
    {
        var authorizer = new WorkerHost::Worker.WorkerWriteAuthorizer();

        Assert.False(authorizer.CanWrite(typeof(MerchantRegistrationNotice), WriteOperation.Update, MerchantA));
        Assert.False(authorizer.CanWrite(typeof(MerchantRegistrationNotice), WriteOperation.Delete, MerchantA));
    }

    [Fact]
    public void Worker_denies_an_unrelated_entity_type()
    {
        var authorizer = new WorkerHost::Worker.WorkerWriteAuthorizer();

        Assert.False(authorizer.CanWrite(typeof(MerchantEntity), WriteOperation.Update, MerchantA));
    }

    /// <summary>insurance-pivot task 0: CheckoutConfirmedConsumer inserts the Order it creates while
    /// dispatched inside the Worker's real outbox drain — Order was missing from this authorizer entirely
    /// before this fix (a pre-existing gap unrelated to insurance).</summary>
    [Fact]
    public void Worker_allows_insert_on_order_across_any_merchant()
    {
        var authorizer = new WorkerHost::Worker.WorkerWriteAuthorizer();

        Assert.True(authorizer.CanWrite(typeof(OrderAggregate), WriteOperation.Insert, MerchantA));
        Assert.True(authorizer.CanWrite(typeof(OrderAggregate), WriteOperation.Insert, Guid.Empty));
    }

    /// <summary>OrderPaidConsumer updates the order via Order.MarkPaid while dispatched the same way —
    /// same pre-existing gap, same fix, so both operations are covered together.</summary>
    [Fact]
    public void Worker_allows_update_on_order_across_any_merchant()
    {
        var authorizer = new WorkerHost::Worker.WorkerWriteAuthorizer();

        Assert.True(authorizer.CanWrite(typeof(OrderAggregate), WriteOperation.Update, MerchantA));
        Assert.True(authorizer.CanWrite(typeof(OrderAggregate), WriteOperation.Update, Guid.Empty));
    }

    [Fact]
    public void Worker_denies_delete_on_order()
    {
        var authorizer = new WorkerHost::Worker.WorkerWriteAuthorizer();

        Assert.False(authorizer.CanWrite(typeof(OrderAggregate), WriteOperation.Delete, MerchantA));
    }
}
