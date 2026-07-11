using SharedKernel;

namespace Cart.Application;

/// <summary>Outcome of <see cref="AddItemToCartCommand"/>: the cart's line count and its subtotal after the add.</summary>
public sealed record AddItemResult(Guid CartId, int ItemCount, Money Subtotal);
