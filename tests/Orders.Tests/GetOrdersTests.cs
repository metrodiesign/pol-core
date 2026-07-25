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
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly DateTime Dob = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Order OrderWithIdNumber(Guid merchantId, string idNumber) =>
        Order.Create(merchantId, Money.Of(15000m, "THB"), DateTime.UtcNow,
            [new OrderItemInput(
                Product, 1, Money.Of(15000m, "THB"), Money.Of(1_000_000m, "THB"), 365, "Test Insurer",
                "Somchai", "Jaidee", idNumber, Dob)]);

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
}
