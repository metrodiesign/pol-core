using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Vault;
using Contracts;
using Governance.Application;
using Governance.Domain;
using Iam.Domain.ApiClients;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Persistence.ControlPlane.Iam;

internal sealed class ApiClientApprovalExecutor(
    ControlPlaneDbContext db,
    IClock clock,
    IUnitOfWork unitOfWork,
    VaultKeyring keyring,
    IDataProtectionProvider protection) : IApprovalDecisionExecutor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector = protection.CreateProtector("pol-core/api-client-reveal/v1");

    public bool CanHandle(string targetType) => targetType == "api-client-secret";

    public Task ExecuteAsync(ApprovalDecided decision, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            if (decision.MerchantId is not { } merchantId || !Guid.TryParse(decision.TargetId, out var clientId))
                throw new InvalidOperationException("API-client approval target is invalid.");
            var client = await db.ApiClients.SingleOrDefaultAsync(
                x => x.Id == clientId && x.MerchantId == merchantId, ct)
                ?? throw new NotFoundException("API client was not found.");
            if (client.PendingRotationApprovalId != decision.ApprovalId
                || decision.TargetVersion != $"v{client.Version}"
                || client.PendingRotationTicketId is not { } ticketId)
                throw new ConcurrencyConflictException("API-client rotation target changed.");
            var ticket = await db.OneTimeSecretTickets.SingleAsync(x => x.Id == ticketId, ct);

            if (decision.Decision == "rejected")
            {
                ticket.Reject(decision.ApprovalId, clock.UtcNow);
                client.RejectRotation(decision.ApprovalId, clock.UtcNow);
                EnqueueResult(decision, succeeded: false, "api_client_secret_rotation_rejected", client.Version);
                await unitOfWork.SaveChangesAsync(ct);
                return true;
            }
            if (decision.Decision != "approved")
                throw new InvalidOperationException("Approval decision is invalid.");

            var secret = $"pol_{Token(32)}";
            var secretBytes = Encoding.UTF8.GetBytes(secret);
            try
            {
                var (_, key) = keyring.Active;
                client.CompleteRotation(decision.ApprovalId, HMACSHA256.HashData(key, secretBytes),
                    $"••••{secret[^4..]}", clock.UtcNow);
                ticket.Activate(decision.ApprovalId, _protector.Protect(secret), clock.UtcNow);
                EnqueueResult(decision, succeeded: true, "api_client_secret_rotated", client.Version);
                await unitOfWork.SaveChangesAsync(ct);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secretBytes);
            }
        }, cancellationToken);

    private void EnqueueResult(ApprovalDecided decision, bool succeeded, string outcome, long version)
    {
        var result = new ApprovalExecutionReported(
            Guid.CreateVersion7(), decision.ApprovalId, decision.CheckerId,
            succeeded, Unknown: false, outcome, $"v{version}", decision.CorrelationId, clock.UtcNow);
        db.GovernanceOutboxMessages.Add(GovernanceOutboxMessage.Create(
            result.EventId, GovernanceScopeKind.Merchant, decision.MerchantId,
            ApprovalExecutionReported.EventType, ApprovalExecutionReported.SchemaVersion,
            JsonSerializer.Serialize(result, Json), result.OccurredAt));
    }

    private static string Token(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
