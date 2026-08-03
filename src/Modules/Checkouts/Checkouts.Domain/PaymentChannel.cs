namespace Checkouts.Domain;

/// <summary>
/// The payment channel the merchant picked for this checkout (purchase-flow-completion REQ-6.1). Member
/// names ARE the wire values (repo convention — same as <c>Products.Domain.PaymentStatus</c>), so the
/// column, the outbox payload and the HTTP body all read <c>CARD</c>/<c>PROMPTPAY_QR</c>/<c>INSTALLMENT</c>
/// with no mapping table. For <see cref="INSTALLMENT"/> only the channel is captured — the bank and the
/// number of instalments are chosen by the customer on 2C2P's hosted page.
/// </summary>
public enum PaymentChannel
{
    CARD = 0,
    PROMPTPAY_QR = 1,
    INSTALLMENT = 2,
}
