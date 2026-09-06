using System.Text.Json;
using BuildingBlocks.Application;
using Payments.Application.Capabilities;

namespace Payments.Application.AdminControlPlane;

public sealed record AdminPaymentsAccess(
    Guid ActorId,
    long AuthorizationVersion,
    bool IsUnrestricted,
    IReadOnlySet<Guid> MerchantIds)
{
    public bool Allows(Guid merchantId) => IsUnrestricted || MerchantIds.Contains(merchantId);
}

public sealed class AdminPaymentsAccessDeniedException(string message) : Exception(message);
public sealed class PaymentCapabilityUnavailableException(string message) : Exception(message);
public sealed class PaymentAuthorizationBusyException(string message) : Exception(message);

public interface IMerchantRuntimeAuthorizationLease
{
    Task VerifyAsync(AdminPaymentsAccess access, CancellationToken cancellationToken);
}
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

/// <summary>Result of an optional read-only probe of a staged candidate credential (REQ-7.8/7.9).</summary>
public sealed record PspCredentialTestView(string Result, DateTime TestedAt);

/// <summary>Whether an admin confirmed the connection's callback URL is registered at the PSP (REQ-11.2).</summary>
public sealed record WebhookRegistrationView(bool Acknowledged, DateTime? AcknowledgedAt);

/// <summary>Safe projection of a connection: masked hints only, never a secret or the vault envelope.
/// <c>Environment</c> is the merchant's (inherited, REQ-2.2); <c>CredentialEnvironment</c> is what the active
/// credential was issued for; <c>CallbackUrl</c> carries no secret (REQ-11.1).</summary>
/// <summary>One canonical method as seen on one provider account (REQ-5.13/5.16): the account-level
/// switch (<c>AccountEnabled</c>), whether the adapter has sandbox evidence for it (<c>AdapterVerified</c>,
/// REQ-5.11), and the backend-decided availability with the first blocking reason so the console never
/// hard-codes per-provider rules. Independent of the merchant-level policy, which is its own resource.</summary>
public sealed record PspConnectionMethodView(
    string Method,
    bool AccountEnabled,
    bool AdapterVerified,
    bool Available,
    string? Denial);

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
    bool HasPendingCredentialChange,
    DateTime CreatedAt,
    long Version,
    string Environment,
    string CredentialEnvironment,
    string CallbackUrl,
    PspCredentialTestView? PendingCredentialTest,
    WebhookRegistrationView WebhookRegistration,
    IReadOnlyList<PspConnectionMethodView> Methods);

/// <summary>The merchant-level payment environment (REQ-2.1) with its pending switch, if any. <c>Version</c>
/// is <c>Merchant.Version</c> — the ETag an environment-change request must present.</summary>
public sealed record MerchantPaymentSettingsView(
    Guid MerchantId,
    string Environment,
    string? PendingEnvironment,
    Guid? PendingApprovalId,
    DateTime UpdatedAt,
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

public sealed record GlobalPaymentCapabilityView(
    string Kind,
    string Code,
    string? Provider,
    string? Method,
    string? Option,
    bool Enabled,
    bool AdapterSupported,
    Guid? UpdatedBy,
    DateTime? UpdatedAt,
    long Version);

public sealed record AccountPaymentCapabilityView(
    string Kind,
    Guid PspConnectionId,
    Guid MerchantId,
    string Provider,
    string Method,
    string? Option,
    bool Enabled,
    Guid? UpdatedBy,
    DateTime? UpdatedAt,
    long Version,
    bool AdapterVerified,
    string? Denial);

public sealed record SetGlobalPaymentCapabilityIntent(
    string Code,
    string? Provider,
    string? Method,
    string? Option,
    bool Enabled,
    long ExpectedVersion,
    string IdempotencyKey,
    AdminPaymentsAccess Access);

public sealed record SetAccountPaymentCapabilityIntent(
    Guid PspConnectionId,
    string Method,
    string? Option,
    bool Enabled,
    long ExpectedVersion,
    string IdempotencyKey,
    AdminPaymentsAccess Access);

public sealed record PaymentCapabilityMutationResult<T>(T Value, bool Replayed);

/// <summary>The merchant-level policy for one method (REQ-5.13) plus the backend's effective decision:
/// <c>Effective</c> requires BOTH this policy and a qualifying provider-account method (REQ-5.15);
/// <c>Denial</c> names the first blocking reason (snake_case of <see cref="PaymentCapabilityDenial"/>).</summary>
public sealed record MerchantPaymentMethodView(
    Guid MerchantId,
    string Method,
    bool Enabled,
    bool Effective,
    Guid? UpdatedBy,
    DateTime? UpdatedAt,
    long Version,
    string? Denial);

public sealed record MerchantUserPaymentMethodView(
    Guid MerchantUserId,
    Guid MerchantId,
    string Method,
    bool Enabled,
    bool Effective,
    Guid? UpdatedBy,
    DateTime? UpdatedAt,
    long Version);

public sealed record UserPaymentMethodResolutionView(string Method, string Resolution);

public sealed record SetMerchantPaymentCapabilityIntent(
    Guid MerchantId,
    string Method,
    bool Enabled,
    long ExpectedVersion,
    string IdempotencyKey,
    AdminPaymentsAccess Access);

public sealed record SetMerchantUserPaymentCapabilityIntent(
    Guid MerchantId,
    Guid MerchantUserId,
    string Method,
    bool Enabled,
    long ExpectedVersion,
    string IdempotencyKey,
    AdminPaymentsAccess Access);

public interface IGlobalPaymentCapabilityControlStore
{
    Task<GlobalPaymentCapabilityView?> GetMethodAsync(
        string method, AdminPaymentsAccess access, CancellationToken cancellationToken);
    Task<GlobalPaymentCapabilityView?> GetProviderAsync(
        string provider, AdminPaymentsAccess access, CancellationToken cancellationToken);
    Task<GlobalPaymentCapabilityView?> GetProviderMethodAsync(
        string provider, string method, AdminPaymentsAccess access, CancellationToken cancellationToken);
    Task<GlobalPaymentCapabilityView?> GetProviderMethodOptionAsync(
        string provider, string method, string option, AdminPaymentsAccess access,
        CancellationToken cancellationToken);
    Task<PaymentCapabilityMutationResult<GlobalPaymentCapabilityView>> SetMethodAsync(
        SetGlobalPaymentCapabilityIntent intent, CancellationToken cancellationToken);
    Task<PaymentCapabilityMutationResult<GlobalPaymentCapabilityView>> SetProviderAsync(
        SetGlobalPaymentCapabilityIntent intent, CancellationToken cancellationToken);
    Task<PaymentCapabilityMutationResult<GlobalPaymentCapabilityView>> SetProviderMethodAsync(
        SetGlobalPaymentCapabilityIntent intent, CancellationToken cancellationToken);
    Task<PaymentCapabilityMutationResult<GlobalPaymentCapabilityView>> SetProviderMethodOptionAsync(
        SetGlobalPaymentCapabilityIntent intent, CancellationToken cancellationToken);
}

public interface IAccountPaymentCapabilityControlStore
{
    Task<AccountPaymentCapabilityView?> GetAccountMethodAsync(
        Guid connectionId, string method, AdminPaymentsAccess access, CancellationToken cancellationToken);
    Task<AccountPaymentCapabilityView?> GetAccountMethodOptionAsync(
        Guid connectionId, string method, string option, AdminPaymentsAccess access,
        CancellationToken cancellationToken);
    Task<PaymentCapabilityMutationResult<AccountPaymentCapabilityView>> SetAccountMethodAsync(
        SetAccountPaymentCapabilityIntent intent, CancellationToken cancellationToken);
    Task<PaymentCapabilityMutationResult<AccountPaymentCapabilityView>> SetAccountMethodOptionAsync(
        SetAccountPaymentCapabilityIntent intent, CancellationToken cancellationToken);
}

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

/// <summary>One simple per-method routing row as the general settings page edits it (REQ-6.19): a primary
/// connection and an optional fallback, never an amount, Originator or <c>any</c> predicate.</summary>
public sealed record SimpleRoutingRuleRow(
    string Method,
    Guid PrimaryConnectionId,
    Guid? FallbackConnectionId);

public sealed record SimpleRoutingRuleView(
    string Method,
    Guid PrimaryConnectionId,
    Guid? FallbackConnectionId);

/// <summary>The merchant's simple routing as the general settings page reads it. <c>AdvancedReadOnly</c> is
/// true when the active or any draft ruleset carries an amount, Originator or <c>any</c> predicate — the page
/// then renders the matrix read-only and cannot write (REQ-6.20). <c>RulesetId</c> is the draft the page
/// edits (null when none exists yet). <c>Version</c> is the ETag a PUT must present: the draft's version, or
/// 0 when no draft exists (REQ-9.4), mirroring the row-absent convention used across this store.</summary>
public sealed record SimpleRoutingView(
    Guid MerchantId,
    Guid? RulesetId,
    string Status,
    bool AdvancedReadOnly,
    IReadOnlyList<SimpleRoutingRuleView> Rules,
    long Version);

public sealed record SetSimpleRoutingIntent(
    Guid MerchantId,
    IReadOnlyList<SimpleRoutingRuleRow> Rules,
    long ExpectedVersion,
    string IdempotencyKey,
    AdminPaymentsAccess Access);

/// <summary>Narrow port for the general settings page's simple routing (design <c>ISimpleRoutingControlStore</c>):
/// it accepts only method/primary/fallback rows and refuses to create, replace or delete any ruleset that
/// carries an advanced predicate (REQ-6.19/6.20/6.21).</summary>
public interface ISimpleRoutingControlStore
{
    /// <summary>Null when the merchant does not exist OR is outside the admin's scope (REQ-1.4: 404 either way).</summary>
    Task<SimpleRoutingView?> GetSimpleRoutingAsync(
        Guid merchantId, AdminPaymentsAccess access, CancellationToken cancellationToken);
    Task<SimpleRoutingView> SetSimpleRoutingAsync(
        SetSimpleRoutingIntent intent, CancellationToken cancellationToken);
}

public interface IAdminPaymentsControlStore
{
    /// <summary>Null when the merchant does not exist OR is outside the admin's scope (REQ-1.4: 404 either way).</summary>
    Task<MerchantPaymentSettingsView?> GetMerchantPaymentSettingsAsync(
        Guid merchantId, AdminPaymentsAccess access, CancellationToken cancellationToken);
    Task<PagedResult<PspConnectionView>> ListConnectionsAsync(PspConnectionQuery query, CancellationToken cancellationToken);
    Task<PspConnectionView?> GetConnectionAsync(Guid connectionId, Guid? merchantId, AdminPaymentsAccess access, CancellationToken cancellationToken);
    Task<PspConnectionMutationResult> CreateConnectionAsync(CreatePspConnectionIntent intent, CancellationToken cancellationToken);
    Task<PspConnectionMutationResult> UpdateConnectionAsync(UpdatePspConnectionIntent intent, CancellationToken cancellationToken);
    Task<PspConnectionMutationResult> TestConnectionAsync(TestPspConnectionIntent intent, CancellationToken cancellationToken);
    Task<PspCredentialChangeResult> RequestCredentialChangeAsync(RequestPspCredentialChangeIntent intent, CancellationToken cancellationToken);

    Task<IReadOnlyList<EffectivePaymentMethod>?> ListMerchantMethodsAsync(
        Guid merchantId, AdminPaymentsAccess access, CancellationToken cancellationToken);
    Task<MerchantPaymentMethodView?> GetMerchantMethodAsync(
        Guid merchantId, string method, AdminPaymentsAccess access, CancellationToken cancellationToken);
    Task<PaymentCapabilityMutationResult<MerchantPaymentMethodView>> SetMerchantMethodAsync(
        SetMerchantPaymentCapabilityIntent intent, CancellationToken cancellationToken);
    Task<IReadOnlyList<MerchantUserPaymentMethodView>?> ListMerchantUserMethodsAsync(
        Guid merchantId, Guid merchantUserId, AdminPaymentsAccess access, CancellationToken cancellationToken);
    Task<MerchantUserPaymentMethodView?> GetMerchantUserMethodAsync(
        Guid merchantId, Guid merchantUserId, string method, AdminPaymentsAccess access,
        CancellationToken cancellationToken);
    Task<PaymentCapabilityMutationResult<MerchantUserPaymentMethodView>> SetMerchantUserMethodAsync(
        SetMerchantUserPaymentCapabilityIntent intent, CancellationToken cancellationToken);
    Task<UserPaymentMethodResolutionView?> ResolveMerchantUserMethodAsync(
        Guid merchantId, Guid merchantUserId, string method, AdminPaymentsAccess access,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<EffectivePaymentOption>?> ResolveMerchantUserOptionsAsync(
        Guid merchantId, Guid merchantUserId, string method, string provider,
        AdminPaymentsAccess access, CancellationToken cancellationToken);

    Task<PagedResult<RoutingRulesetView>> ListRulesetsAsync(RoutingRulesetQuery query, CancellationToken cancellationToken);
    Task<RoutingRulesetView?> GetRulesetAsync(Guid rulesetId, Guid? merchantId, AdminPaymentsAccess access, CancellationToken cancellationToken);
    Task<RoutingRulesetView> CreateRulesetAsync(CreateRoutingRulesetIntent intent, CancellationToken cancellationToken);
    Task<RoutingRulesetView> ReplaceRulesetAsync(ReplaceRoutingRulesetIntent intent, CancellationToken cancellationToken);
    Task DeleteRulesetAsync(Guid rulesetId, Guid merchantId, long expectedVersion, AdminPaymentsAccess access, CancellationToken cancellationToken);
    Task<RoutingActivationResult> RequestActivationAsync(RequestRoutingActivationIntent intent, CancellationToken cancellationToken);
}
