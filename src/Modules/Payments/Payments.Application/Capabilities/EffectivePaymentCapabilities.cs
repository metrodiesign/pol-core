namespace Payments.Application.Capabilities;

public enum PaymentAudience
{
    User = 1,
    PlatformAdmin = 2,
}

public sealed record PaymentCapabilitySubject(
    Guid MerchantId,
    PaymentAudience Audience,
    Guid? MerchantUserId);

public sealed record ResolvePaymentMethod(
    PaymentCapabilitySubject Subject,
    string Method,
    string? ProviderCode);

public enum PaymentCapabilityDenial
{
    None,
    UserNotActive,
    UserPolicyDenied,
    MerchantUnavailable,
    MethodUnavailable,
    ProviderUnavailable,
    AccountUnavailable,
    AdapterUnsupported,
}

public sealed record PaymentMethodDecision(
    bool Allowed,
    string Method,
    PaymentCapabilityDenial Denial,
    Guid? QualifyingAccountId);

public sealed record EffectivePaymentMethod(string Method);
public sealed record EffectivePaymentOption(string Code, string Name);

public interface IEffectivePaymentCapabilityResolver
{
    Task<PaymentMethodDecision> ResolveMethodAsync(
        ResolvePaymentMethod request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EffectivePaymentMethod>> ListMethodsAsync(
        PaymentCapabilitySubject subject,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EffectivePaymentOption>> ResolveOptionsAsync(
        ResolvePaymentMethod request,
        CancellationToken cancellationToken);
}
