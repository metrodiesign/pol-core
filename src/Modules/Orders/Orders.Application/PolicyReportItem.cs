using Orders.Domain.Items;
using SharedKernel;

namespace Orders.Application;

/// <summary>
/// One row of the sold-items policy report (REQ-4.1) — the shared wire shape for both the merchant
/// (<see cref="ListPolicyReportQuery"/>) and admin (<see cref="ListPolicyReportAdminQuery"/>) planes.
/// <see cref="InsuredIdNumberMasked"/> is masked (REQ-4.5); <see cref="ReferenceNumber"/>/
/// <see cref="EndorsementNumber"/>/<see cref="RenewalReminderNumber"/>/<see cref="InsuredObjectReference"/> are
/// NOT masked (REQ-4.6 — not secrets like an id number). <see cref="NetPremium"/>/<see cref="GrossPremium"/>
/// MUST stay <c>Money?</c> — <c>MoneyJsonConverter</c> only writes JSON <c>null</c> for an unset premium when
/// the property is nullable; a non-null <c>Money</c> would serialize a default-struct's garbage amount/null
/// currency instead (design.md m5). <see cref="PaymentStatus"/> is derived from <c>Order.Status</c>, never
/// blank, even when the item has no <see cref="ItemPolicy"/> row yet (REQ-4.3/4.7).
/// <see cref="MerchantId"/> is populated on the admin plane only (REQ-4.2) — null on the merchant plane, since
/// a merchant already knows its own id.
/// </summary>
public sealed record PolicyReportItem(
    Guid OrderId, Guid ItemId, string InsuredName, string InsuredIdNumberMasked,
    InsuranceCategory? InsuranceCategory, ReferenceNumberType? ReferenceNumberType,
    string? ReferenceNumber, string? EndorsementNumber, string? RenewalReminderNumber, string? InsuredObjectReference,
    Money? NetPremium, Money? GrossPremium,
    PremiumRemittanceStatus PremiumRemittanceStatus, DateOnly? DeductedAt,
    string PaymentStatus, Guid? MerchantId);
