using SharedKernel;

namespace Merchants.Domain;

public enum BranchStatus
{
    Active = 1,
    Inactive = 2,
}

/// <summary>Merchant-owned branch master. The merchant binding is immutable.</summary>
public sealed class Branch : AggregateRoot<Guid>
{
    public Guid MerchantId { get; private set; }
    public string Code { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public BranchStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public long Version { get; private set; }

    private Branch() { }

    public static Branch Create(Guid merchantId, string code, string name, DateTime now)
    {
        RequireId(merchantId, nameof(merchantId));
        return new Branch
        {
            Id = Guid.CreateVersion7(),
            MerchantId = merchantId,
            Code = NormalizeCode(code),
            Name = Required(name, nameof(name), 200),
            Status = BranchStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
    }

    public void Rename(string name, DateTime now)
    {
        Name = Required(name, nameof(name), 200);
        UpdatedAt = now;
        Version++;
    }

    public void Enable(DateTime now) => SetStatus(BranchStatus.Active, now);
    public void Disable(DateTime now) => SetStatus(BranchStatus.Inactive, now);

    private void SetStatus(BranchStatus status, DateTime now)
    {
        if (Status == status)
            return;
        Status = status;
        UpdatedAt = now;
        Version++;
    }

    private static string NormalizeCode(string value)
    {
        var code = Required(value, nameof(value), 64).ToLowerInvariant();
        if (code.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException("Branch code contains unsupported characters.", nameof(value));
        return code;
    }

    private static string Required(string value, string parameter, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"{parameter} exceeds {maxLength} characters.", parameter);
        return trimmed;
    }

    private static void RequireId(Guid value, string parameter)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", parameter);
    }
}
