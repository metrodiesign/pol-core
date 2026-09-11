using Orders.Application;
using Orders.Domain;
using Orders.Domain.Items;
using SharedKernel;

namespace Orders.Tests;

public sealed class GetOrdersTests
{
    private static readonly Guid Merchant = Guid.NewGuid();
    private static readonly Guid OtherMerchant = Guid.NewGuid();

    private static Order Build(Guid merchantId, string productCode, string orderNo = "ORD6900000001") =>
        Order.Create(merchantId, Money.Of(30000m, "THB"), DateTime.UtcNow,
            [new OrderItemInput(2, Money.Of(15000m, "THB"), productCode, "VMI", "ประกันรถยนต์")], orderNo);

    [Fact]
    public async Task List_returns_generic_line_without_metadata()
    {
        var handler = new GetOrdersHandler(new FakeOrderRepository(Build(Merchant, "DOC-1")));

        var result = await handler.Handle(new GetOrdersQuery(Merchant), default);

        var line = Assert.Single(Assert.Single(result.Items).Lines);
        Assert.Equal("DOC-1", line.ProductCode);
        Assert.Equal("VMI", line.VariantCode);
        Assert.Equal("ประกันรถยนต์", line.VariantName);
        Assert.Equal(2, line.Quantity);
        Assert.DoesNotContain(typeof(OrderItemListItem).GetProperties(), p => p.Name == "Metadata");
    }

    [Fact]
    public async Task Only_bound_merchant_orders_are_returned()
    {
        var handler = new GetOrdersHandler(new FakeOrderRepository(
            Build(Merchant, "DOC-1"), Build(OtherMerchant, "DOC-2")));

        var result = await handler.Handle(new GetOrdersQuery(Merchant), default);

        Assert.Single(result.Items);
    }

    [Fact]
    public async Task Filtering_by_order_number_narrows_list()
    {
        var handler = new GetOrdersHandler(new FakeOrderRepository(
            Build(Merchant, "DOC-1", "ORD6900000001"),
            Build(Merchant, "DOC-2", "ORD6900000002")));

        var filtered = await handler.Handle(new GetOrdersQuery(Merchant)
        {
            Filters = [new BuildingBlocks.Application.FilterOption(
                "orderNo",
                BuildingBlocks.Application.FilterOperator.Equals,
                System.Text.Json.JsonSerializer.SerializeToElement("ORD6900000002"))],
        }, default);

        Assert.Equal("ORD6900000002", Assert.Single(filtered.Items).OrderNo);
    }

    [Fact]
    public async Task List_returns_page_envelope_and_customer_fields()
    {
        var first = Build(Merchant, "DOC-1", "ORD6900000001");
        var second = Build(Merchant, "DOC-2", "ORD6900000002");
        var result = await new GetOrdersHandler(new FakeOrderRepository(first, second))
            .Handle(new GetOrdersQuery(Merchant) { Page = 2, Limit = 1 }, default);

        Assert.Equal(2, result.Page);
        Assert.Equal(1, result.Limit);
        Assert.Equal(2, result.Total);
        Assert.Single(result.Items);
        Assert.NotNull(result.Items[0].CustomerName);
    }
}
