namespace Admins.Application.Users;

public sealed record AdminOperationReplay(string RequestHash, string? ResponseBody, bool InProgress);

/// <summary>Durable replay seam over admin.OperationRecords for retry-safe Admin credential/session mutations.</summary>
public interface IAdminOperationStore
{
    Task AcquireAsync(Guid actorId, string operation, string idempotencyKey, CancellationToken cancellationToken);
    Task<AdminOperationReplay?> FindReplayAsync(
        Guid actorId, string operation, string idempotencyKey, CancellationToken cancellationToken);
    void AddSucceeded(
        Guid actorId, string operation, string idempotencyKey, string requestHash,
        int responseStatus, string responseBody, DateTime now, DateTime expiresAt);
}
