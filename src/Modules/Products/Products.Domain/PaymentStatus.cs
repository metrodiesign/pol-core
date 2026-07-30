namespace Products.Domain;

/// <summary>Payment status of an insurance document (VCentralPay SP guide §2 <c>@PaymentStatus</c>).
/// No <c>ALL</c> member — filters use a nullable value (null = ALL). Members are uppercase on purpose
/// so the EF enum-to-string conversion emits the exact wire values.</summary>
public enum PaymentStatus
{
    UNPAID = 0,
    PAID = 1,
}
