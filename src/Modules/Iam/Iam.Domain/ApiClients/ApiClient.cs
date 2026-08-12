namespace Iam.Domain.ApiClients;

public enum ApiClientStatus { Active = 1, Revoked = 2 }
public enum SecretTicketStatus { Pending = 1, Ready = 2, Consumed = 3, Rejected = 4 }

public sealed class ApiClient
{
    public Guid Id { get; private set; }
    public string PublicClientId { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public Guid MerchantId { get; private set; }
    public Guid? OriginatorId { get; private set; }
    public string ScopesCsv { get; private set; } = default!;
    public string? IpPolicy { get; private set; }
    public byte[] SecretHash { get; private set; } = [];
    public string SecretHint { get; private set; } = default!;
    public ApiClientStatus Status { get; private set; }
    public Guid? PendingRotationApprovalId { get; private set; }
    public Guid? PendingRotationTicketId { get; private set; }
    public DateTime? LastUsedAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public long Version { get; private set; }

    private ApiClient() { }

    public static ApiClient Create(string publicClientId, string name, Guid merchantId, Guid? originatorId,
        IReadOnlyCollection<string> scopes, string? ipPolicy, byte[] secretHash, string secretHint, DateTime now)
    {
        if (merchantId == Guid.Empty) throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        ArgumentException.ThrowIfNullOrWhiteSpace(publicClientId);
        Validate(name, scopes);
        return new ApiClient
        {
            Id = Guid.NewGuid(),
            PublicClientId = publicClientId.Trim(),
            Name = name.Trim(),
            MerchantId = merchantId,
            OriginatorId = originatorId,
            ScopesCsv = Join(scopes),
            IpPolicy = Normalize(ipPolicy),
            SecretHash = secretHash,
            SecretHint = secretHint,
            Status = ApiClientStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
    }

    public void Update(string name, IReadOnlyCollection<string> scopes, string? ipPolicy, DateTime now)
    {
        Validate(name, scopes);
        if (Status == ApiClientStatus.Revoked) throw new InvalidOperationException("Revoked API client is immutable.");
        Name = name.Trim();
        ScopesCsv = Join(scopes);
        IpPolicy = Normalize(ipPolicy);
        UpdatedAt = now;
        Version++;
    }

    public void RequestRotation(Guid approvalId, Guid ticketId, DateTime now)
    {
        if (Status != ApiClientStatus.Active) throw new InvalidOperationException("Only active API clients can rotate secrets.");
        if (PendingRotationApprovalId.HasValue) throw new InvalidOperationException("A secret rotation is already pending.");
        PendingRotationApprovalId = approvalId;
        PendingRotationTicketId = ticketId;
        UpdatedAt = now;
        Version++;
    }

    public void CompleteRotation(Guid approvalId, byte[] hash, string hint, DateTime now)
    {
        EnsurePending(approvalId);
        SecretHash = hash;
        SecretHint = hint;
        PendingRotationApprovalId = null;
        PendingRotationTicketId = null;
        UpdatedAt = now;
        Version++;
    }

    public void RejectRotation(Guid approvalId, DateTime now)
    {
        EnsurePending(approvalId);
        PendingRotationApprovalId = null;
        PendingRotationTicketId = null;
        UpdatedAt = now;
        Version++;
    }

    public void Revoke(DateTime now)
    {
        if (Status == ApiClientStatus.Revoked) return;
        Status = ApiClientStatus.Revoked;
        PendingRotationApprovalId = null;
        PendingRotationTicketId = null;
        UpdatedAt = now;
        Version++;
    }

    public void Use(DateTime now) => LastUsedAt = now;
    public IReadOnlyList<string> Scopes() => ScopesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries);

    private void EnsurePending(Guid approvalId)
    {
        if (PendingRotationApprovalId != approvalId || PendingRotationTicketId is null)
            throw new InvalidOperationException("Secret rotation approval does not match pending state.");
    }

    private static void Validate(string name, IReadOnlyCollection<string> scopes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (scopes.Count == 0) throw new ArgumentException("At least one scope is required.", nameof(scopes));
    }

    private static string Join(IReadOnlyCollection<string> scopes) =>
        string.Join(',', scopes.Order(StringComparer.Ordinal));

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class OneTimeSecretTicket
{
    public Guid Id { get; private set; }
    public Guid ApiClientId { get; private set; }
    public Guid? ApprovalId { get; private set; }
    public byte[] TicketHash { get; private set; } = [];
    public string? ProtectedSecret { get; private set; }
    public SecretTicketStatus Status { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? ConsumedAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public long Version { get; private set; }

    private OneTimeSecretTicket() { }

    public static OneTimeSecretTicket CreateReady(
        Guid apiClientId, byte[] ticketHash, string protectedSecret, DateTime now) => new()
        {
            Id = Guid.NewGuid(),
            ApiClientId = apiClientId,
            TicketHash = ticketHash,
            ProtectedSecret = protectedSecret,
            Status = SecretTicketStatus.Ready,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(10),
            Version = 1,
        };

    public static OneTimeSecretTicket CreatePending(
        Guid apiClientId, Guid approvalId, byte[] ticketHash, DateTime now) => new()
        {
            Id = Guid.NewGuid(),
            ApiClientId = apiClientId,
            ApprovalId = approvalId,
            TicketHash = ticketHash,
            Status = SecretTicketStatus.Pending,
            CreatedAt = now,
            ExpiresAt = now.AddHours(24),
            Version = 1,
        };

    public void Activate(Guid approvalId, string protectedSecret, DateTime now)
    {
        if (Status != SecretTicketStatus.Pending || ApprovalId != approvalId)
            throw new InvalidOperationException("Secret ticket is not pending for this approval.");
        ProtectedSecret = protectedSecret;
        Status = SecretTicketStatus.Ready;
        ExpiresAt = now.AddMinutes(10);
        Version++;
    }

    public void Reject(Guid approvalId, DateTime now)
    {
        if (Status != SecretTicketStatus.Pending || ApprovalId != approvalId)
            throw new InvalidOperationException("Secret ticket is not pending for this approval.");
        Status = SecretTicketStatus.Rejected;
        ConsumedAt = now;
        Version++;
    }

    public void Consume(DateTime now)
    {
        if (Status != SecretTicketStatus.Ready || ProtectedSecret is null)
            throw new InvalidOperationException("Secret ticket is not ready.");
        Status = SecretTicketStatus.Consumed;
        ConsumedAt = now;
        Version++;
    }
}
