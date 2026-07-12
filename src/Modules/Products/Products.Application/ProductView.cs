using SharedKernel;

namespace Products.Application;

/// <summary>Read model returned by <see cref="GetProductsQuery"/>.</summary>
public sealed record ProductView(
    Guid ProductId,
    Guid MerchantId,
    string Name,
    Money Price,
    bool IsActive,
    DateTime CreatedAt);
