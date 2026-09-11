using SharedKernel;

namespace Merchants.Domain;

public enum SaleStatus
{
    Active = 1,
    Inactive = 2,
}

/// <summary>Merchant-owned sale master with an immutable home branch binding.</summary>
public sealed class Sale : AggregateRoot<Guid>
{
    public Guid MerchantId { get; private set; }
    public Guid BranchId { get; private set; }
    public string Code { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public SaleStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public long Version { get; private set; }

    private Sale() { }

    public static Sale Create(Guid merchantId, Guid branchId, string code, string name, DateTime now)
    {
        RequireId(merchantId, nameof(merchantId));
        RequireId(branchId, nameof(branchId));
        return new Sale
        {
            Id = Guid.CreateVersion7(),
            MerchantId = merchantId,
            BranchId = branchId,
            Code = NormalizeCode(code),
            Name = Required(name, nameof(name), 200),
            Status = SaleStatus.Active,
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

    public void Enable(DateTime now) => SetStatus(SaleStatus.Active, now);
    public void Disable(DateTime now) => SetStatus(SaleStatus.Inactive, now);

    private void SetStatus(SaleStatus status, DateTime now)
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
            throw new ArgumentException("Sale code contains unsupported characters.", nameof(value));
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
            throw new ArgumentException("Identifier is required.", parameter);
    }
}
