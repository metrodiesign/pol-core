using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SharedKernel;

namespace Governance.Domain;

public sealed class AuditHead : Entity<string>
{
    public GovernanceScopeKind ScopeKind { get; private set; }
    public Guid? MerchantId { get; private set; }
    public long LastSequence { get; private set; }
    public byte[] LastHash { get; private set; } = AuditRecord.Genesis;
    public DateTime UpdatedAt { get; private set; }

    private AuditHead() { }

    public static AuditHead Create(string scopeKey, GovernanceScopeKind scopeKind, Guid? merchantId, DateTime now)
    {
        ApprovalRequest.ValidateScope(scopeKind, merchantId);
        return new()
        {
            Id = ApprovalRequest.Required(scopeKey, nameof(scopeKey), 80),
            ScopeKind = scopeKind,
            MerchantId = merchantId,
            UpdatedAt = now,
        };
    }

    public void Advance(long sequence, byte[] previousHash, byte[] hash, DateTime now)
    {
        if (sequence != LastSequence + 1 || !CryptographicOperations.FixedTimeEquals(previousHash, LastHash))
            throw new InvalidOperationException("Audit chain head does not match the appended record.");
        LastSequence = sequence;
        LastHash = hash.ToArray();
        UpdatedAt = now;
    }
}

/// <summary>Immutable, redacted, per-scope hash-chain record.</summary>
public sealed class AuditRecord : Entity<Guid>
{
    public string ScopeKey { get; private set; } = default!;
    public GovernanceScopeKind ScopeKind { get; private set; }
    public Guid? MerchantId { get; private set; }
    public long Sequence { get; private set; }
    public Guid ActorId { get; private set; }
    public string Action { get; private set; } = default!;
    public string ResourceType { get; private set; } = default!;
    public string ResourceId { get; private set; } = default!;
    public string Result { get; private set; } = default!;
    public string Changes { get; private set; } = default!;
    public Guid? ApprovalId { get; private set; }
    public string? ResourceVersion { get; private set; }
    public string CorrelationId { get; private set; } = default!;
    public DateTime OccurredAt { get; private set; }
    public byte[] PreviousHash { get; private set; } = default!;
    public byte[] Hash { get; private set; } = default!;

    private AuditRecord() { }

    public static byte[] Genesis => new byte[32];

    public static AuditRecord Append(
        string scopeKey,
        GovernanceScopeKind scopeKind,
        Guid? merchantId,
        long sequence,
        byte[] previousHash,
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string result,
        string redactedCanonicalChanges,
        Guid? approvalId,
        string? resourceVersion,
        string correlationId,
        DateTime occurredAt)
    {
        if (sequence <= 0 || previousHash.Length != 32 || actorId == Guid.Empty)
            throw new ArgumentException("Valid sequence, previous hash, and actor are required.");
        ApprovalRequest.ValidateScope(scopeKind, merchantId);
        var record = new AuditRecord
        {
            Id = Guid.CreateVersion7(),
            ScopeKey = ApprovalRequest.Required(scopeKey, nameof(scopeKey), 80),
            ScopeKind = scopeKind,
            MerchantId = merchantId,
            Sequence = sequence,
            ActorId = actorId,
            Action = ApprovalRequest.Required(action, nameof(action), 120),
            ResourceType = ApprovalRequest.Required(resourceType, nameof(resourceType), 120),
            ResourceId = ApprovalRequest.Required(resourceId, nameof(resourceId), 200),
            Result = ApprovalRequest.Required(result, nameof(result), 80),
            Changes = ApprovalRequest.Required(redactedCanonicalChanges, nameof(redactedCanonicalChanges), 32_768),
            ApprovalId = approvalId,
            ResourceVersion = resourceVersion,
            CorrelationId = ApprovalRequest.Required(correlationId, nameof(correlationId), 128),
            OccurredAt = occurredAt,
            PreviousHash = previousHash.ToArray(),
        };
        record.Hash = ComputeHash(record);
        return record;
    }

    public static byte[] ComputeHash(AuditRecord record)
    {
        var fields = string.Join('\n',
            record.ScopeKey,
            ((int)record.ScopeKind).ToString(System.Globalization.CultureInfo.InvariantCulture),
            record.MerchantId?.ToString("D") ?? "",
            record.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            record.ActorId.ToString("D"),
            record.Action,
            record.ResourceType,
            record.ResourceId,
            record.Result,
            record.Changes,
            record.ApprovalId?.ToString("D") ?? "",
            record.ResourceVersion ?? "",
            record.CorrelationId,
            record.OccurredAt.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var payload = Encoding.UTF8.GetBytes(fields);
        var buffer = new byte[4 + record.PreviousHash.Length + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, record.PreviousHash.Length);
        record.PreviousHash.CopyTo(buffer, 4);
        payload.CopyTo(buffer, 4 + record.PreviousHash.Length);
        return SHA256.HashData(buffer);
    }

    public bool HasValidHash() => CryptographicOperations.FixedTimeEquals(Hash, ComputeHash(this));
}
