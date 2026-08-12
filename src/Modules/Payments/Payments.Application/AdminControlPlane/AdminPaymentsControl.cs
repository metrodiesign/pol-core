using System.Text.Json;
using BuildingBlocks.Application;

namespace Payments.Application.AdminControlPlane;

public sealed record AdminPaymentsAccess(
    Guid ActorId,
    bool IsUnrestricted,
    IReadOnlySet<Guid> MerchantIds)
{
    public bool Allows(Guid merchantId) => IsUnrestricted || MerchantIds.Contains(merchantId);
}

public sealed class AdminPaymentsAccessDeniedException(string message) : Exception(message);
public sealed class PspConnectionTestFailedException(PspConnectionView connection) : Exception("PSP connection test failed.")
{
    public PspConnectionView Connection { get; } = connection;
}

public sealed record PspConnectionQuery(
    int Page,
    int Limit,
    string? Search,
    Guid? MerchantId,
    string? Psp,
    string? Health,
    AdminPaymentsAccess Access);

public sealed record PspConnectionView(
    Guid PspConnectionId,
    Guid MerchantId,
    string Psp,
    IReadOnlyList<string> EnabledMethods,
    JsonElement? Config,
    IReadOnlyDictionary<string, string> MaskedSecrets,
    bool IsEnabled,
    string Health,
    DateTime? LastTestedAt,
    string? LastTestResult,
    IReadOnlyDictionary<string, bool> Capabilities,
    DateTime CreatedAt,
    long Version);

public sealed record CreatePspConnectionIntent(
    Guid MerchantId,
    string Psp,
    IReadOnlyList<string> EnabledMethods,
    JsonElement? Config,
    IReadOnlyDictionary<string, string> Secrets,
    string? PspMerchantId,
    string IdempotencyKey,
    AdminPaymentsAccess Access);

public sealed record UpdatePspConnectionIntent(
    Guid ConnectionId,
    Guid MerchantId,
    IReadOnlyList<string> EnabledMethods,
    JsonElement? Config,
    bool IsEnabled,
    long ExpectedVersion,
    string IdempotencyKey,
    AdminPaymentsAccess Access);

public sealed record TestPspConnectionIntent(
    Guid ConnectionId,
    Guid MerchantId,
    long ExpectedVersion,
    string IdempotencyKey,
    AdminPaymentsAccess Access);

public sealed record RequestPspCredentialChangeIntent(
    Guid ConnectionId,
    Guid MerchantId,
    IReadOnlyDictionary<string, string> Secrets,
    string? PspMerchantId,
    long ExpectedVersion,
    string IdempotencyKey,
    string CorrelationId,
    AdminPaymentsAccess Access);

public sealed record PspConnectionMutationResult(PspConnectionView Connection, bool Replayed);
public sealed record PspCredentialChangeResult(Guid ApprovalId, Guid CandidateVersionId, string Status, bool Replayed);

public sealed record RoutingRuleInput(
    int Priority,
    string Method,
    Guid? OriginatorId,
    decimal? MinAmount,
    decimal? MaxAmount,
    Guid TargetConnectionId,
    Guid? FallbackConnectionId,
    bool Enabled);

public sealed record RoutingRuleView(
    Guid RuleId,
    int Priority,
    string Method,
    Guid? OriginatorId,
    string? MinAmount,
    string? MaxAmount,
    Guid TargetConnectionId,
    Guid? FallbackConnectionId,
    bool Enabled);

public sealed record RoutingRulesetView(
    Guid RulesetId,
    Guid MerchantId,
    string Name,
    string Status,
    Guid? ApprovalId,
    IReadOnlyList<RoutingRuleView> Rules,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    long Version);

public sealed record RoutingRulesetQuery(
    int Page,
    int Limit,
    Guid? MerchantId,
    string? Status,
    AdminPaymentsAccess Access);

public sealed record CreateRoutingRulesetIntent(
    Guid MerchantId,
    string Name,
    IReadOnlyList<RoutingRuleInput> Rules,
    AdminPaymentsAccess Access);

public sealed record ReplaceRoutingRulesetIntent(
    Guid RulesetId,
    Guid MerchantId,
    string Name,
    IReadOnlyList<RoutingRuleInput> Rules,
    long ExpectedVersion,
    AdminPaymentsAccess Access);

public sealed record RequestRoutingActivationIntent(
    Guid RulesetId,
    Guid MerchantId,
    long ExpectedVersion,
    string IdempotencyKey,
    string CorrelationId,
    AdminPaymentsAccess Access);

public sealed record RoutingActivationResult(Guid ApprovalId, RoutingRulesetView Ruleset, bool Replayed);

public interface IAdminPaymentsControlStore
{
    Task<PagedResult<PspConnectionView>> ListConnectionsAsync(PspConnectionQuery query, CancellationToken cancellationToken);
    Task<PspConnectionView?> GetConnectionAsync(Guid connectionId, Guid? merchantId, AdminPaymentsAccess access, CancellationToken cancellationToken);
    Task<PspConnectionMutationResult> CreateConnectionAsync(CreatePspConnectionIntent intent, CancellationToken cancellationToken);
    Task<PspConnectionMutationResult> UpdateConnectionAsync(UpdatePspConnectionIntent intent, CancellationToken cancellationToken);
    Task<PspConnectionMutationResult> TestConnectionAsync(TestPspConnectionIntent intent, CancellationToken cancellationToken);
    Task<PspCredentialChangeResult> RequestCredentialChangeAsync(RequestPspCredentialChangeIntent intent, CancellationToken cancellationToken);

    Task<PagedResult<RoutingRulesetView>> ListRulesetsAsync(RoutingRulesetQuery query, CancellationToken cancellationToken);
    Task<RoutingRulesetView?> GetRulesetAsync(Guid rulesetId, Guid? merchantId, AdminPaymentsAccess access, CancellationToken cancellationToken);
    Task<RoutingRulesetView> CreateRulesetAsync(CreateRoutingRulesetIntent intent, CancellationToken cancellationToken);
    Task<RoutingRulesetView> ReplaceRulesetAsync(ReplaceRoutingRulesetIntent intent, CancellationToken cancellationToken);
    Task DeleteRulesetAsync(Guid rulesetId, Guid merchantId, long expectedVersion, AdminPaymentsAccess access, CancellationToken cancellationToken);
    Task<RoutingActivationResult> RequestActivationAsync(RequestRoutingActivationIntent intent, CancellationToken cancellationToken);
}
