using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Checkouts.Application;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.AspNetCore.DataProtection;

namespace Persistence.MerchantRuntime.Orders;

/// <summary>Derives a stable keyed digest from the persisted ASP.NET Data Protection key ring.</summary>
internal sealed class DataProtectedPaymentLinkTokenService(VaultKeyring keyring)
    : IPaymentLinkTokenService
{
    private readonly byte[] _hashKey = keyring.Active.Key.ToArray();

    public PaymentLinkToken Mint()
    {
        var rawBytes = RandomNumberGenerator.GetBytes(32);
        var raw = Convert.ToBase64String(rawBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new PaymentLinkToken(raw, Hash(raw));
    }

    public byte[] Hash(string rawToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);
        using var hmac = new HMACSHA256(_hashKey);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(rawToken));
    }
}

public sealed class DataProtectedPaymentLinkReplayProtector(IDataProtectionProvider provider)
    : IPaymentLinkReplayProtector
{
    private readonly ITimeLimitedDataProtector _protector = provider
        .CreateProtector("pol.checkout.payment-link-replay.v1")
        .ToTimeLimitedDataProtector();

    public string Protect(string rawToken, DateTime expiresAt) =>
        _protector.Protect(rawToken, new DateTimeOffset(expiresAt, TimeSpan.Zero));

    public string? Unprotect(string protectedToken)
    {
        try
        {
            return _protector.Unprotect(protectedToken);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return null;
        }
    }
}

public sealed class DataProtectedCheckoutCapabilityService(IDataProtectionProvider provider)
    : ICheckoutCapabilityService
{
    private static readonly TimeSpan CapabilityTtl = TimeSpan.FromMinutes(30);
    private readonly ITimeLimitedDataProtector _protector = provider
        .CreateProtector("pol.checkout.order-capability.v1")
        .ToTimeLimitedDataProtector();

    public CheckoutCapability Issue(
        Guid orderId, Guid linkId, long orderVersion, DateTime linkExpiresAt, DateTime now)
    {
        if (orderId == Guid.Empty || linkId == Guid.Empty || orderVersion <= 0)
            throw new ArgumentException("Checkout capability binding is invalid.");
        var expiresAt = linkExpiresAt < now + CapabilityTtl ? linkExpiresAt : now + CapabilityTtl;
        if (expiresAt <= now)
            throw new InvalidOperationException("Payment link is expired.");
        var csrfToken = Encode(RandomNumberGenerator.GetBytes(32));
        var payload = new CapabilityPayload(
            orderId, linkId, orderVersion, csrfToken, Encode(RandomNumberGenerator.GetBytes(32)),
            new DateTimeOffset(expiresAt, TimeSpan.Zero));
        var proof = _protector.Protect(
            JsonSerializer.Serialize(payload), new DateTimeOffset(expiresAt, TimeSpan.Zero));
        return new CheckoutCapability(orderId, linkId, orderVersion, proof, csrfToken, expiresAt, payload.BrowserNonce);
    }

    public bool TryRead(string proof, out CheckoutCapability capability)
    {
        capability = default!;
        if (string.IsNullOrWhiteSpace(proof))
            return false;
        try
        {
            var payload = JsonSerializer.Deserialize<CapabilityPayload>(_protector.Unprotect(proof));
            if (payload is null || payload.OrderId == Guid.Empty || payload.LinkId == Guid.Empty
                || payload.OrderVersion <= 0 || string.IsNullOrWhiteSpace(payload.CsrfToken))
                return false;
            capability = new CheckoutCapability(
                payload.OrderId, payload.LinkId, payload.OrderVersion, proof, payload.CsrfToken,
                payload.ExpiresAt.UtcDateTime, payload.BrowserNonce);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or JsonException)
        {
            return false;
        }
    }

    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record CapabilityPayload(
        Guid OrderId,
        Guid LinkId,
        long OrderVersion,
        string CsrfToken,
        string BrowserNonce,
        DateTimeOffset ExpiresAt);
}
