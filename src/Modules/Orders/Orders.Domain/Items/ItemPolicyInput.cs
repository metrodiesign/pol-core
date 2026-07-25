using SharedKernel;

namespace Orders.Domain.Items;

/// <summary>
/// Primitive input for <see cref="ItemPolicy.Apply"/> — mirrors <see cref="OrderItemInput"/>'s reasoning
/// for living in <c>Orders.Domain</c> rather than <c>Orders.Application</c> (insurance-pivot lesson: a
/// Domain factory/mutator cannot take an Application-layer parameter type without an illegal reverse
/// project reference). Every field is external-reference data (ADR-1) — <see cref="Money"/> fields come
/// pre-built (Amount+Currency already paired by <see cref="Money.Of"/>); <see cref="ItemPolicy.Apply"/>
/// only additionally enforces THB (REQ-3.8).
/// </summary>
public sealed record ItemPolicyInput(
    InsuranceCategory? InsuranceCategory,
    ReferenceNumberType? ReferenceNumberType,
    string? ReferenceNumber,
    string? EndorsementNumber,
    string? RenewalReminderNumber,
    string? InsuredObjectReference,
    Money? NetPremium,
    Money? GrossPremium,
    PremiumRemittanceStatus PremiumRemittanceStatus,
    DateOnly? DeductedAt);
