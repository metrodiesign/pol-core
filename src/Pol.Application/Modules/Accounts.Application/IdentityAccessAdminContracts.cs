using Access.Domain;
using Accounts.Domain;
using BuildingBlocks.Application;

namespace Accounts.Application;

public sealed record AccountAdminView(
    Guid AccountId,
    AccountType AccountType,
    string DisplayName,
    AccountStatus Status,
    long AuthorizationVersion,
    string? Provider,
    string? TenantId,
    string? ExternalUserId,
    Guid? MerchantId,
    Guid? SystemClientId);

public sealed record SystemClientAdminView(
    Guid SystemClientId,
    Guid AccountId,
    string ClientId,
    Guid MerchantId,
    string Environment,
    SystemClientStatus Status,
    long AccountAuthorizationVersion,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<ClientKeyAdminView> Keys);

public sealed record ClientKeyAdminView(
    Guid KeyId,
    string ApplicationId,
    string Kid,
    string Algorithm,
    DateTime ValidFrom,
    DateTime? ValidUntil,
    KeyPolicyStatus Status);

public sealed record MerchantAccessAdminView(
    Guid AccessId,
    Guid AccountId,
    Guid MerchantId,
    DataScope DataScope,
    AccessStatus Status,
    long Version,
    IReadOnlyList<Guid> RoleIds,
    IReadOnlyList<Guid> BranchIds,
    IReadOnlyList<string> PaymentMethods);

public sealed record PlatformAccessAdminView(
    Guid AccountId,
    PlatformAccessStatus Status,
    long Version,
    IReadOnlyList<Guid> RoleIds);

public sealed record BffSessionAdminView(
    Guid SessionId,
    Guid AccountId,
    string? ClientId,
    DateTime IssuedAt,
    DateTime ExpiresAt,
    DateTime? RevokedAt,
    bool IsLive);

public sealed record AccountAdminUpdate(
    Guid AccountId,
    string DisplayName,
    AccountStatus Status,
    long ExpectedAuthorizationVersion);

public sealed record SystemClientAdminCreate(
    Guid MerchantId,
    string ClientId,
    string DisplayName,
    string Environment,
    IReadOnlyList<string> Scopes);

public sealed record SystemClientAdminUpdate(
    Guid SystemClientId,
    string DisplayName,
    SystemClientStatus Status,
    long ExpectedAuthorizationVersion);

public sealed record SystemClientAccessReplace(
    Guid SystemClientId,
    IReadOnlyList<string> Scopes);

public sealed record ClientKeyAdminCreate(
    Guid SystemClientId,
    string ApplicationId,
    string Kid,
    string Algorithm,
    DateTime ValidFrom,
    DateTime? ValidUntil,
    string? AuditReference,
    string PublicJwkJson);

public sealed record MerchantAccessReplace(
    Guid AccountId,
    Guid MerchantId,
    DataScope DataScope,
    IReadOnlyList<Guid> RoleIds,
    IReadOnlyList<Guid> BranchIds,
    IReadOnlyList<string> PaymentMethods,
    long ExpectedVersion);

public sealed record PlatformAccessReplace(
    Guid AccountId,
    PlatformAccessStatus Status,
    IReadOnlyList<Guid> RoleIds,
    long ExpectedVersion);

public interface IIdentityAccessAdminStore
{
    Task<PagedResult<AccountAdminView>> ListAccountsAsync(PagedQuery query, CancellationToken cancellationToken);
    Task<AccountAdminView?> GetAccountAsync(Guid accountId, CancellationToken cancellationToken);
    Task<AccountAdminView> UpdateAccountAsync(AccountAdminUpdate update, CancellationToken cancellationToken);
    Task<(AccountAdminView Value, bool Replayed)> UpdateAccountIdempotentAsync(
        Guid actorId, string idempotencyKey, AccountAdminUpdate update, CancellationToken cancellationToken);
    Task RevokeSessionsAsync(Guid accountId, CancellationToken cancellationToken);
    Task<bool> RevokeSessionsIdempotentAsync(
        Guid actorId, string idempotencyKey, Guid accountId, CancellationToken cancellationToken);
    Task<IReadOnlyList<BffSessionAdminView>> ListBffSessionsAsync(
        Guid accountId, CancellationToken cancellationToken);
    Task RevokeBffSessionAsync(Guid accountId, Guid sessionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SystemClientAdminView>> ListSystemClientsAsync(
        Guid? merchantId, CancellationToken cancellationToken);
    Task<SystemClientAdminView?> GetSystemClientAsync(Guid systemClientId, CancellationToken cancellationToken);
    Task<SystemClientAdminView> CreateSystemClientAsync(
        SystemClientAdminCreate create, CancellationToken cancellationToken);
    Task<(SystemClientAdminView Value, bool Replayed)> CreateSystemClientIdempotentAsync(
        Guid actorId, string idempotencyKey, SystemClientAdminCreate create, CancellationToken cancellationToken);
    Task<SystemClientAdminView> UpdateSystemClientAsync(
        SystemClientAdminUpdate update, CancellationToken cancellationToken);
    Task<(SystemClientAdminView Value, bool Replayed)> UpdateSystemClientIdempotentAsync(
        Guid actorId, string idempotencyKey, SystemClientAdminUpdate update, CancellationToken cancellationToken);
    Task<SystemClientAdminView> ReplaceSystemClientAccessAsync(
        SystemClientAccessReplace replace, CancellationToken cancellationToken);
    Task<(SystemClientAdminView Value, bool Replayed)> ReplaceSystemClientAccessIdempotentAsync(
        Guid actorId, string idempotencyKey, SystemClientAccessReplace replace, CancellationToken cancellationToken);
    Task<IReadOnlyList<ClientKeyAdminView>> ListClientKeysAsync(
        Guid systemClientId, CancellationToken cancellationToken);
    Task<ClientKeyAdminView> CreateClientKeyAsync(
        ClientKeyAdminCreate create, CancellationToken cancellationToken);
    Task<(ClientKeyAdminView Value, bool Replayed)> CreateClientKeyIdempotentAsync(
        Guid actorId, string idempotencyKey, ClientKeyAdminCreate create, CancellationToken cancellationToken);
    Task DeleteClientKeyAsync(Guid systemClientId, Guid keyId, CancellationToken cancellationToken);
    Task<bool> DeleteClientKeyIdempotentAsync(
        Guid actorId, string idempotencyKey, Guid systemClientId, Guid keyId, CancellationToken cancellationToken);

    Task<MerchantAccessAdminView?> GetMerchantAccessAsync(
        Guid accountId, Guid merchantId, CancellationToken cancellationToken);
    Task<MerchantAccessAdminView> ReplaceMerchantAccessAsync(
        MerchantAccessReplace replace, CancellationToken cancellationToken);
    Task<(MerchantAccessAdminView Value, bool Replayed)> ReplaceMerchantAccessIdempotentAsync(
        Guid actorId, string idempotencyKey, MerchantAccessReplace replace, CancellationToken cancellationToken);
    Task RevokeMerchantAccessAsync(
        Guid accountId, Guid merchantId, long expectedVersion, CancellationToken cancellationToken);
    Task<bool> RevokeMerchantAccessIdempotentAsync(
        Guid actorId, string idempotencyKey, Guid accountId, Guid merchantId, long expectedVersion,
        CancellationToken cancellationToken);
    Task<PlatformAccessAdminView?> GetPlatformAccessAsync(
        Guid accountId, CancellationToken cancellationToken);
    Task<PlatformAccessAdminView> ReplacePlatformAccessAsync(
        PlatformAccessReplace replace, CancellationToken cancellationToken);
    Task<(PlatformAccessAdminView Value, bool Replayed)> ReplacePlatformAccessIdempotentAsync(
        Guid actorId, string idempotencyKey, PlatformAccessReplace replace, CancellationToken cancellationToken);
}
