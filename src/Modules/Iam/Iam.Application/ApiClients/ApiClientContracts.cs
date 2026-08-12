using BuildingBlocks.Application;

namespace Iam.Application.ApiClients;

public sealed record ApiClientAccess(bool IsUnrestricted, IReadOnlySet<Guid> MerchantIds)
{
    public bool Allows(Guid merchantId) => IsUnrestricted || MerchantIds.Contains(merchantId);
}

public sealed record ApiClientView(Guid Id, string ClientId, string Name, Guid MerchantId, Guid? OriginatorId,
    IReadOnlyList<string> Scopes, string? IpPolicy, string SecretHint, string Status, bool RotationPending,
    DateTime? LastUsedAt, DateTime CreatedAt, DateTime UpdatedAt, long Version);
public sealed record ApiClientCreate(string Name, Guid MerchantId, Guid? OriginatorId,
    IReadOnlyList<string> Scopes, string? IpPolicy, Guid ActorId, string IdempotencyKey);
public sealed record ApiClientUpdate(Guid Id, string Name, IReadOnlyList<string> Scopes, string? IpPolicy,
    long ExpectedVersion, Guid ActorId, string IdempotencyKey);
public sealed record OneTimeSecretTicketView(string TicketId, DateTime ExpiresAt);
public sealed record ApiClientCreated(ApiClientView Client, OneTimeSecretTicketView SecretTicket, bool Replayed);
public sealed record ApiClientMutation(ApiClientView Client, bool Replayed);
public sealed record ApiClientRotationRequested(Guid ApprovalId, OneTimeSecretTicketView SecretTicket,
    string Status, long ClientVersion, bool Replayed);
public sealed record SecretReveal(string ClientId, string ClientSecret);
public enum SecretRevealState { Ready, Pending, Expired, Consumed, Rejected, Unknown }
public sealed record SecretRevealResult(SecretRevealState State, SecretReveal? Secret);

public interface IApiClientStore
{
    Task<PagedResult<ApiClientView>> ListAsync(ApiClientAccess access, int page, int limit, string? search,
        Guid? merchantId, string? status, CancellationToken cancellationToken);
    Task<ApiClientView?> GetAsync(Guid id, ApiClientAccess access, CancellationToken cancellationToken);
    Task<ApiClientCreated> CreateAsync(ApiClientCreate input, ApiClientAccess access, CancellationToken cancellationToken);
    Task<ApiClientMutation?> UpdateAsync(ApiClientUpdate input, ApiClientAccess access, CancellationToken cancellationToken);
    Task<ApiClientMutation?> RevokeAsync(Guid id, long expectedVersion, Guid actorId, string idempotencyKey,
        ApiClientAccess access, CancellationToken cancellationToken);
    Task<ApiClientRotationRequested?> RequestRotationAsync(Guid id, long expectedVersion, Guid actorId,
        string idempotencyKey, string correlationId, ApiClientAccess access, CancellationToken cancellationToken);
    Task<SecretRevealResult> RevealAsync(string ticket, CancellationToken cancellationToken);
    Task<bool> VerifyAsync(string clientId, string secret, string? remoteAddress, CancellationToken cancellationToken);
}
