using Payments.Domain.Psp;

namespace Payments.Application.Ports;

/// <summary>
/// The values needed to build a PSP secret envelope at provisioning time: the write-only
/// <paramref name="Secrets"/> block from the admin payload (keyed by field name, e.g. "secretKey",
/// "publicKey", "webhookSecret") plus the non-secret <paramref name="MerchantId"/> that 2C2P
/// co-locates inside its envelope.
/// </summary>
public sealed record PspSecretInput(
    Code Psp,
    IReadOnlyDictionary<string, string> Secrets,
    string? MerchantId);

/// <summary>
/// The serialized vault envelope plus a masked hint (last-4) per provided secret field, for the
/// masked read-back. The hint is NOT a secret.
/// </summary>
public sealed record PspSecretEnvelopeResult(
    string EnvelopeJson,
    IReadOnlyDictionary<string, string> Hints);

/// <summary>
/// Owns the per-PSP secret envelope shape (Payments is the only module that knows it). Validates the
/// required keys for a PSP, serializes the provided secrets into the exact envelope the adapters
/// parse on reveal, and returns a masked hint per field. The Merchant provisioning module calls this
/// across the module boundary instead of reaching into Payments.Infrastructure's internal records.
/// </summary>
public interface IPspSecretEnvelopeFactory
{
    /// <summary>Builds the vault envelope JSON + masked hints. Throws <see cref="ArgumentException"/>
    /// when a required secret (e.g. "secretKey") is missing for the PSP.</summary>
    PspSecretEnvelopeResult Build(PspSecretInput input);
}
