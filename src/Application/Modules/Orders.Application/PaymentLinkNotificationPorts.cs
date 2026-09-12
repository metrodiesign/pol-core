namespace Orders.Application;

/// <summary>Dedicated protection purpose for payment-link notification payloads. It is separate from replay
/// protection so a stored notification ciphertext cannot be used as an idempotency replay artifact.</summary>
public interface IPaymentLinkNotificationProtector
{
    string Protect(string rawToken, DateTime expiresAt);
    string? Unprotect(string protectedToken);
}
