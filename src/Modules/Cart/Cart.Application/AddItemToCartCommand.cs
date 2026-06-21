using BuildingBlocks.Application;
using Mediator;

namespace Cart.Application;

/// <summary>
/// Adds a product line to an open cart. The unit price is carried as scalar minor-units + ISO 4217
/// code (the cross-boundary money shape) and re-validated into a <c>Money</c> inside the handler.
/// </summary>
public sealed record AddItemToCartCommand(
    Guid CartId,
    Guid TenantId,
    Guid ProductId,
    int Quantity,
    long UnitPriceMinorUnits,
    string Currency) : ICommand<AddItemResult>, ITenantScoped;
