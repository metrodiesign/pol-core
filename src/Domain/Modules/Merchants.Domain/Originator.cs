using SharedKernel;

namespace Merchants.Domain;

public enum OriginatorType
{
    Branch = 1,
    Agent = 2,
    Broker = 3,
    Staff = 4,
    App = 5,
}

public enum OriginatorStatus
{
    Active = 1,
    Inactive = 2,
}

/// <summary>Stable merchant-owned source identity used by commerce, routing, reports, and audit.</summary>
public sealed class Originator : AggregateRoot<Guid>
{
    public Guid MerchantId { get; private set; }
    public string Code { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public OriginatorType Type { get; private set; }
    public string? SaleCode { get; private set; }
    public Guid? ApiClientId { get; private set; }
    public OriginatorStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public long Version { get; private set; }

    private Originator() { }

    public static Originator Create(
        Guid merchantId,
        string code,
        string name,
        OriginatorType type,
        string? saleCode,
        Guid? apiClientId,
        DateTime now)
    {
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        return new Originator
        {
            Id = Guid.CreateVersion7(),
            MerchantId = merchantId,
            Code = NormalizeCode(code),
            Name = Required(name, nameof(name), 200),
            Type = Enum.IsDefined(type) ? type : throw new ArgumentOutOfRangeException(nameof(type)),
            SaleCode = Optional(saleCode, 100),
            ApiClientId = apiClientId == Guid.Empty ? throw new ArgumentException("ApiClientId cannot be empty.", nameof(apiClientId)) : apiClientId,
            Status = OriginatorStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
    }

    public void Update(string name, OriginatorType type, string? saleCode, Guid? apiClientId, DateTime now)
    {
        Name = Required(name, nameof(name), 200);
        Type = Enum.IsDefined(type) ? type : throw new ArgumentOutOfRangeException(nameof(type));
        SaleCode = Optional(saleCode, 100);
        ApiClientId = apiClientId == Guid.Empty ? throw new ArgumentException("ApiClientId cannot be empty.", nameof(apiClientId)) : apiClientId;
        UpdatedAt = now;
        Version++;
    }

    public void Enable(DateTime now) => SetStatus(OriginatorStatus.Active, now);
    public void Disable(DateTime now) => SetStatus(OriginatorStatus.Inactive, now);

    private void SetStatus(OriginatorStatus status, DateTime now)
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
            throw new ArgumentException("Originator code contains unsupported characters.", nameof(value));
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

    private static string? Optional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"Value exceeds {maxLength} characters.", nameof(value));
        return trimmed;
    }
}
