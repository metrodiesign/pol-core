using BuildingBlocks.Application;
using Mediator;
using SharedKernel;

namespace Carts.Application;

/// <summary>Adds one server-resolved insurance product line to an open cart.</summary>
public sealed record AddItemToCartCommand(
    Guid CartId,
    Guid MerchantId,
    string ProductCode,
    string SaleCode,
    string VariantCode,
    string? VariantName,
    int Quantity,
    Money UnitPrice,
    CommerceItemMetadata Metadata) : ICommand<AddItemResult>, IMerchantScoped;
