using BuildingBlocks.Application;
using Checkouts.Application;
using Checkouts.Domain;
using Checkouts.Domain.Lines;
using Contracts;
using Mediator;
using SharedKernel;

namespace Checkouts.Tests;

/// <summary>Checkout -> Order keystone, merchant-user side (REQ-5.1/5.5): start captures the recipient; confirm
/// transitions the session AND emits CheckoutConfirmed (carrying amount + recipient + lines) in the same unit
/// of work, so Orders opens the order out-of-band.</summary>
public sealed class ConfirmCheckoutTests
{
    private static readonly Guid Merchant = Guid.NewGuid();
    private static readonly Guid Cart = Guid.NewGuid();
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly DateTime Dob = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<CheckoutLineInput> OneLine() =>
        [new CheckoutLineInput(
            Product, 1, Money.Of(15000m, "THB"), Money.Of(1_000_000m, "THB"), 365, "Test Insurer",
            "Somchai", "Jaidee", "1234567890123", Dob)];

    private static readonly DateTime StartAt = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Start_captures_the_notification_recipient()
    {
        var repo = new FakeCheckoutRepository();
        var handler = new StartCheckoutHandler(repo, new FakeUnitOfWork(), new FixedClock());

        await handler.Handle(
            new StartCheckoutCommand(Merchant, Cart, Money.Of(15000m, "THB"), OneLine(), "buyer@example.com"), default);

        Assert.Equal("buyer@example.com", Assert.Single(repo.Added).NotificationRecipient);
    }

    [Fact]
    public void Start_rejects_an_empty_line_list()
    {
        Assert.Throws<ArgumentException>(() => Session.Start(
            Merchant, Cart, Money.Of(15000m, "THB"), new DateTime(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc), []));
    }

    // REQ-7.2 — validated at Start, not deferred to Order.Create, so a bad request never reaches a
    // successful confirm response (Codex P1 finding, PR #122).
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Start_rejects_a_blank_insured_IdNumber(string idNumber)
    {
        var line = new CheckoutLineInput(
            Product, 1, Money.Of(15000m, "THB"), Money.Of(1_000_000m, "THB"), 365, "Test Insurer",
            "Somchai", "Jaidee", idNumber, Dob);

        Assert.Throws<ArgumentException>(() => Session.Start(Merchant, Cart, Money.Of(15000m, "THB"), StartAt, [line]));
    }

    [Fact]
    public void Start_rejects_a_future_date_of_birth()
    {
        var line = new CheckoutLineInput(
            Product, 1, Money.Of(15000m, "THB"), Money.Of(1_000_000m, "THB"), 365, "Test Insurer",
            "Somchai", "Jaidee", "1234567890123", StartAt.AddDays(1));

        Assert.Throws<ArgumentException>(() => Session.Start(Merchant, Cart, Money.Of(15000m, "THB"), StartAt, [line]));
    }

    [Fact]
    public void The_thrown_exception_never_echoes_the_invalid_date_of_birth_value()
    {
        var distinctiveFutureDob = new DateTime(2099, 3, 14, 0, 0, 0, DateTimeKind.Utc);
        var line = new CheckoutLineInput(
            Product, 1, Money.Of(15000m, "THB"), Money.Of(1_000_000m, "THB"), 365, "Test Insurer",
            "Somchai", "Jaidee", "1234567890123", distinctiveFutureDob);

        var ex = Assert.Throws<ArgumentException>(
            () => Session.Start(Merchant, Cart, Money.Of(15000m, "THB"), StartAt, [line]));

        Assert.DoesNotContain("2099", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirm_transitions_and_emits_CheckoutConfirmed()
    {
        var session = Session.Start(
            Merchant, Cart, Money.Of(15000m, "THB"), new DateTime(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc), OneLine(),
            "buyer@example.com");
        var repo = new FakeCheckoutRepository(session);
        var outbox = new FakeOutbox();
        var handler = new ConfirmCheckoutHandler(repo, outbox, new FakeUnitOfWork(), new FixedClock());

        var result = await handler.Handle(new ConfirmCheckoutCommand(session.Id, Merchant), default);

        Assert.Equal(SessionStatus.Confirmed, result.Status);
        var evt = Assert.IsType<CheckoutConfirmed>(Assert.Single(outbox.Enqueued));
        Assert.Equal(session.Id, evt.CheckoutSessionId);
        Assert.Equal(Money.Of(15000m, "THB"), evt.Amount);
        Assert.Equal("buyer@example.com", evt.Recipient);
        Assert.Single(evt.Lines);
    }
}

internal sealed class FakeCheckoutRepository : ICheckoutRepository
{
    private readonly List<Session> _sessions = [];
    public FakeCheckoutRepository(params Session[] seed) => _sessions.AddRange(seed);

    public readonly List<Session> Added = [];

    public void Add(Session session) { _sessions.Add(session); Added.Add(session); }

    public Task<Session?> GetByIdAsync(Guid checkoutSessionId, CancellationToken cancellationToken) =>
        Task.FromResult(_sessions.FirstOrDefault(s => s.Id == checkoutSessionId));
}

internal sealed class FakeOutbox : IOutbox
{
    public readonly List<INotification> Enqueued = [];
    public void Enqueue(INotification notification) => Enqueued.Add(notification);
}

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(0);
    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
        await operation(ct);
}

internal sealed class FixedClock : IClock
{
    public DateTime UtcNow { get; init; } = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);
}
