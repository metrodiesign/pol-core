using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Platform.Application.Transactions;

namespace Persistence.MerchantRuntime.Payments;

internal sealed class DataProtectedTransactionReturnBindingService(IDataProtectionProvider provider)
    : ITransactionReturnBindingService
{
    private static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(15);
    private readonly ITimeLimitedDataProtector _protector = provider
        .CreateProtector("pol.checkout.status-return.v1")
        .ToTimeLimitedDataProtector();

    public string Issue(Guid transactionId, Guid orderId, string browserBindingId, DateTime expiresAt)
    {
        if (transactionId == Guid.Empty || orderId == Guid.Empty || string.IsNullOrWhiteSpace(browserBindingId))
            throw new ArgumentException("Return binding identifiers are required.");
        var now = DateTime.UtcNow;
        var expiry = expiresAt < now + MaxTtl ? expiresAt : now + MaxTtl;
        if (expiry <= now)
            throw new InvalidOperationException("Return binding is expired.");
        var payload = JsonSerializer.Serialize(new Payload(
            transactionId, orderId, browserBindingId.Trim(), new DateTimeOffset(expiry, TimeSpan.Zero)));
        return _protector.Protect(payload, new DateTimeOffset(expiry, TimeSpan.Zero));
    }

    public bool TryRead(string value, out TransactionReturnBinding binding)
    {
        binding = default!;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(value));
            if (payload is null || payload.TransactionId == Guid.Empty || payload.OrderId == Guid.Empty
                || string.IsNullOrWhiteSpace(payload.BrowserBindingId))
                return false;
            binding = new TransactionReturnBinding(
                payload.TransactionId, payload.OrderId, payload.BrowserBindingId, payload.ExpiresAt.UtcDateTime);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or JsonException)
        {
            return false;
        }
    }

    private sealed record Payload(
        Guid TransactionId,
        Guid OrderId,
        string BrowserBindingId,
        DateTimeOffset ExpiresAt);
}
