using Payments.Domain.Psp;
using SharedKernel;

namespace Payments.Domain.Capabilities;

public static class PaymentCapabilityIds
{
    public static readonly Guid Card = Guid.Parse("f1000000-0000-4000-8000-000000000001");
    public static readonly Guid PromptPay = Guid.Parse("f1000000-0000-4000-8000-000000000002");
    public static readonly Guid Installment = Guid.Parse("f1000000-0000-4000-8000-000000000003");

    public static readonly Guid InstallmentBankGroup = Guid.Parse("f2000000-0000-4000-8000-000000000001");
    public static readonly Guid Kbank = Guid.Parse("f3000000-0000-4000-8000-000000000001");
    public static readonly Guid Scb = Guid.Parse("f3000000-0000-4000-8000-000000000002");
    public static readonly Guid Ktc = Guid.Parse("f3000000-0000-4000-8000-000000000003");
    public static readonly Guid Bay = Guid.Parse("f3000000-0000-4000-8000-000000000004");

    public static readonly Guid TwoCTwoP = Guid.Parse("f4000000-0000-4000-8000-000000000001");
    public static readonly Guid Omise = Guid.Parse("f4000000-0000-4000-8000-000000000002");

    public static readonly Guid TwoCTwoPCard = Guid.Parse("f5000000-0000-4000-8000-000000000001");
    public static readonly Guid TwoCTwoPPromptPay = Guid.Parse("f5000000-0000-4000-8000-000000000002");
    public static readonly Guid TwoCTwoPInstallment = Guid.Parse("f5000000-0000-4000-8000-000000000003");
    public static readonly Guid OmiseCard = Guid.Parse("f5000000-0000-4000-8000-000000000004");

    public static readonly Guid AuthorizationState = Guid.Parse("f9000000-0000-4000-8000-000000000001");
    public static readonly Guid SeedActor = Guid.Parse("f9000000-0000-4000-8000-000000000002");
    public static readonly DateTime SeededAt = new(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc);
}

public sealed class PaymentMethod : Entity<Guid>
{
    public string Code { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public bool IsActive { get; private set; }
    public Guid? UpdatedBy { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public long Version { get; private set; }

    private PaymentMethod() { }

    public static PaymentMethod Create(string code, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new PaymentMethod
        {
            Id = Guid.CreateVersion7(),
            Code = PaymentMethods.Normalize(code),
            Name = name.Trim(),
            IsActive = true,
            Version = 1,
        };
    }

    public void SetActive(bool isActive, Guid actorId, DateTime now)
    {
        RequireActor(actorId);
        if (IsActive == isActive)
            return;
        IsActive = isActive;
        UpdatedBy = actorId;
        UpdatedAt = now;
        Version++;
    }

    private static void RequireActor(Guid actorId)
    {
        if (actorId == Guid.Empty)
            throw new ArgumentException("ActorId is required.", nameof(actorId));
    }
}

public sealed class PaymentMethodOptionGroup : Entity<Guid>
{
    public Guid PaymentMethodId { get; private set; }
    public string Code { get; private set; } = default!;
    public string Name { get; private set; } = default!;

    private PaymentMethodOptionGroup() { }

    public static PaymentMethodOptionGroup Create(Guid paymentMethodId, string code, string name)
    {
        RequireId(paymentMethodId, nameof(paymentMethodId));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new PaymentMethodOptionGroup
        {
            Id = Guid.CreateVersion7(),
            PaymentMethodId = paymentMethodId,
            Code = OptionCode.Normalize(code),
            Name = name.Trim(),
        };
    }

    private static void RequireId(Guid value, string name)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("Id is required.", name);
    }
}

public sealed class PaymentMethodOption : Entity<Guid>
{
    public Guid PaymentMethodId { get; private set; }
    public Guid OptionGroupId { get; private set; }
    public string Code { get; private set; } = default!;
    public string Name { get; private set; } = default!;

    private PaymentMethodOption() { }

    public static PaymentMethodOption Create(Guid paymentMethodId, Guid optionGroupId, string code, string name)
    {
        RequireId(paymentMethodId, nameof(paymentMethodId));
        RequireId(optionGroupId, nameof(optionGroupId));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new PaymentMethodOption
        {
            Id = Guid.CreateVersion7(),
            PaymentMethodId = paymentMethodId,
            OptionGroupId = optionGroupId,
            Code = OptionCode.Normalize(code),
            Name = name.Trim(),
        };
    }

    private static void RequireId(Guid value, string name)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("Id is required.", name);
    }
}

public sealed class PaymentProvider : Entity<Guid>
{
    public string Code { get; private set; } = default!;
    public Code AdapterCode { get; private set; }
    public string Name { get; private set; } = default!;
    public bool IsEnabled { get; private set; }
    public Guid? UpdatedBy { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public long Version { get; private set; }

    private PaymentProvider() { }

    public static PaymentProvider Create(string code, Code adapterCode, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _ = adapterCode.ToCode();
        return new PaymentProvider
        {
            Id = Guid.CreateVersion7(),
            Code = CapabilityCode.Normalize(code),
            AdapterCode = adapterCode,
            Name = name.Trim(),
            IsEnabled = true,
            Version = 1,
        };
    }

    public void SetEnabled(bool enabled, Guid actorId, DateTime now)
    {
        if (actorId == Guid.Empty)
            throw new ArgumentException("ActorId is required.", nameof(actorId));
        if (IsEnabled == enabled)
            return;
        IsEnabled = enabled;
        UpdatedBy = actorId;
        UpdatedAt = now;
        Version++;
    }
}

public sealed class PaymentProviderMethod : Entity<Guid>
{
    public Guid PaymentProviderId { get; private set; }
    public Guid PaymentMethodId { get; private set; }
    public bool IsActive { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public long Version { get; private set; }

    private PaymentProviderMethod() { }

    public static PaymentProviderMethod Create(
        Guid paymentProviderId, Guid paymentMethodId, Guid actorId, DateTime now)
    {
        RequireId(paymentProviderId, nameof(paymentProviderId));
        RequireId(paymentMethodId, nameof(paymentMethodId));
        RequireId(actorId, nameof(actorId));
        return new PaymentProviderMethod
        {
            Id = Guid.CreateVersion7(),
            PaymentProviderId = paymentProviderId,
            PaymentMethodId = paymentMethodId,
            IsActive = true,
            CreatedBy = actorId,
            CreatedAt = now,
            Version = 1,
        };
    }

    public void SetActive(bool active, Guid actorId, DateTime now)
    {
        RequireId(actorId, nameof(actorId));
        if (IsActive == active)
            return;
        IsActive = active;
        UpdatedBy = actorId;
        UpdatedAt = now;
        Version++;
    }

    private static void RequireId(Guid value, string name)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("Id is required.", name);
    }
}

public sealed class PaymentProviderMethodOption : Entity<Guid>
{
    public Guid PaymentProviderMethodId { get; private set; }
    public Guid PaymentMethodId { get; private set; }
    public Guid PaymentMethodOptionId { get; private set; }
    public bool IsActive { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public long Version { get; private set; }

    private PaymentProviderMethodOption() { }

    public static PaymentProviderMethodOption Create(
        Guid paymentProviderMethodId, Guid paymentMethodId, Guid paymentMethodOptionId,
        Guid actorId, DateTime now)
    {
        RequireId(paymentProviderMethodId, nameof(paymentProviderMethodId));
        RequireId(paymentMethodId, nameof(paymentMethodId));
        RequireId(paymentMethodOptionId, nameof(paymentMethodOptionId));
        RequireId(actorId, nameof(actorId));
        return new PaymentProviderMethodOption
        {
            Id = Guid.CreateVersion7(),
            PaymentProviderMethodId = paymentProviderMethodId,
            PaymentMethodId = paymentMethodId,
            PaymentMethodOptionId = paymentMethodOptionId,
            IsActive = true,
            CreatedBy = actorId,
            CreatedAt = now,
            Version = 1,
        };
    }

    public void SetActive(bool active, Guid actorId, DateTime now)
    {
        RequireId(actorId, nameof(actorId));
        if (IsActive == active)
            return;
        IsActive = active;
        UpdatedBy = actorId;
        UpdatedAt = now;
        Version++;
    }

    private static void RequireId(Guid value, string name)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("Id is required.", name);
    }
}

public enum PaymentAuthorizationMode
{
    LegacyRead = 1,
    NormalizedRead = 2,
    FailClosed = 3,
}

public sealed class PaymentAuthorizationState : Entity<Guid>
{
    public PaymentAuthorizationMode Mode { get; private set; }
    public DateTime? CutoffAt { get; private set; }
    public long Version { get; private set; }

    private PaymentAuthorizationState() { }

    public void ChangeMode(PaymentAuthorizationMode mode, DateTime? cutoffAt)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        Mode = mode;
        CutoffAt = cutoffAt;
        Version++;
    }
}

public sealed class PaymentCapabilityMigrationConflict : Entity<Guid>
{
    public string Kind { get; private set; } = default!;
    public Guid? MerchantId { get; private set; }
    public Guid? EntityId { get; private set; }
    public string Detail { get; private set; } = default!;
    public DateTime DetectedAt { get; private set; }
    public DateTime? ResolvedAt { get; private set; }
    public Guid? ResolvedBy { get; private set; }

    private PaymentCapabilityMigrationConflict() { }

    public static PaymentCapabilityMigrationConflict Detect(
        string kind, Guid? merchantId, Guid? entityId, string redactedDetail, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(redactedDetail);
        return new PaymentCapabilityMigrationConflict
        {
            Id = Guid.CreateVersion7(),
            Kind = kind.Trim(),
            MerchantId = merchantId,
            EntityId = entityId,
            Detail = redactedDetail.Trim(),
            DetectedAt = now,
        };
    }

    public void Resolve(Guid actorId, DateTime now)
    {
        if (actorId == Guid.Empty)
            throw new ArgumentException("ActorId is required.", nameof(actorId));
        ResolvedBy = actorId;
        ResolvedAt = now;
    }
}

internal static class CapabilityCode
{
    public static string Normalize(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return code.Trim().ToLowerInvariant();
    }
}

internal static class OptionCode
{
    public static string Normalize(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return code.Trim().ToUpperInvariant();
    }
}
