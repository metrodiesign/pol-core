using SharedKernel;

namespace Products.Application;

/// <summary>Read model returned by <see cref="GetProductsQuery"/>.</summary>
public sealed record ProductView(
    Guid ProductId,
    Guid MerchantId,
    string Name,
    Money Price,
    Money SumInsured,
    int CoverageDurationDays,
    string Insurer,
    bool IsActive,
    DateTime CreatedAt);
