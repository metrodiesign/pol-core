namespace Cart.Application;

/// <summary>
/// Outcome of <see cref="AddItemToCartCommand"/>: the cart's line count and its subtotal after the
/// add, expressed as scalar minor-units + ISO 4217 code (the cross-boundary money shape).
/// </summary>
public sealed record AddItemResult(Guid CartId, int ItemCount, long SubtotalMinorUnits, string Currency);
