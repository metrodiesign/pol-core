using SharedKernel;

namespace Payments.Domain.Capabilities;

public sealed class MerchantProviderAccountMethod : Entity<Guid>
{
    public Guid MerchantId { get; private set; }
    public Guid PspConnectionId { get; private set; }
    public Guid PaymentProviderId { get; private set; }
    public Guid PaymentProviderMethodId { get; private set; }
    public Guid PaymentMethodId { get; private set; }
    public bool IsEnabled { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public long Version { get; private set; }

    private MerchantProviderAccountMethod() { }

    public static MerchantProviderAccountMethod Create(
        Guid merchantId, Guid pspConnectionId, Guid paymentProviderId,
        Guid paymentProviderMethodId, Guid paymentMethodId, Guid actorId, DateTime now)
    {
        RequireIds(merchantId, pspConnectionId, paymentProviderId, paymentProviderMethodId, paymentMethodId, actorId);
        return new MerchantProviderAccountMethod
        {
            Id = Guid.CreateVersion7(),
            MerchantId = merchantId,
            PspConnectionId = pspConnectionId,
            PaymentProviderId = paymentProviderId,
            PaymentProviderMethodId = paymentProviderMethodId,
            PaymentMethodId = paymentMethodId,
            IsEnabled = true,
            CreatedBy = actorId,
            CreatedAt = now,
            Version = 1,
        };
    }

    public void SetEnabled(bool enabled, Guid actorId, DateTime now)
    {
        RequireIds(actorId);
        if (IsEnabled == enabled)
            return;
        IsEnabled = enabled;
        UpdatedBy = actorId;
        UpdatedAt = now;
        Version++;
    }

    private static void RequireIds(params Guid[] values)
    {
        if (values.Any(x => x == Guid.Empty))
            throw new ArgumentException("Every id is required.");
    }
}

public sealed class MerchantProviderAccountMethodOption : Entity<Guid>
{
    public Guid MerchantId { get; private set; }
    public Guid MerchantProviderAccountMethodId { get; private set; }
    public Guid PspConnectionId { get; private set; }
    public Guid PaymentProviderId { get; private set; }
    public Guid PaymentProviderMethodId { get; private set; }
    public Guid PaymentMethodId { get; private set; }
    public Guid PaymentProviderMethodOptionId { get; private set; }
    public Guid PaymentMethodOptionId { get; private set; }
    public bool IsEnabled { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public long Version { get; private set; }

    private MerchantProviderAccountMethodOption() { }

    public static MerchantProviderAccountMethodOption Create(
        Guid merchantId, Guid accountMethodId, Guid pspConnectionId, Guid paymentProviderId,
        Guid paymentProviderMethodId, Guid paymentMethodId, Guid providerMethodOptionId,
        Guid paymentMethodOptionId, Guid actorId, DateTime now)
    {
        RequireIds(merchantId, accountMethodId, pspConnectionId, paymentProviderId,
            paymentProviderMethodId, paymentMethodId, providerMethodOptionId, paymentMethodOptionId, actorId);
        return new MerchantProviderAccountMethodOption
        {
            Id = Guid.CreateVersion7(),
            MerchantId = merchantId,
            MerchantProviderAccountMethodId = accountMethodId,
            PspConnectionId = pspConnectionId,
            PaymentProviderId = paymentProviderId,
            PaymentProviderMethodId = paymentProviderMethodId,
            PaymentMethodId = paymentMethodId,
            PaymentProviderMethodOptionId = providerMethodOptionId,
            PaymentMethodOptionId = paymentMethodOptionId,
            IsEnabled = true,
            CreatedBy = actorId,
            CreatedAt = now,
            Version = 1,
        };
    }

    public void SetEnabled(bool enabled, Guid actorId, DateTime now)
    {
        RequireIds(actorId);
        if (IsEnabled == enabled)
            return;
        IsEnabled = enabled;
        UpdatedBy = actorId;
        UpdatedAt = now;
        Version++;
    }

    private static void RequireIds(params Guid[] values)
    {
        if (values.Any(x => x == Guid.Empty))
            throw new ArgumentException("Every id is required.");
    }
}

public sealed class MerchantPaymentMethod : Entity<Guid>
{
    public Guid MerchantId { get; private set; }
    public Guid PaymentMethodId { get; private set; }
    public bool IsEnabled { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public long Version { get; private set; }

    private MerchantPaymentMethod() { }

    public static MerchantPaymentMethod Create(
        Guid merchantId, Guid paymentMethodId, bool enabled, Guid actorId, DateTime now)
    {
        RequireIds(merchantId, paymentMethodId, actorId);
        return new MerchantPaymentMethod
        {
            Id = Guid.CreateVersion7(),
            MerchantId = merchantId,
            PaymentMethodId = paymentMethodId,
            IsEnabled = enabled,
            CreatedBy = actorId,
            CreatedAt = now,
            Version = 1,
        };
    }

    public void SetEnabled(bool enabled, Guid actorId, DateTime now)
    {
        RequireIds(actorId);
        if (IsEnabled == enabled)
            return;
        IsEnabled = enabled;
        UpdatedBy = actorId;
        UpdatedAt = now;
        Version++;
    }

    private static void RequireIds(params Guid[] values)
    {
        if (values.Any(x => x == Guid.Empty))
            throw new ArgumentException("Every id is required.");
    }
}

public sealed class MerchantUserPaymentMethod : Entity<Guid>
{
    public Guid MerchantUserId { get; private set; }
    public Guid MerchantId { get; private set; }
    public Guid PaymentMethodId { get; private set; }
    public bool IsEnabled { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public long Version { get; private set; }

    private MerchantUserPaymentMethod() { }

    public static MerchantUserPaymentMethod Create(
        Guid merchantUserId, Guid merchantId, Guid paymentMethodId, bool enabled, Guid actorId, DateTime now)
    {
        RequireIds(merchantUserId, merchantId, paymentMethodId, actorId);
        return new MerchantUserPaymentMethod
        {
            Id = Guid.CreateVersion7(),
            MerchantUserId = merchantUserId,
            MerchantId = merchantId,
            PaymentMethodId = paymentMethodId,
            IsEnabled = enabled,
            CreatedBy = actorId,
            CreatedAt = now,
            Version = 1,
        };
    }

    public void SetEnabled(bool enabled, Guid actorId, DateTime now)
    {
        RequireIds(actorId);
        if (IsEnabled == enabled)
            return;
        IsEnabled = enabled;
        UpdatedBy = actorId;
        UpdatedAt = now;
        Version++;
    }

    private static void RequireIds(params Guid[] values)
    {
        if (values.Any(x => x == Guid.Empty))
            throw new ArgumentException("Every id is required.");
    }
}
