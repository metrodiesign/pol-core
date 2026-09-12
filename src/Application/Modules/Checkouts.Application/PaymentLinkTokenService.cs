using System.Security.Cryptography;
using System.Text;

namespace Checkouts.Application;

/// <summary>Pure token generator for tests and hosts that supply the keyed hashing boundary.</summary>
public sealed class PaymentLinkTokenService : IPaymentLinkTokenService
{
    private readonly byte[] _key;

    public PaymentLinkTokenService(ReadOnlySpan<byte> key)
    {
        if (key.Length < 32)
            throw new ArgumentException("Payment link hash key must be at least 32 bytes.", nameof(key));
        _key = key.ToArray();
    }

    public PaymentLinkToken Mint()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var raw = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new PaymentLinkToken(raw, Hash(raw));
    }

    public byte[] Hash(string rawToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);
        using var hmac = new HMACSHA256(_key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(rawToken));
    }
}
