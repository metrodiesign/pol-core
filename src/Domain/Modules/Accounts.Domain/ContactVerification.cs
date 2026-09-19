using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharedKernel;

namespace Accounts.Domain;

public static class ContactVerificationPolicy
{
    public const int CodeLength = 6;
    public const int MaximumAttempts = 5;
    public const int MaximumSendsPerHour = 5;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SendWindow = TimeSpan.FromHours(1);
}

public enum ContactVerificationOutcome
{
    Confirmed,
    Invalid,
    Expired,
    AttemptsExceeded,
    AlreadyConfirmed,
}

/// <summary>A challenge bound to a registration and its exact persisted phone number.</summary>
public sealed class ContactVerification : Entity<Guid>
{
    public Guid RegistrationId { get; private set; }
    public string Recipient { get; private set; } = default!;
    public byte[] CodeHash { get; private set; } = default!;
    public DateTime CreatedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? ConfirmedAt { get; private set; }
    public int Attempts { get; private set; }
    public long Version { get; private set; }

    private ContactVerification() { }

    private ContactVerification(Guid id, Guid registrationId, string recipient, string code, DateTime now) : base(id)
    {
        if (registrationId == Guid.Empty)
            throw new ArgumentException("RegistrationId is required.", nameof(registrationId));
        RegistrationId = registrationId;
        Recipient = AgentRegistration.NormalizePhone(recipient);
        CodeHash = Hash(id, code);
        CreatedAt = now;
        ExpiresAt = now + ContactVerificationPolicy.Lifetime;
        Version = 1;
    }

    public static (ContactVerification Verification, string Code) Issue(Guid registrationId, string recipient, DateTime now)
    {
        var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        return (new ContactVerification(Guid.CreateVersion7(), registrationId, recipient, code, now), code);
    }

    public ContactVerificationOutcome TryConfirm(string code, DateTime now)
    {
        if (ConfirmedAt is not null)
            return ContactVerificationOutcome.AlreadyConfirmed;
        if (now >= ExpiresAt)
            return ContactVerificationOutcome.Expired;
        if (Attempts >= ContactVerificationPolicy.MaximumAttempts)
            return ContactVerificationOutcome.AttemptsExceeded;
        Attempts++;
        Version++;
        if (code is null || code.Length != ContactVerificationPolicy.CodeLength
            || code.Any(c => c is < '0' or > '9')
            || !CryptographicOperations.FixedTimeEquals(CodeHash, Hash(Id, code)))
            return ContactVerificationOutcome.Invalid;
        ConfirmedAt = now;
        return ContactVerificationOutcome.Confirmed;
    }

    private static byte[] Hash(Guid id, string code) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{id:N}:{code}"));
}
