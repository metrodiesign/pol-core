using BuildingBlocks.Application;
using Orders.Application;
using Orders.Domain;
using Orders.Domain.Items;
using SharedKernel;

namespace Orders.Tests;

/// <summary>Merchant-authenticated single-order detail read — REQ-7.4's full-reveal surface + REQ-7.5's
/// per-line, fail-closed audit trail.</summary>
public sealed class GetOrderDetailTests
{
    private static readonly Guid Merchant = Guid.NewGuid();
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly DateTime Dob = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Order TwoLineOrder() =>
        Order.Create(Merchant, Money.Of(30000m, "THB"), DateTime.UtcNow,
            [
                new OrderItemInput(Product, 1, Money.Of(15000m, "THB"), Money.Of(1_000_000m, "THB"), 365,
                    "Test Insurer", "Somchai", "Jaidee", "1111111111111", Dob),
                new OrderItemInput(Product, 1, Money.Of(15000m, "THB"), Money.Of(1_000_000m, "THB"), 365,
                    "Test Insurer", "Suda", "Meesuk", "2222222222222", Dob),
            ]);

    [Fact]
    public async Task Every_lines_full_InsuredIdNumber_is_returned()
    {
        var order = TwoLineOrder();
        var handler = new GetOrderDetailHandler(new FakeOrderRepository(order), new FakeRevealAuditWriter(), new FakeUnitOfWork());

        var result = await handler.Handle(new GetOrderDetailCommand(Merchant, order.Id, "merchant-user", "user-1"), default);

        Assert.Equal(["1111111111111", "2222222222222"], result.Lines.Select(l => l.InsuredIdNumber));
    }

    [Fact]
    public async Task One_reveal_audit_row_is_appended_per_line_returned()
    {
        var order = TwoLineOrder();
        var audits = new FakeRevealAuditWriter();
        var handler = new GetOrderDetailHandler(new FakeOrderRepository(order), audits, new FakeUnitOfWork());

        await handler.Handle(new GetOrderDetailCommand(Merchant, order.Id, "merchant-user", "user-1"), default);

        Assert.Equal(2, audits.Appended.Count);
        Assert.Equal(order.Items.Select(i => i.Id).ToArray(), audits.Appended);
    }

    [Fact]
    public async Task Unknown_order_throws_NotFoundException()
    {
        var handler = new GetOrderDetailHandler(new FakeOrderRepository(), new FakeRevealAuditWriter(), new FakeUnitOfWork());

        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.Handle(new GetOrderDetailCommand(Merchant, Guid.NewGuid(), "merchant-user", "user-1"), default).AsTask());
    }

    // REQ-7.5 fail-closed: a reveal that cannot be proven audited must not happen.
    [Fact]
    public async Task A_failing_audit_write_blocks_the_reveal_and_never_saves()
    {
        var order = TwoLineOrder();
        var unitOfWork = new FakeUnitOfWork();
        var handler = new GetOrderDetailHandler(
            new FakeOrderRepository(order), new FakeRevealAuditWriter { ShouldThrow = true }, unitOfWork);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(new GetOrderDetailCommand(Merchant, order.Id, "merchant-user", "user-1"), default).AsTask());

        Assert.Equal(0, unitOfWork.SaveCount);
    }
}
