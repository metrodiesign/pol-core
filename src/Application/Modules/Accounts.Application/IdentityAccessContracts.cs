using Access.Domain;
using Accounts.Domain;

namespace Accounts.Application;

public sealed record VerifiedHumanIdentity(
    ExternalIdentity Identity,
    string Issuer,
    string Audience,
    bool SignatureValidated,
    bool LifetimeValidated,
    bool StateValidated,
    bool NonceValidated,
    bool WorkforceEligible,
    string? Email,
    string? DisplayName);

public sealed record IdentityValidationResult(bool IsValid, string? Code)
{
    public static IdentityValidationResult Valid() => new(true, null);
    public static IdentityValidationResult Invalid(string code) => new(false, code);
}

/// <summary>Protocol gate before any identity lookup or JIT write.</summary>
public static class HumanIdentityPolicy
{
    public static IdentityValidationResult Validate(
        VerifiedHumanIdentity identity, string expectedIssuer, string expectedTenantId, string expectedAudience)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!string.Equals(identity.Identity.Provider, "microsoft", StringComparison.OrdinalIgnoreCase))
            return IdentityValidationResult.Invalid("provider_not_allowed");
        if (!string.Equals(identity.Identity.TenantId, expectedTenantId, StringComparison.Ordinal))
            return IdentityValidationResult.Invalid("tenant_mismatch");
        if (!string.Equals(identity.Issuer, expectedIssuer, StringComparison.Ordinal))
            return IdentityValidationResult.Invalid("issuer_mismatch");
        if (!string.Equals(identity.Audience, expectedAudience, StringComparison.Ordinal))
            return IdentityValidationResult.Invalid("audience_mismatch");
        if (!identity.SignatureValidated)
            return IdentityValidationResult.Invalid("signature_invalid");
        if (!identity.LifetimeValidated)
            return IdentityValidationResult.Invalid("lifetime_invalid");
        if (!identity.StateValidated)
            return IdentityValidationResult.Invalid("state_invalid");
        if (!identity.NonceValidated)
            return IdentityValidationResult.Invalid("nonce_invalid");
        return IdentityValidationResult.Valid();
    }
}

public sealed record EmployeeJitResult(
    Guid AccountId,
    bool Created,
    bool HasPlatformAccess,
    bool HasMerchantAccess,
    long AuthorizationVersion);

public interface IEmployeeJitStore
{
    Task<EmployeeJitResult> GetOrCreateAsync(
        VerifiedHumanIdentity identity, CancellationToken cancellationToken);
}

public sealed class EmployeeJitService(IEmployeeJitStore store)
{
    public async Task<EmployeeJitResult> ResolveAsync(
        VerifiedHumanIdentity identity,
        string expectedIssuer,
        string expectedTenantId,
        string expectedAudience,
        CancellationToken cancellationToken)
    {
        var validation = HumanIdentityPolicy.Validate(identity, expectedIssuer, expectedTenantId, expectedAudience);
        if (!validation.IsValid)
            throw new IdentityAccessException(validation.Code!, "Human identity validation failed.");
        if (!identity.WorkforceEligible)
            throw new IdentityAccessException("workforce_not_eligible", "The identity is not eligible for Employee access.");
        return await store.GetOrCreateAsync(identity, cancellationToken);
    }
}

public interface IRegistrationSessionStore
{
    Task<bool> HasApprovedAccountAsync(ExternalIdentity identity, CancellationToken cancellationToken);

    Task<RegistrationSession> IssueAsync(
        ExternalIdentity identity, Guid merchantId, byte[] sessionReferenceHash, DateTime now, TimeSpan lifetime,
        CancellationToken cancellationToken);
}

public sealed record RegistrationSessionIssue(RegistrationSession Session, string RawReference);

public sealed class RegistrationSessionService(IRegistrationSessionStore store)
{
    public async Task<RegistrationSessionIssue> StartAsync(
        VerifiedHumanIdentity identity, Guid merchantId, DateTime now, TimeSpan lifetime,
        string expectedIssuer, string expectedTenantId, string expectedAudience,
        CancellationToken cancellationToken)
    {
        var validation = HumanIdentityPolicy.Validate(
            identity, expectedIssuer, expectedTenantId, expectedAudience);
        if (!validation.IsValid)
            throw new IdentityAccessException(validation.Code!, "Human identity validation failed.");
        if (identity.WorkforceEligible)
            throw new IdentityAccessException("registration_identity_not_agent", "Workforce identities cannot use agent registration.");
        if (await store.HasApprovedAccountAsync(identity.Identity, cancellationToken))
            throw new IdentityAccessException("account_already_approved", "The identity already has an approved account.");
        var rawReference = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var referenceHash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(rawReference));
        var session = await store.IssueAsync(
            identity.Identity, merchantId, referenceHash, now, lifetime, cancellationToken);
        return new RegistrationSessionIssue(session, rawReference);
    }
}

public sealed class IdentityAccessException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record ClientAssertion(
    string ApplicationId,
    string Issuer,
    string KeyId,
    string Algorithm,
    string Jti,
    DateTime IssuedAt,
    DateTime ExpiresAt,
    string Audience);

public sealed record AssertionValidationResult(bool IsValid, string? Code)
{
    public static AssertionValidationResult Valid() => new(true, null);
    public static AssertionValidationResult Invalid(string code) => new(false, code);
}

public interface IAssertionReplayStore
{
    Task<bool> TryClaimAsync(string applicationId, string jti, DateTime expiresAt, CancellationToken cancellationToken);
}

/// <summary>Small policy around OpenIddict's protocol validation; it owns no token/code table.</summary>
public sealed class SystemClientAssertionService(IAssertionReplayStore replayStore)
{
    public async Task<AssertionValidationResult> ValidateAsync(
        Account account,
        SystemClient client,
        ClientKeyPolicy key,
        ClientAssertion assertion,
        DateTime now,
        string tokenEndpoint,
        TimeSpan clockSkew,
        TimeSpan maxLifetime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.Status != AccountStatus.Active)
            return AssertionValidationResult.Invalid("account_disabled");
        return await ValidateAsync(
            client, key, assertion, now, tokenEndpoint, clockSkew, maxLifetime, cancellationToken);
    }

    public async Task<AssertionValidationResult> ValidateAsync(
        SystemClient client,
        ClientKeyPolicy key,
        ClientAssertion assertion,
        DateTime now,
        string tokenEndpoint,
        TimeSpan clockSkew,
        TimeSpan maxLifetime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(assertion);

        if (client.Status != SystemClientStatus.Active || !client.AllowsGrant("client_credentials"))
            return AssertionValidationResult.Invalid("client_disabled");
        if (!key.IsUsable(now))
            return AssertionValidationResult.Invalid("key_inactive");
        if (!string.Equals(assertion.ApplicationId, key.ApplicationId, StringComparison.Ordinal)
            || !string.Equals(assertion.Issuer, assertion.ApplicationId, StringComparison.Ordinal)
            || !string.Equals(assertion.KeyId, key.KeyId, StringComparison.Ordinal)
            || !string.Equals(assertion.Algorithm, key.Algorithm, StringComparison.Ordinal))
            return AssertionValidationResult.Invalid("key_mismatch");
        if (!string.Equals(assertion.Audience, tokenEndpoint, StringComparison.Ordinal))
            return AssertionValidationResult.Invalid("audience_mismatch");
        if (assertion.ExpiresAt <= assertion.IssuedAt
            || assertion.ExpiresAt - assertion.IssuedAt > maxLifetime
            || assertion.IssuedAt > now + clockSkew
            || assertion.ExpiresAt < now - clockSkew)
            return AssertionValidationResult.Invalid("assertion_expired");
        if (string.IsNullOrWhiteSpace(assertion.Jti))
            return AssertionValidationResult.Invalid("jti_required");
        if (!await replayStore.TryClaimAsync(
                assertion.ApplicationId, assertion.Jti, assertion.ExpiresAt, cancellationToken))
            return AssertionValidationResult.Invalid("assertion_replayed");
        return AssertionValidationResult.Valid();
    }
}

public interface IBffSessionStore
{
    Task<BffSessionTicket?> FindByHashAsync(byte[] ticketKeyHash, CancellationToken cancellationToken);
    void Add(BffSessionTicket ticket);
    Task RevokeAsync(BffSessionTicket ticket, DateTime now, CancellationToken cancellationToken);
    Task ReplaceAsync(BffSessionTicket current, BffSessionTicket replacement, DateTime now,
        CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IRegistrationSessionLookup
{
    Task<RegistrationSession?> FindByHashAsync(byte[] sessionReferenceHash, CancellationToken cancellationToken);
}

public sealed record SystemClientResolution(
    Account Account,
    SystemClient Client,
    IReadOnlyList<ClientKeyPolicy> KeyPolicies,
    IReadOnlyList<string> Scopes);

public interface IIdentityAccessQuery
{
    Task<Account?> FindAccountAsync(Guid accountId, CancellationToken cancellationToken);

    Task<SystemClientResolution?> FindSystemClientAsync(
        string clientId, CancellationToken cancellationToken);

    Task<AuthorizationSnapshot?> ResolveAuthorizationAsync(
        Guid accountId, Guid? merchantId, Guid? clientId, CancellationToken cancellationToken);

    Task<IReadOnlyList<MerchantAccessSummary>> ListMerchantAccessAsync(
        Guid accountId, CancellationToken cancellationToken);
}

public sealed record MerchantAccessSummary(
    Guid Id,
    Guid MerchantId,
    DataScope DataScope,
    AccessStatus Status,
    IReadOnlySet<Guid> RoleIds,
    IReadOnlySet<Guid> BranchIds);

public sealed record AuthorizationSnapshot(
    Guid AccountId,
    AccountType AccountType,
    AccountStatus AccountStatus,
    long AuthorizationVersion,
    Guid? MerchantId,
    DataScope? DataScope,
    Guid? AgentSaleId,
    Guid? HomeBranchId,
    IReadOnlySet<Guid> BranchIds,
    IReadOnlySet<Guid> RoleIds,
    bool HasPlatformAccess,
    IReadOnlySet<string> Permissions,
    Guid? ClientId);

public enum AuthorizationDecisionReason
{
    Allowed,
    InactiveAccount,
    MissingMerchantContext,
    MerchantMismatch,
    MissingAccess,
    OwnershipMismatch,
    SelfNotSupported,
    BranchNotAllowed,
    PermissionMissing,
    GrantExceedsCaller,
    InvalidTarget,
    StaleAuthorization,
}

public sealed record AuthorizationDecision(bool Allowed, AuthorizationDecisionReason Reason)
{
    public static AuthorizationDecision Allow() => new(true, AuthorizationDecisionReason.Allowed);
    public static AuthorizationDecision Deny(AuthorizationDecisionReason reason) => new(false, reason);
}

/// <summary>Pure, shared policy used by every Account/Access protected path.</summary>
public static class AccessEvaluator
{
    public static AuthorizationDecision VerifyTokenVersion(long tokenVersion, long databaseVersion) =>
        tokenVersion == databaseVersion
            ? AuthorizationDecision.Allow()
            : AuthorizationDecision.Deny(AuthorizationDecisionReason.StaleAuthorization);

    public static AuthorizationDecision CanReadOrder(
        AuthorizationSnapshot snapshot,
        Guid merchantId,
        Guid? ownerSaleId,
        Guid? ownerBranchId)
    {
        if (snapshot.AccountStatus != AccountStatus.Active)
            return AuthorizationDecision.Deny(AuthorizationDecisionReason.InactiveAccount);
        if (snapshot.MerchantId is null || snapshot.DataScope is null)
            return AuthorizationDecision.Deny(AuthorizationDecisionReason.MissingMerchantContext);
        if (snapshot.MerchantId != merchantId)
            return AuthorizationDecision.Deny(AuthorizationDecisionReason.MerchantMismatch);

        return snapshot.DataScope.Value switch
        {
            DataScope.Merchant => AuthorizationDecision.Allow(),
            DataScope.Self when snapshot.AccountType == AccountType.Agent
                && snapshot.AgentSaleId is not null && ownerSaleId == snapshot.AgentSaleId
                => AuthorizationDecision.Allow(),
            DataScope.Self => AuthorizationDecision.Deny(AuthorizationDecisionReason.SelfNotSupported),
            DataScope.Branch when snapshot.BranchIds.Count == 1 && ownerBranchId is not null
                && snapshot.BranchIds.Contains(ownerBranchId.Value)
                => AuthorizationDecision.Allow(),
            DataScope.AssignedBranches when ownerBranchId is not null
                && snapshot.BranchIds.Contains(ownerBranchId.Value)
                => AuthorizationDecision.Allow(),
            _ => AuthorizationDecision.Deny(AuthorizationDecisionReason.OwnershipMismatch),
        };
    }

    public static AuthorizationDecision CanAssign(
        AuthorizationSnapshot grantor,
        AccountType targetAccountType,
        Guid merchantId,
        DataScope dataScope,
        IEnumerable<Guid> branchIds,
        IEnumerable<Guid> roleIds)
    {
        if (grantor.AccountStatus != AccountStatus.Active)
            return AuthorizationDecision.Deny(AuthorizationDecisionReason.InactiveAccount);
        if (!grantor.Permissions.Contains("access.manage", StringComparer.Ordinal))
            return AuthorizationDecision.Deny(AuthorizationDecisionReason.PermissionMissing);
        if (dataScope == DataScope.Self && targetAccountType != AccountType.Agent)
            return AuthorizationDecision.Deny(AuthorizationDecisionReason.SelfNotSupported);
        if (grantor.MerchantId != merchantId && !grantor.HasPlatformAccess)
            return AuthorizationDecision.Deny(AuthorizationDecisionReason.MerchantMismatch);

        var targetBranches = branchIds.ToHashSet();
        var targetRoles = roleIds.ToHashSet();
        if (dataScope is DataScope.Branch or DataScope.AssignedBranches && targetBranches.Count == 0)
            return AuthorizationDecision.Deny(AuthorizationDecisionReason.BranchNotAllowed);
        if (dataScope == DataScope.Merchant && targetBranches.Count > 0)
            return AuthorizationDecision.Deny(AuthorizationDecisionReason.BranchNotAllowed);
        if (!grantor.HasPlatformAccess
            && (!targetBranches.IsSubsetOf(grantor.BranchIds) || !targetRoles.IsSubsetOf(grantor.RoleIds)))
            return AuthorizationDecision.Deny(AuthorizationDecisionReason.GrantExceedsCaller);
        return AuthorizationDecision.Allow();
    }

    /// <summary>Checks the soft references that cannot be represented as cross-module foreign keys in the
    /// control-plane schema. Every role, branch and (for an Agent) Sale reference must resolve to the same
    /// Merchant as the access row before the write is allowed.</summary>
    public static AuthorizationDecision ValidateReferenceMerchants(
        Guid accessMerchantId,
        IEnumerable<Guid> roleMerchantIds,
        IEnumerable<Guid> branchMerchantIds,
        Guid? saleMerchantId = null)
    {
        if (accessMerchantId == Guid.Empty)
            return AuthorizationDecision.Deny(AuthorizationDecisionReason.InvalidTarget);
        if (roleMerchantIds.Any(merchantId => merchantId != accessMerchantId)
            || branchMerchantIds.Any(merchantId => merchantId != accessMerchantId)
            || saleMerchantId is { } saleMerchant && saleMerchant != accessMerchantId)
            return AuthorizationDecision.Deny(AuthorizationDecisionReason.MerchantMismatch);
        return AuthorizationDecision.Allow();
    }

    public static bool CanUsePlatform(AuthorizationSnapshot snapshot) =>
        snapshot.AccountType == AccountType.Employee
        && snapshot.AccountStatus == AccountStatus.Active
        && snapshot.HasPlatformAccess;
}
