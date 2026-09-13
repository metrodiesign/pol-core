namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>Minimal model-building seam shared by the runtime Control Plane and the design-time migration
/// composition. Keeping the query-filter owner independent from a concrete DbContext prevents duplicate
/// entity mappings when the two runtime contexts are assembled.</summary>
public interface IMerchantFilterContext
{
    Guid CurrentMerchant { get; }
}
