using BuildingBlocks.Application;
using Mediator;
using SharedKernel;

namespace Carts.Application;

/// <summary>Adds a product line to an open cart, priced from the catalog.</summary>
public sealed record AddItemToCartCommand(
    Guid CartId,
    Guid MerchantId,
    Guid ProductId,
    int Quantity,
    Money UnitPrice) : ICommand<AddItemResult>, IMerchantScoped;
