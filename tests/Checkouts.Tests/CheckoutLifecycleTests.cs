using BuildingBlocks.Application;
using Checkouts.Application;
using Checkouts.Domain;
using Checkouts.Domain.Items;
using SharedKernel;

namespace Checkouts.Tests;

/// <summary>purchase-flow-completion REQ-2 — one live checkout per cart, and the way back out of it. The DB
/// backstop for the race this pre-check cannot win (IX_CheckoutSessions_CartId_Open) is proven separately:
/// its shape in Architecture.Tests/OpenCheckoutIndexTests, its enforcement in Integration.Tests.</summary>
public sealed class CheckoutLifecycleTests
{
    private static readonly Guid Merchant = Guid.NewGuid();
    private static readonly Guid CartId = Guid.NewGuid();
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly DateTime StartAt = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Money Amount = Money.Of(15000m, "THB");

    private static IReadOnlyList<CheckoutItemInput> OneLine() =>
        [new CheckoutItemInput(
            Product, 1, Amount, "00098-69100/AB/900001-10", "VMI", "POLICY", "POL-0001",
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2027, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            "Somchai", "Jaidee", "1234567890123", new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc))];

    private static readonly CustomerContact Customer = CustomerContact.Of("Somchai Jaidee", "0812345678", null);

    private static Session Started(Guid cartId) =>
        Session.Start(Merchant, cartId, Amount, StartAt, OneLine(), PaymentChannel.CARD, Customer);

    private static StartCheckoutCommand StartCommand(Guid cartId) =>
        new(Merchant, cartId, Amount, OneLine(), PaymentChannel.CARD, Customer);

    // REQ-2.3 — Started AND Confirmed both count as live: a confirmed checkout has already become an order,
    // so re-checking-out the same cart would sell the same documents twice.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Starting_a_second_checkout_for_a_cart_that_already_has_a_live_one_is_a_conflict(bool confirmFirst)
    {
        var existing = Started(CartId);
        if (confirmFirst)
            existing.Confirm();
        var handler = new StartCheckoutHandler(
            new FakeCheckoutRepository(existing), new FakeUnitOfWork(), new FixedClock());

        await Assert.ThrowsAsync<ConflictException>(async () =>
            await handler.Handle(StartCommand(CartId), default));
    }

    // ...but an abandoned one must not block the retry, which is the entire point of abandoning.
    [Fact]
    public async Task Starting_a_checkout_after_the_previous_one_was_abandoned_succeeds()
    {
        var abandoned = Started(CartId);
        abandoned.Abandon();
        var repo = new FakeCheckoutRepository(abandoned);
        var handler = new StartCheckoutHandler(repo, new FakeUnitOfWork(), new FixedClock());

        var result = await handler.Handle(StartCommand(CartId), default);

        Assert.NotEqual(Guid.Empty, result.CheckoutSessionId);
        Assert.Equal(result.CheckoutSessionId, Assert.Single(repo.Added).Id);
    }

    [Fact]
    public async Task A_live_checkout_on_a_DIFFERENT_cart_does_not_block_this_one()
    {
        var handler = new StartCheckoutHandler(
            new FakeCheckoutRepository(Started(Guid.NewGuid())), new FakeUnitOfWork(), new FixedClock());

        var result = await handler.Handle(StartCommand(CartId), default);

        Assert.NotEqual(Guid.Empty, result.CheckoutSessionId);
    }

    // REQ-2.5 — Started -> Abandoned, and the handler hands back the cart the endpoint must reopen.
    [Fact]
    public async Task Abandon_transitions_a_started_session_and_returns_its_cart()
    {
        var session = Started(CartId);
        var uow = new FakeUnitOfWork();
        var handler = new AbandonCheckoutHandler(new FakeCheckoutRepository(session), uow);

        var result = await handler.Handle(new AbandonCheckoutCommand(session.Id, Merchant), default);

        Assert.Equal(SessionStatus.Abandoned, result.Status);
        Assert.Equal(CartId, result.CartId);
        Assert.Equal(SessionStatus.Abandoned, session.Status);
    }

    // REQ-2.9 — idempotent: a retried cancel (or a first attempt that died after the session saved but before
    // the cart reopened) must succeed, not 409.
    [Fact]
    public async Task Abandoning_an_already_abandoned_session_succeeds_without_changing_anything()
    {
        var session = Started(CartId);
        session.Abandon();
        var handler = new AbandonCheckoutHandler(new FakeCheckoutRepository(session), new FakeUnitOfWork());

        var result = await handler.Handle(new AbandonCheckoutCommand(session.Id, Merchant), default);

        Assert.Equal(SessionStatus.Abandoned, result.Status);
    }

    // REQ-2.6 — a confirmed checkout is already an order; cancelling it is the order-cancel flow, not this one.
    [Fact]
    public async Task Abandoning_a_confirmed_session_is_rejected()
    {
        var session = Started(CartId);
        session.Confirm();
        var handler = new AbandonCheckoutHandler(new FakeCheckoutRepository(session), new FakeUnitOfWork());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await handler.Handle(new AbandonCheckoutCommand(session.Id, Merchant), default));

        Assert.Equal(SessionStatus.Confirmed, session.Status);
    }

    [Fact]
    public async Task Abandoning_an_unknown_session_is_a_not_found()
    {
        var handler = new AbandonCheckoutHandler(new FakeCheckoutRepository(), new FakeUnitOfWork());

        await Assert.ThrowsAsync<NotFoundException>(async () =>
            await handler.Handle(new AbandonCheckoutCommand(Guid.NewGuid(), Merchant), default));
    }
}
