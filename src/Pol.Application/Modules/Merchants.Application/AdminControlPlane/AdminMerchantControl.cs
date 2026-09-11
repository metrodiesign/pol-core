using System.Text.Json;
using BuildingBlocks.Application;

namespace Merchants.Application.AdminControlPlane;

public sealed record AdminMerchantAccess(
    Guid ActorId,
    bool IsUnrestricted,
    IReadOnlySet<Guid> MerchantIds)
{
    public bool Allows(Guid merchantId) => IsUnrestricted || MerchantIds.Contains(merchantId);
}

public sealed record AdminMerchantListQuery(
    int Page,
    int Limit,
    string? Search,
    string? Status,
    AdminMerchantAccess Access);

public sealed record AdminMerchantListItem(
    Guid Id,
    string Code,
    string Name,
    string Status,
    string Country,
    string Currency,
    string EnabledChannels,
    DateTime CreatedAt,
    long Version);

public sealed record MerchantDetailView(
    Guid Id,
    string Code,
    string Name,
    string? Note,
    string Status,
    string Country,
    string Currency,
    string EnabledChannels,
    JsonElement Metadata,
    DateTime CreatedAt,
    long Version);

public sealed record AdminMerchantMutation(
    Guid MerchantId,
    string Name,
    string? Note,
    IReadOnlyList<string> EnabledChannels,
    JsonElement? Metadata,
    long ExpectedVersion,
    string IdempotencyKey,
    AdminMerchantAccess Access);

public sealed record AdminMerchantStatusMutation(
    Guid MerchantId,
    bool Activate,
    long ExpectedVersion,
    string IdempotencyKey,
    AdminMerchantAccess Access);

public sealed record AdminMerchantPatch(
    Guid MerchantId,
    string? Name,
    string? Note,
    IReadOnlyList<string>? EnabledChannels,
    JsonElement? Metadata,
    string? Status,
    long ExpectedVersion,
    string IdempotencyKey,
    AdminMerchantAccess Access);

public sealed record AdminMutationResult<T>(T Value, bool Replayed);

public sealed class AdminMerchantAccessDeniedException(string message) : Exception(message);

public sealed record OriginatorListQuery(
    int Page,
    int Limit,
    string? Search,
    Guid? MerchantId,
    string? Type,
    string? Status,
    AdminMerchantAccess Access);

public sealed record OriginatorView(
    Guid OriginatorId,
    Guid MerchantId,
    string Code,
    string Name,
    string Type,
    string? SaleCode,
    Guid? LinkedApiClientId,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    long Version);

public sealed record CreateOriginatorIntent(
    Guid MerchantId,
    string Code,
    string Name,
    string Type,
    string? SaleCode,
    Guid? LinkedApiClientId,
    AdminMerchantAccess Access);

public sealed record UpdateOriginatorIntent(
    Guid OriginatorId,
    Guid MerchantId,
    string Name,
    string Type,
    string? SaleCode,
    Guid? LinkedApiClientId,
    long ExpectedVersion,
    AdminMerchantAccess Access);

public sealed record OriginatorStateIntent(
    Guid OriginatorId,
    Guid MerchantId,
    bool Enable,
    long ExpectedVersion,
    AdminMerchantAccess Access);

public sealed record BranchListQuery(
    int Page,
    int Limit,
    string? Search,
    string? Status,
    Guid MerchantId,
    AdminMerchantAccess Access);

public sealed record BranchView(
    Guid BranchId,
    Guid MerchantId,
    string Code,
    string Name,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    long Version);

public sealed record CreateBranchIntent(
    Guid MerchantId,
    string Code,
    string Name,
    string IdempotencyKey,
    AdminMerchantAccess Access);

public sealed record UpdateBranchIntent(
    Guid BranchId,
    Guid MerchantId,
    string? Name,
    string? Status,
    long ExpectedVersion,
    string IdempotencyKey,
    AdminMerchantAccess Access);

public sealed record SaleListQuery(
    int Page,
    int Limit,
    string? Search,
    string? Status,
    Guid? BranchId,
    Guid MerchantId,
    AdminMerchantAccess Access);

public sealed record SaleView(
    Guid SaleId,
    Guid MerchantId,
    Guid BranchId,
    string Code,
    string Name,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    long Version);

public sealed record CreateSaleIntent(
    Guid MerchantId,
    Guid BranchId,
    string Code,
    string Name,
    string IdempotencyKey,
    AdminMerchantAccess Access);

public sealed record UpdateSaleIntent(
    Guid SaleId,
    Guid MerchantId,
    Guid? BranchId,
    string? Name,
    string? Status,
    string? Reason,
    long ExpectedVersion,
    string IdempotencyKey,
    AdminMerchantAccess Access);

public interface IAdminMerchantControlStore
{
    Task<PagedResult<AdminMerchantListItem>> ListMerchantsAsync(
        AdminMerchantListQuery query, CancellationToken cancellationToken);

    Task<AdminMutationResult<AdminMerchantListItem>> UpdateMerchantAsync(
        AdminMerchantMutation mutation, CancellationToken cancellationToken);

    Task<AdminMutationResult<AdminMerchantListItem>> ChangeMerchantStatusAsync(
        AdminMerchantStatusMutation mutation, CancellationToken cancellationToken);

    Task<MerchantDetailView?> GetMerchantAsync(
        Guid merchantId, AdminMerchantAccess access, CancellationToken cancellationToken);

    Task<AdminMutationResult<MerchantDetailView>> PatchMerchantAsync(
        AdminMerchantPatch mutation, CancellationToken cancellationToken);

    Task<PagedResult<BranchView>> ListBranchesAsync(
        BranchListQuery query, CancellationToken cancellationToken);

    Task<AdminMutationResult<BranchView>> CreateBranchAsync(
        CreateBranchIntent intent, CancellationToken cancellationToken);

    Task<AdminMutationResult<BranchView>> UpdateBranchAsync(
        UpdateBranchIntent intent, CancellationToken cancellationToken);

    Task<PagedResult<SaleView>> ListSalesAsync(
        SaleListQuery query, CancellationToken cancellationToken);

    Task<AdminMutationResult<SaleView>> CreateSaleAsync(
        CreateSaleIntent intent, CancellationToken cancellationToken);

    Task<AdminMutationResult<SaleView>> UpdateSaleAsync(
        UpdateSaleIntent intent, CancellationToken cancellationToken);

    Task<PagedResult<OriginatorView>> ListOriginatorsAsync(
        OriginatorListQuery query, CancellationToken cancellationToken);

    Task<OriginatorView?> GetOriginatorAsync(
        Guid originatorId, Guid? expectedMerchantId, AdminMerchantAccess access, CancellationToken cancellationToken);

    Task<OriginatorView> CreateOriginatorAsync(
        CreateOriginatorIntent intent, CancellationToken cancellationToken);

    Task<OriginatorView> UpdateOriginatorAsync(
        UpdateOriginatorIntent intent, CancellationToken cancellationToken);

    Task<OriginatorView> SetOriginatorStateAsync(
        OriginatorStateIntent intent, CancellationToken cancellationToken);

    Task DeleteOriginatorAsync(
        Guid originatorId, Guid merchantId, long expectedVersion, AdminMerchantAccess access,
        CancellationToken cancellationToken);
}
