using System.Text.Json;
using System.Text.Json.Serialization;
using Payments.Application.Ports;
using Payments.Domain.Psp;

namespace Payments.Infrastructure.Psp;

/// <summary>
/// Builds the per-PSP vault secret envelope (the exact shape <see cref="TwoCTwoPSecret"/> /
/// <see cref="OmiseSecret"/> that the adapters parse on reveal) from the provisioning payload, and
/// returns a masked hint (last-4) per provided secret field. Stateless. Lives here because the
/// envelope records are internal to this assembly.
/// </summary>
public sealed class PspSecretEnvelopeFactory : IPspSecretEnvelopeFactory
{
    private static readonly JsonSerializerOptions Options =
        new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public PspSecretEnvelopeResult Build(PspSecretInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var secrets = input.Secrets ?? throw new ArgumentException("Secrets are required.", nameof(input));

        switch (input.Psp)
        {
            case Code.TwoCTwoP:
            {
                var secretKey = Require(secrets, "secretKey", input.Psp);
                if (string.IsNullOrWhiteSpace(input.MerchantId))
                    throw new ArgumentException("2c2p requires a merchantId.", nameof(input));
                var json = JsonSerializer.Serialize(new TwoCTwoPSecret(input.MerchantId.Trim(), secretKey), Options);
                return new PspSecretEnvelopeResult(json, MaskAll(("secretKey", secretKey)));
            }
            case Code.Omise:
            {
                var secretKey = Require(secrets, "secretKey", input.Psp);
                var publicKey = NullIfBlank(Get(secrets, "publicKey"));
                var webhookSecret = NullIfBlank(Get(secrets, "webhookSecret"));
                var json = JsonSerializer.Serialize(new OmiseSecret(secretKey, publicKey, webhookSecret), Options);

                var fields = new List<(string, string)> { ("secretKey", secretKey) };
                if (publicKey is not null) fields.Add(("publicKey", publicKey));
                if (webhookSecret is not null) fields.Add(("webhookSecret", webhookSecret));
                return new PspSecretEnvelopeResult(json, MaskAll([.. fields]));
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(input), input.Psp, "Unknown PSP.");
        }
    }

    private static string Require(IReadOnlyDictionary<string, string> secrets, string key, Code psp)
    {
        if (!secrets.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{psp.ToCode()} requires the secret '{key}'.", nameof(secrets));
        return value.Trim();
    }

    private static string? Get(IReadOnlyDictionary<string, string> secrets, string key) =>
        secrets.TryGetValue(key, out var value) ? value : null;

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Masks each value to its last 4 chars ("••••3a9f"); short values mask fully.</summary>
    private static IReadOnlyDictionary<string, string> MaskAll(params (string Field, string Value)[] fields)
    {
        var hints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (field, value) in fields)
            hints[field] = value.Length <= 4 ? new string('*', value.Length) : value[^4..];
        return hints;
    }
}
