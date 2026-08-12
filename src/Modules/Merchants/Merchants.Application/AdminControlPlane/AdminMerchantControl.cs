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

public interface IAdminMerchantControlStore
{
    Task<PagedResult<AdminMerchantListItem>> ListMerchantsAsync(
        AdminMerchantListQuery query, CancellationToken cancellationToken);

    Task<AdminMutationResult<AdminMerchantListItem>> UpdateMerchantAsync(
        AdminMerchantMutation mutation, CancellationToken cancellationToken);

    Task<AdminMutationResult<AdminMerchantListItem>> ChangeMerchantStatusAsync(
        AdminMerchantStatusMutation mutation, CancellationToken cancellationToken);

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
