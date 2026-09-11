using SharedKernel;

namespace Accounts.Domain;

public enum AccountType
{
    Employee = 1,
    Agent = 2,
    System = 3,
}

public enum AccountStatus
{
    Active = 1,
    Suspended = 2,
}

/// <summary>Stable external identity key. Email is deliberately absent: it is mutable contact data.</summary>
public readonly record struct ExternalIdentity(string Provider, string TenantId, string ExternalUserId)
{
    public static ExternalIdentity Create(string provider, string tenantId, string externalUserId)
    {
        return new(
            Required(provider, nameof(provider), 64),
            Required(tenantId, nameof(tenantId), 128),
            Required(externalUserId, nameof(externalUserId), 256));
    }

    private static string Required(string value, string parameter, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        var normalized = value.Trim();
        if (normalized.Length > maxLength)
            throw new ArgumentException($"{parameter} exceeds {maxLength} characters.", parameter);
        return normalized;
    }
}

/// <summary>One business account shared by all token and access decisions.</summary>
public sealed class Account : AggregateRoot<Guid>
{
    public AccountType AccountType { get; private set; }
    public string DisplayName { get; private set; } = default!;
    public AccountStatus Status { get; private set; }
    public long AuthorizationVersion { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private Account() { }

    private Account(Guid id, AccountType accountType, string displayName, DateTime now) : base(id)
    {
        AccountType = accountType;
        DisplayName = displayName;
        Status = AccountStatus.Active;
        AuthorizationVersion = 0;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public static Account Create(AccountType accountType, string displayName, DateTime now)
    {
        if (!Enum.IsDefined(accountType))
            throw new ArgumentOutOfRangeException(nameof(accountType));
        return new Account(Guid.CreateVersion7(), accountType, Required(displayName, nameof(displayName), 200), now);
    }

    public void Suspend(DateTime now)
    {
        if (Status == AccountStatus.Suspended)
            return;
        Status = AccountStatus.Suspended;
        BumpAuthorizationVersion(now);
    }

    public void Reactivate(DateTime now)
    {
        if (Status == AccountStatus.Active)
            return;
        Status = AccountStatus.Active;
        BumpAuthorizationVersion(now);
    }

    public void Rename(string displayName, DateTime now)
    {
        DisplayName = Required(displayName, nameof(displayName), 200);
        UpdatedAt = now;
    }

    public void BumpAuthorizationVersion(DateTime now)
    {
        AuthorizationVersion++;
        UpdatedAt = now;
    }

    private static string Required(string value, string parameter, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        var normalized = value.Trim();
        if (normalized.Length > maxLength)
            throw new ArgumentException($"{parameter} exceeds {maxLength} characters.", parameter);
        return normalized;
    }
}

/// <summary>Provider observation for an Account. The identity tuple is immutable.</summary>
public sealed class LoginAccount : Entity<Guid>
{
    public Guid AccountId { get; private set; }
    public string Provider { get; private set; } = default!;
    public string TenantId { get; private set; } = default!;
    public string ExternalUserId { get; private set; } = default!;
    public string? Email { get; private set; }
    public string? DisplayName { get; private set; }
    public DateTime? LastLoginAt { get; private set; }

    private LoginAccount() { }

    private LoginAccount(Guid id, Guid accountId, ExternalIdentity identity, string? email, string? displayName,
        DateTime now) : base(id)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("AccountId is required.", nameof(accountId));
        AccountId = accountId;
        Provider = identity.Provider;
        TenantId = identity.TenantId;
        ExternalUserId = identity.ExternalUserId;
        Email = Optional(email, 320);
        DisplayName = Optional(displayName, 200);
        LastLoginAt = now;
    }

    public static LoginAccount Create(Guid accountId, ExternalIdentity identity, string? email, string? displayName,
        DateTime now) => new(Guid.CreateVersion7(), accountId, identity, email, displayName, now);

    public ExternalIdentity Identity => new(Provider, TenantId, ExternalUserId);

    public void Observe(string? email, string? displayName, DateTime now)
    {
        Email = Optional(email, 320);
        DisplayName = Optional(displayName, 200);
        LastLoginAt = now;
    }

    private static string? Optional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        return normalized.Length <= maxLength
            ? normalized
            : throw new ArgumentException($"Value exceeds {maxLength} characters.", nameof(value));
    }
}

public sealed class Employee : Entity<Guid>
{
    public Guid AccountId { get; private set; }
    public string? EmployeeCode { get; private set; }
    public string? DepartmentCode { get; private set; }
    public string Metadata { get; private set; } = "{}";

    private Employee() { }

    private Employee(Guid accountId, string? employeeCode, string? departmentCode, string metadata) : base(accountId)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("AccountId is required.", nameof(accountId));
        AccountId = accountId;
        EmployeeCode = Optional(employeeCode, 128);
        DepartmentCode = Optional(departmentCode, 128);
        Metadata = metadata;
    }

    public static Employee Create(Guid accountId, string? employeeCode, string? departmentCode, string? metadata = null) =>
        new(accountId, employeeCode, departmentCode, string.IsNullOrWhiteSpace(metadata) ? "{}" : metadata);

    private static string? Optional(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim() is var result && result.Length <= maxLength
            ? result
            : throw new ArgumentException($"Value exceeds {maxLength} characters.", nameof(value));
}

public sealed class Agent : Entity<Guid>
{
    public Guid AccountId { get; private set; }
    public Guid MerchantId { get; private set; }
    public Guid SaleId { get; private set; }
    public string Metadata { get; private set; } = "{}";

    private Agent() { }

    private Agent(Guid accountId, Guid merchantId, Guid saleId, string metadata) : base(accountId)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("AccountId is required.", nameof(accountId));
        if (saleId == Guid.Empty)
            throw new ArgumentException("SaleId is required.", nameof(saleId));
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        AccountId = accountId;
        MerchantId = merchantId;
        SaleId = saleId;
        Metadata = metadata;
    }

    public static Agent Create(Guid accountId, Guid merchantId, Guid saleId, string? metadata = null) =>
        new(accountId, merchantId, saleId, string.IsNullOrWhiteSpace(metadata) ? "{}" : metadata);

    public static Agent Create(Guid accountId, Guid saleId, string? metadata = null) =>
        throw new ArgumentException("MerchantId is required for an Agent assignment.", nameof(accountId));
}

public enum SystemClientStatus
{
    Active = 1,
    Suspended = 2,
}

/// <summary>Protocol client binding. Merchant and environment are immutable after creation.</summary>
public sealed class SystemClient : Entity<Guid>
{
    public Guid AccountId { get; private set; }
    public string ClientId { get; private set; } = default!;
    public Guid MerchantId { get; private set; }
    public string Environment { get; private set; } = default!;
    public SystemClientStatus Status { get; private set; }
    public string AllowedGrantTypes { get; private set; } = "client_credentials";
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private SystemClient() { }

    private SystemClient(Guid id, Guid accountId, string clientId, Guid merchantId, string environment,
        IEnumerable<string> allowedGrantTypes, DateTime now) : base(id)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("AccountId is required.", nameof(accountId));
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        AccountId = accountId;
        ClientId = Required(clientId, nameof(clientId), 128);
        MerchantId = merchantId;
        Environment = Required(environment, nameof(environment), 32);
        AllowedGrantTypes = JoinGrantTypes(allowedGrantTypes);
        Status = SystemClientStatus.Active;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public static SystemClient Create(Guid accountId, string clientId, Guid merchantId, string environment,
        IEnumerable<string> allowedGrantTypes, DateTime now) =>
        new(Guid.CreateVersion7(), accountId, clientId, merchantId, environment, allowedGrantTypes, now);

    public IReadOnlyList<string> GrantTypes() =>
        AllowedGrantTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void Suspend(DateTime now)
    {
        if (Status == SystemClientStatus.Suspended)
            return;
        Status = SystemClientStatus.Suspended;
        UpdatedAt = now;
    }

    public void Reactivate(DateTime now)
    {
        if (Status == SystemClientStatus.Active)
            return;
        Status = SystemClientStatus.Active;
        UpdatedAt = now;
    }

    public bool AllowsGrant(string grantType) =>
        GrantTypes().Contains(grantType, StringComparer.Ordinal);

    private static string JoinGrantTypes(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var grants = values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        if (grants.Length == 0)
            throw new ArgumentException("At least one grant type is required.", nameof(values));
        if (grants.Any(value => value is not "client_credentials"))
            throw new ArgumentException("SYSTEM clients only support client_credentials.", nameof(values));
        return string.Join(',', grants);
    }

    private static string Required(string value, string parameter, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        var normalized = value.Trim();
        if (normalized.Length > maxLength)
            throw new ArgumentException($"{parameter} exceeds {maxLength} characters.", parameter);
        return normalized;
    }
}

public enum KeyPolicyStatus
{
    Active = 1,
    Revoked = 2,
}

/// <summary>Metadata policy for a key kept in OpenIddict's application JWK set.</summary>
public sealed class ClientKeyPolicy : Entity<Guid>
{
    public Guid SystemClientId { get; private set; }
    public string ApplicationId { get; private set; } = default!;
    public string KeyId { get; private set; } = default!;
    public string Algorithm { get; private set; } = default!;
    public DateTime ValidFrom { get; private set; }
    public DateTime? ValidUntil { get; private set; }
    public KeyPolicyStatus Status { get; private set; }
    public string? AuditReference { get; private set; }

    private ClientKeyPolicy() { }

    private ClientKeyPolicy(Guid id, Guid systemClientId, string applicationId, string keyId, string algorithm,
        DateTime validFrom, DateTime? validUntil, string? auditReference) : base(id)
    {
        if (systemClientId == Guid.Empty)
            throw new ArgumentException("SystemClientId is required.", nameof(systemClientId));
        if (validUntil is not null && validUntil <= validFrom)
            throw new ArgumentException("ValidUntil must be after ValidFrom.", nameof(validUntil));
        if (algorithm is not ("RS256" or "PS256"))
            throw new ArgumentException("Only RS256 and PS256 client assertion algorithms are allowed.", nameof(algorithm));
        SystemClientId = systemClientId;
        ApplicationId = Required(applicationId, nameof(applicationId), 128);
        KeyId = Required(keyId, nameof(keyId), 128);
        Algorithm = Required(algorithm, nameof(algorithm), 32);
        ValidFrom = validFrom;
        ValidUntil = validUntil;
        AuditReference = Optional(auditReference, 256);
        Status = KeyPolicyStatus.Active;
    }

    public static ClientKeyPolicy Create(Guid systemClientId, string applicationId, string keyId, string algorithm,
        DateTime validFrom, DateTime? validUntil, string? auditReference = null) =>
        new(Guid.CreateVersion7(), systemClientId, applicationId, keyId, algorithm, validFrom, validUntil,
            auditReference);

    public bool IsUsable(DateTime now) =>
        Status == KeyPolicyStatus.Active && now >= ValidFrom && (ValidUntil is null || now < ValidUntil);

    public void Revoke() => Status = KeyPolicyStatus.Revoked;

    private static string Required(string value, string parameter, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        var normalized = value.Trim();
        if (normalized.Length > maxLength)
            throw new ArgumentException($"{parameter} exceeds {maxLength} characters.", parameter);
        return normalized;
    }

    private static string? Optional(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim() is var result && result.Length <= maxLength
            ? result
            : throw new ArgumentException($"Value exceeds {maxLength} characters.", nameof(value));
}

/// <summary>One-time assertion replay marker. The unique (ApplicationId, Jti) is the security boundary.</summary>
public sealed class AssertionReplay : Entity<Guid>
{
    public string ApplicationId { get; private set; } = default!;
    public string Jti { get; private set; } = default!;
    public DateTime ExpiresAt { get; private set; }
    public DateTime ConsumedAt { get; private set; }

    private AssertionReplay() { }

    private AssertionReplay(Guid id, string applicationId, string jti, DateTime expiresAt, DateTime consumedAt)
        : base(id)
    {
        ApplicationId = applicationId;
        Jti = jti;
        ExpiresAt = expiresAt;
        ConsumedAt = consumedAt;
    }

    public static AssertionReplay Create(string applicationId, string jti, DateTime expiresAt, DateTime consumedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jti);
        if (expiresAt <= consumedAt)
            throw new ArgumentException("Assertion expiry must be after claim time.", nameof(expiresAt));
        return new AssertionReplay(Guid.CreateVersion7(), applicationId.Trim(), jti.Trim(), expiresAt, consumedAt);
    }
}

/// <summary>Server-side BFF ticket. Raw access/refresh tokens only exist inside protected ticket material.</summary>
public sealed class BffSessionTicket : Entity<Guid>
{
    public byte[] TicketKeyHash { get; private set; } = default!;
    public Guid AccountId { get; private set; }
    public string? ClientId { get; private set; }
    public string ProtectedAuthenticationTicket { get; private set; } = default!;
    public long AuthorizationVersion { get; private set; }
    public DateTime IssuedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }

    private BffSessionTicket() { }

    private BffSessionTicket(Guid id, byte[] ticketKeyHash, Guid accountId, string? clientId,
        string protectedAuthenticationTicket, long authorizationVersion, DateTime issuedAt, DateTime expiresAt)
        : base(id)
    {
        RequireHash(ticketKeyHash);
        if (accountId == Guid.Empty)
            throw new ArgumentException("AccountId is required.", nameof(accountId));
        if (expiresAt <= issuedAt)
            throw new ArgumentException("ExpiresAt must be after IssuedAt.", nameof(expiresAt));
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedAuthenticationTicket);
        TicketKeyHash = ticketKeyHash.ToArray();
        AccountId = accountId;
        ClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim();
        ProtectedAuthenticationTicket = protectedAuthenticationTicket;
        AuthorizationVersion = authorizationVersion;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
    }

    public static BffSessionTicket Create(byte[] ticketKeyHash, Guid accountId, string? clientId,
        string protectedAuthenticationTicket, long authorizationVersion, DateTime issuedAt, DateTime expiresAt) =>
        new(Guid.CreateVersion7(), ticketKeyHash, accountId, clientId, protectedAuthenticationTicket,
            authorizationVersion, issuedAt, expiresAt);

    public bool IsLiveAt(DateTime now, long currentAuthorizationVersion) =>
        RevokedAt is null && now < ExpiresAt && currentAuthorizationVersion == AuthorizationVersion;

    public void Revoke(DateTime now) => RevokedAt ??= now;

    private static void RequireHash(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 32)
            throw new ArgumentException("TicketKeyHash must be a 32-byte SHA-256 digest.", nameof(value));
    }
}

public enum RegistrationSessionStatus
{
    Active = 1,
    Revoked = 2,
}

/// <summary>Short-lived pre-account capability for an agent registration.</summary>
public sealed class RegistrationSession : Entity<Guid>
{
    public byte[] SessionReferenceHash { get; private set; } = default!;
    public string Provider { get; private set; } = default!;
    public string TenantId { get; private set; } = default!;
    public string ExternalUserId { get; private set; } = default!;
    public Guid MerchantId { get; private set; }
    public DateTime IssuedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public RegistrationSessionStatus Status { get; private set; }

    private RegistrationSession() { }

    private RegistrationSession(Guid id, byte[] sessionReferenceHash, ExternalIdentity identity, Guid merchantId,
        DateTime issuedAt, DateTime expiresAt) : base(id)
    {
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        if (expiresAt <= issuedAt)
            throw new ArgumentException("ExpiresAt must be after IssuedAt.", nameof(expiresAt));
        if (sessionReferenceHash.Length != 32)
            throw new ArgumentException("SessionReferenceHash must be a 32-byte SHA-256 digest.", nameof(sessionReferenceHash));
        SessionReferenceHash = sessionReferenceHash.ToArray();
        Provider = identity.Provider;
        TenantId = identity.TenantId;
        ExternalUserId = identity.ExternalUserId;
        MerchantId = merchantId;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
        Status = RegistrationSessionStatus.Active;
    }

    public static RegistrationSession Issue(byte[] sessionReferenceHash, ExternalIdentity identity, Guid merchantId,
        DateTime issuedAt, TimeSpan lifetime) =>
        new(Guid.CreateVersion7(), sessionReferenceHash, identity, merchantId, issuedAt, issuedAt + lifetime);

    public bool IsLiveAt(DateTime now) => Status == RegistrationSessionStatus.Active && now < ExpiresAt;

    public void Revoke() => Status = RegistrationSessionStatus.Revoked;
}
