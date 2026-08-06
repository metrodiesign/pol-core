using Orders.Application;
using Orders.Domain;
using Orders.Domain.Items;
using SharedKernel;

namespace Orders.Tests;

/// <summary>Merchant-authenticated order list — REQ-7.4's masked surface. No <see cref="IRevealAuditWriter"/>
/// dependency at all: nothing full-value is ever disclosed here, so there is nothing to audit.</summary>
public sealed class GetOrdersTests
{
    private static readonly Guid Merchant = Guid.NewGuid();
    private static readonly Guid OtherMerchant = Guid.NewGuid();
    private static readonly DateTime Dob = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Order OrderWithIdNumber(Guid merchantId, string idNumber, string orderNo = "ORD6900000001") =>
        Order.Create(merchantId, Money.Of(15000m, "THB"), DateTime.UtcNow,
            [new OrderItemInput(
                1, Money.Of(15000m, "THB"),
                "00098-69100/กธ/900001-10", "VMI", "POLICY", null, null, null,
                "Somchai", "Jaidee", idNumber, Dob)], orderNo);

    [Theory]
    [InlineData("12", "**")]           // shorter than 4 -> masked in full
    [InlineData("1234", "****")]       // exactly 4 -> masked in full
    [InlineData("1234567890123", "****0123")] // longer than 4 -> last 4 visible
    public async Task InsuredIdNumber_is_masked_on_the_list_surface(string idNumber, string expectedMasked)
    {
        var repo = new FakeOrderRepository(OrderWithIdNumber(Merchant, idNumber));
        var handler = new GetOrdersHandler(repo);

        var result = await handler.Handle(new GetOrdersQuery(Merchant), default);

        var line = Assert.Single(Assert.Single(result.Orders).Lines);
        Assert.Equal(expectedMasked, line.MaskedInsuredIdNumber);
    }

    [Fact]
    public async Task Name_and_date_of_birth_are_returned_as_is()
    {
        var repo = new FakeOrderRepository(OrderWithIdNumber(Merchant, "1234567890123"));
        var handler = new GetOrdersHandler(repo);

        var result = await handler.Handle(new GetOrdersQuery(Merchant), default);

        var line = Assert.Single(Assert.Single(result.Orders).Lines);
        Assert.Equal("Somchai", line.InsuredFirstName);
        Assert.Equal("Jaidee", line.InsuredLastName);
        Assert.Equal(Dob, line.InsuredDateOfBirth);
    }

    [Fact]
    public async Task Only_the_bound_merchants_orders_are_returned()
    {
        var repo = new FakeOrderRepository(
            OrderWithIdNumber(Merchant, "1234567890123"), OrderWithIdNumber(OtherMerchant, "9999999999999"));
        var handler = new GetOrdersHandler(repo);

        var result = await handler.Handle(new GetOrdersQuery(Merchant), default);

        Assert.Single(result.Orders);
    }

    // purchase-flow-completion REQ-7.3 — the number is on the list surface.
    [Fact]
    public async Task The_order_number_is_returned_on_the_list()
    {
        var handler = new GetOrdersHandler(new FakeOrderRepository(OrderWithIdNumber(Merchant, "1234567890123")));

        var result = await handler.Handle(new GetOrdersQuery(Merchant), default);

        Assert.Equal("ORD6900000001", Assert.Single(result.Orders).OrderNo);
    }

    // REQ-7.4 — filtering by order number returns only that order; no filter returns both.
    [Fact]
    public async Task Filtering_by_order_number_narrows_the_list()
    {
        var repo = new FakeOrderRepository(
            OrderWithIdNumber(Merchant, "1234567890123", "ORD6900000001"),
            OrderWithIdNumber(Merchant, "9999999999999", "ORD6900000002"));
        var handler = new GetOrdersHandler(repo);

        Assert.Equal(2, (await handler.Handle(new GetOrdersQuery(Merchant), default)).Orders.Count);

        var filtered = await handler.Handle(new GetOrdersQuery(Merchant, "ORD6900000002"), default);

        Assert.Equal("ORD6900000002", Assert.Single(filtered.Orders).OrderNo);
    }

    [Fact]
    public async Task Filtering_by_an_unknown_order_number_returns_nothing()
    {
        var handler = new GetOrdersHandler(new FakeOrderRepository(OrderWithIdNumber(Merchant, "1234567890123")));

        var result = await handler.Handle(new GetOrdersQuery(Merchant, "ORD6900009999"), default);

        Assert.Empty(result.Orders);
    }
}
