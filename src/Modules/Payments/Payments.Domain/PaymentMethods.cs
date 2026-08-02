namespace Payments.Domain;

/// <summary>
/// The canonical payment-method vocabulary. One place defines the codes so that a connection's
/// <c>EnabledMethods</c>, a session's <c>Method</c> and an adapter's capability set all speak the same
/// strings — a connection provisioned as "Card"/"CC" would otherwise pass provisioning and then have
/// every payment of that merchant refused by the eligibility gate. These are the stable lowercase wire
/// values (CODING_STANDARDS "ค่า code string เสถียร"), also written verbatim by the DB seed, so they are
/// pinned by a test.
/// </summary>
public static class PaymentMethods
{
    public const string Card = "card";
    public const string PromptPay = "promptpay";
    public const string Installment = "installment";

    /// <summary>True when <paramref name="method"/> is one of the canonical codes, ignoring surrounding
    /// whitespace and case. Null/blank is not known.</summary>
    public static bool IsKnown(string? method) =>
        method?.Trim().ToLowerInvariant() is Card or PromptPay or Installment;

    /// <summary>
    /// Maps a purchase-flow payment CHANNEL wire value (<c>CARD</c>/<c>PROMPTPAY_QR</c>/<c>INSTALLMENT</c> —
    /// the values a checkout captures and an order carries) to the method code payments speak. The ONE
    /// place that translation happens: the checkout eligibility gate and the customer pay path both route
    /// through here, so a channel can never mean one method at checkout and another at charge time.
    /// </summary>
    /// <exception cref="ArgumentException">The channel is blank or outside the contract — malformed CLIENT
    /// input (400), the same class of failure as an unknown method.</exception>
    public static string ForChannel(string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);

        // Ordinal, case-sensitive: the wire values ARE the enum member names (Checkouts.Domain.PaymentChannel).
        return channel.Trim() switch
        {
            "CARD" => Card,
            "PROMPTPAY_QR" => PromptPay,
            "INSTALLMENT" => Installment,
            _ => throw new ArgumentException($"Unknown payment channel '{channel}'.", nameof(channel)),
        };
    }

    /// <summary>Returns <paramref name="method"/> as its canonical code (trimmed, lower-invariant).</summary>
    /// <exception cref="ArgumentException">The method is blank or outside the vocabulary — that is
    /// malformed CLIENT input, which the ProblemDetails handler maps to 400. A method the server merely
    /// has not enabled is a different case and belongs to <c>Connection.EnsureEligible</c> (409).</exception>
    public static string Normalize(string method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        var code = method.Trim().ToLowerInvariant();
        return IsKnown(code)
            ? code
            : throw new ArgumentException($"Unknown payment method '{method}'.", nameof(method));
    }
}
