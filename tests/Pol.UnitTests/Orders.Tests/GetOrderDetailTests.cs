using BuildingBlocks.Application;
using Orders.Application;
using Orders.Domain;
using Orders.Domain.Items;
using SharedKernel;

namespace Orders.Tests;

public sealed class GetOrderDetailTests
{
    private static readonly Guid Merchant = Guid.NewGuid();

    private static Order TwoLineOrder()
    {
        var metadata = new CommerceItemMetadata(
            CommerceItemMetadataCodec.InsuranceDocumentSource, "POLICY", "POL-1", null, null);
        return Order.Create(Merchant, Money.Of(30000m, "THB"), DateTime.UtcNow,
            [
                new OrderItemInput(1, Money.Of(15000m, "THB"), "DOC-1", "VMI", "One", Metadata: metadata),
                new OrderItemInput(1, Money.Of(15000m, "THB"), "DOC-2", "VMI", "Two", Metadata: metadata),
            ], "ORD6900000001");
    }

    [Fact]
    public async Task Detail_returns_server_metadata_after_per_line_audit()
    {
        var order = TwoLineOrder();
        var audits = new FakeRevealAuditWriter();
        var handler = new GetOrderDetailHandler(new FakeOrderRepository(order), audits, new FakeUnitOfWork());

        var result = await handler.Handle(
            new GetOrderDetailCommand(Merchant, order.Id, "merchant-user", "user-1"), default);

        Assert.Equal(2, audits.Appended.Count);
        Assert.All(result.Lines, line =>
        {
            Assert.NotNull(line.Metadata);
            Assert.Equal("insurance_document", line.Metadata.Value.GetProperty("sourceType").GetString());
        });
    }

    [Fact]
    public async Task Unknown_order_throws_NotFoundException()
    {
        var handler = new GetOrderDetailHandler(
            new FakeOrderRepository(), new FakeRevealAuditWriter(), new FakeUnitOfWork());

        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.Handle(new GetOrderDetailCommand(Merchant, Guid.NewGuid(), "merchant-user", "user-1"), default).AsTask());
    }

    [Fact]
    public async Task Failing_audit_blocks_response_and_save()
    {
        var order = TwoLineOrder();
        var unitOfWork = new FakeUnitOfWork();
        var handler = new GetOrderDetailHandler(
            new FakeOrderRepository(order),
            new FakeRevealAuditWriter { ShouldThrow = true },
            unitOfWork);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(new GetOrderDetailCommand(Merchant, order.Id, "merchant-user", "user-1"), default).AsTask());

        Assert.Equal(0, unitOfWork.SaveCount);
    }
}
