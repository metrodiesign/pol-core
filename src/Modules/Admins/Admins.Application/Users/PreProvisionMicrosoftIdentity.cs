using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using SharedKernel;

namespace Admins.Application.Users;

public sealed record PreProvisionMicrosoftIdentityCommand(
    Guid TargetAdminId,
    Guid WorkforceTenantId,
    Guid EntraObjectId,
    string Reason,
    Guid ActingAdminId,
    long ExpectedAuthorizationVersion,
    long ExpectedTargetVersion,
    string CorrelationId,
    string IdempotencyKey,
    Guid? ConfiguredWorkforceTenantId)
    : ICommand<PreProvisionMicrosoftIdentityResult>;

public sealed record PreProvisionMicrosoftIdentityResult(
    Guid AdminId,
    string Provider,
    bool SubjectBound,
    long Version);

public sealed class PreProvisionMicrosoftIdentityHandler :
    ICommandHandler<PreProvisionMicrosoftIdentityCommand, PreProvisionMicrosoftIdentityResult>
{
    private const string Operation = "PreProvisionMicrosoftIdentity";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IUserRepository _admins;
    private readonly IAdminIdentityAuditWriter _audit;
    private readonly IAdminOperationStore _operations;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public PreProvisionMicrosoftIdentityHandler(
        IUserRepository admins,
        IAdminIdentityAuditWriter audit,
        IAdminOperationStore operations,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _admins = admins;
        _audit = audit;
        _operations = operations;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<PreProvisionMicrosoftIdentityResult> Handle(
        PreProvisionMicrosoftIdentityCommand command, CancellationToken cancellationToken)
    {
        var tenantId = CanonicalRequiredGuid(
            command.WorkforceTenantId, "Workforce tenant identifier is required.", "invalid_entra_tenant_id");
        var objectId = CanonicalRequiredGuid(
            command.EntraObjectId, "Entra Object identifier is required.", "invalid_entra_object_id");
        var reason = NormalizeReason(command.Reason, command.WorkforceTenantId, command.EntraObjectId);
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Length > 200)
            throw new InvalidRequestException("Idempotency-Key is required and must not exceed 200 characters.",
                "invalid_idempotency_key");
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CorrelationId);

        var requestHash = HashIntent(command.TargetAdminId, tenantId, objectId, reason);

        try
        {
            return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                await _operations.AcquireAsync(command.ActingAdminId, Operation, command.IdempotencyKey, ct);
                var prior = await _operations.FindAsync(
                    command.ActingAdminId, Operation, command.IdempotencyKey, ct);
                if (prior is not null)
                {
                    if (!string.Equals(prior.RequestHash, requestHash, StringComparison.Ordinal))
                        throw new ConflictException(
                            "The idempotency key was reused for a different identity binding.",
                            "idempotency_key_reused");
                    if (prior.InProgress || prior.ResponseBody is null)
                        throw new ConflictException(
                            "The identity binding outcome is not available.", "operation_in_progress");
                    return JsonSerializer.Deserialize<PreProvisionMicrosoftIdentityResult>(prior.ResponseBody, Json)
                        ?? throw new InvalidOperationException("Recorded identity-binding response is invalid.");
                }

                await _admins.AcquireIdentityMutationLockAsync(ct);
                await _admins.VerifyActiveSuperAsync(
                    command.ActingAdminId, command.ExpectedAuthorizationVersion, ct);

                if (command.ConfiguredWorkforceTenantId is null
                    || command.ConfiguredWorkforceTenantId == Guid.Empty)
                    throw new ConflictException(
                        "The Admin Microsoft provider is disabled.", "microsoft_provider_disabled");
                if (command.WorkforceTenantId != command.ConfiguredWorkforceTenantId.Value)
                    throw new InvalidRequestException(
                        "The workforce tenant does not match the configured tenant.", "entra_tenant_mismatch");

                var target = await _admins.GetByIdAsync(command.TargetAdminId, ct)
                    ?? throw new NotFoundException("The admin account was not found.", "admin_not_found");
                if (target.Tier != Tier.Scoped)
                    throw new ConflictException(
                        "Microsoft identity pre-provisioning requires a Scoped admin.", "target_not_scoped");
                if (target.Version != command.ExpectedTargetVersion)
                    throw new ConcurrencyConflictException("The admin account resource version is stale.");

                if (string.Equals(target.Provider, User.MicrosoftProvider, StringComparison.Ordinal)
                    && string.Equals(target.Subject, objectId, StringComparison.Ordinal))
                {
                    var noOp = Result(target);
                    AddSucceeded(command, requestHash, noOp);
                    await _unitOfWork.SaveChangesAsync(ct);
                    return noOp;
                }

                if (target.Subject is not null)
                    throw new ConflictException(
                        "The admin account already has a bound identity.", "admin_identity_already_bound");

                var owner = await _admins.GetByIdentityAsync(
                    new ProviderIdentity(User.MicrosoftProvider, objectId), ct);
                if (owner is not null)
                    throw new ConflictException(
                        "The Microsoft identity is already bound.", "microsoft_identity_already_bound");

                target.BindSubject(User.MicrosoftProvider, objectId);
                var now = _clock.UtcNow;
                await _audit.AppendMicrosoftPreProvisionAsync(new AdminIdentityAuditEntry(
                    command.ActingAdminId,
                    target.Id,
                    reason,
                    Fingerprint(tenantId, objectId),
                    target.Version,
                    command.CorrelationId,
                    now), ct);

                var result = Result(target);
                AddSucceeded(command, requestHash, result, now);
                await _unitOfWork.SaveChangesAsync(ct);
                return result;
            }, cancellationToken);
        }
        catch (ConflictException exception) when (exception.Code is null)
        {
            var owner = await _admins.GetByIdentityAsync(
                new ProviderIdentity(User.MicrosoftProvider, objectId), cancellationToken);
            if (owner is not null && owner.Id != command.TargetAdminId)
                throw new ConflictException(
                    "The Microsoft identity is already bound.", "microsoft_identity_already_bound");
            throw new ConcurrencyConflictException(
                "A concurrent change to the target admin was detected.");
        }
        catch (ConcurrencyConflictException)
        {
            await _admins.VerifyActiveSuperAsync(
                command.ActingAdminId, command.ExpectedAuthorizationVersion, cancellationToken);
            throw new ConcurrencyConflictException(
                "A concurrent change to the target admin was detected.");
        }
    }

    private void AddSucceeded(
        PreProvisionMicrosoftIdentityCommand command,
        string requestHash,
        PreProvisionMicrosoftIdentityResult result,
        DateTime? now = null)
    {
        var occurredAt = now ?? _clock.UtcNow;
        _operations.AddSucceeded(
            command.ActingAdminId,
            Operation,
            command.IdempotencyKey,
            requestHash,
            200,
            JsonSerializer.Serialize(result, Json),
            occurredAt,
            DateTime.MaxValue);
    }

    private static PreProvisionMicrosoftIdentityResult Result(User target) =>
        new(target.Id, User.MicrosoftProvider, SubjectBound: true, target.Version);

    private static string CanonicalRequiredGuid(Guid value, string message, string code)
    {
        if (value == Guid.Empty)
            throw new InvalidRequestException(message, code);
        return value.ToString("D").ToLowerInvariant();
    }

    private static string NormalizeReason(string reason, Guid tenantId, Guid objectId)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidRequestException("Reason is required.", "invalid_reason");
        var normalized = reason.Trim();
        if (normalized.Length > 1000
            || normalized.Contains('@')
            || ContainsGuid(normalized, tenantId)
            || ContainsGuid(normalized, objectId))
            throw new InvalidRequestException("Reason contains prohibited identity data.", "invalid_reason");
        return normalized;
    }

    private static bool ContainsGuid(string value, Guid id) =>
        new[] { "D", "N", "B", "P", "X" }
            .Any(format => value.Contains(id.ToString(format), StringComparison.OrdinalIgnoreCase));

    private static string HashIntent(Guid targetId, string tenantId, string objectId, string reason) =>
        LowerHex(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{targetId:D}\n{tenantId}\n{objectId}\n{reason}")));

    private static string Fingerprint(string tenantId, string objectId) =>
        $"sha256:{LowerHex(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}\n{objectId}")))}";

    private static string LowerHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
