using System.Text.Json;
using BuildingBlocks.Application;
using Mediator;
using Merchants.Domain;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Domain;
using Payments.Domain.Psp;

namespace Merchants.Application.ProvisionMerchant;

/// <summary>
/// Orchestrates admin-driven provisioning (reference 2.4): validate everything BEFORE any write (pure, no
/// side effects), then delegate the actual cross-context write to <see cref="IProvisioningWriter"/> (task
/// 8.5.4 — the ONE place a merchant, its PSP connection(s)/vault secret(s), and the provisioning audit row
/// are created atomically alongside the in-transaction Super-tier authorization recheck). The masked
/// response is derived from the (immutable) input, never read back from the vault (REQ-6.5).
/// <c>operationKey</c> is derived deterministically from the already-unique, already-validated merchant
/// code — a retry with the same code collides on the same idempotency-ledger key and returns the stored
/// result rather than re-executing (strictly better semantics than the old naive <c>ExistsByCodeAsync</c>
/// pre-check alone, which only narrowed the race window).
/// </summary>
public sealed class ProvisionMerchantHandler : ICommandHandler<ProvisionMerchantCommand, ProvisionMerchantResult>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyDictionary<string, string> EmptySecrets =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly IMerchantRepository _merchants;
    private readonly IProvisioningWriter _provisioningWriter;
    private readonly IPspSecretEnvelopeFactory _envelopeFactory;
    private readonly IClock _clock;

    public ProvisionMerchantHandler(
        IMerchantRepository merchants,
        IProvisioningWriter provisioningWriter,
        IPspSecretEnvelopeFactory envelopeFactory,
        IClock clock)
    {
        _merchants = merchants;
        _provisioningWriter = provisioningWriter;
        _envelopeFactory = envelopeFactory;
        _clock = clock;
    }

    public async ValueTask<ProvisionMerchantResult> Handle(ProvisionMerchantCommand command, CancellationToken cancellationToken)
    {
        // ---- validate-before-write (REQ-3.1): pure, no side effects ----
        var code = MerchantCode.Normalize(command.Merchant.Code);
        if (!MerchantCode.IsAllowed(code))
            throw new ArgumentException($"Merchant code '{code}' is not in the captive allowlist.");

        if (command.PspConnections is null || command.PspConnections.Count == 0)
            throw new ArgumentException("At least one PSP connection is required."); // REQ-3.5

        var prepared = new List<PreparedConnection>(command.PspConnections.Count);
        var seen = new HashSet<Code>();
        foreach (var spec in command.PspConnections)
        {
            var psp = Codes.FromCode(spec.Psp); // REQ-3.2 — throws on unknown
            if (!seen.Add(psp))
                throw new ArgumentException($"Duplicate PSP '{spec.Psp}' in submission."); // REQ-3.6

            // REQ-3.7: normalize through the ONE canonical vocabulary rather than merely trimming. A
            // connection provisioned as "Card"/"CC" would be accepted here and then have EVERY payment of
            // that merchant refused, because Connection.Supports compares the stored codes ordinally.
            var methods = string.Join(',', (spec.EnabledMethods ?? []).Select(PaymentMethods.Normalize));
            if (methods.Length == 0)
                throw new ArgumentException($"Connection '{spec.Psp}' must enable at least one method.");

            var envelope = _envelopeFactory.Build(new PspSecretInput(psp, spec.Secrets ?? EmptySecrets, spec.MerchantId)); // REQ-3.7
            // merchantId is non-secret config (2C2P co-locates it in the envelope for its adapter); also keep it
            // on the readable connection metadata so the masked read-back can surface it (REQ-9.1).
            var metadata = JsonSerializer.Serialize(new ConnectionMetadata(spec.Config, spec.MerchantId, envelope.Hints), JsonOptions);
            prepared.Add(new PreparedConnection(psp, methods, envelope, metadata));
        }

        // ---- idempotency fast-path pre-check (REQ-5.2): cheap, avoids the write path for the obvious-conflict
        // case. Not the sole guard — the coordinator's own operationKey ledger is authoritative under a race. ----
        if (await _merchants.ExistsByCodeAsync(code, cancellationToken))
            throw new ConflictException($"Merchant '{code}' is already provisioned.");

        var merchantMetadata = command.Merchant.Metadata is { } m ? m.GetRawText() : "{}";

        var provisionSpec = new ProvisionSpec(
            code, command.Merchant.DisplayName, command.Merchant.LegalEntityId, command.Merchant.Country,
            command.Merchant.Currency, command.Merchant.EnabledChannels, merchantMetadata,
            command.AdminSubject, command.CorrelationId,
            [.. prepared.Select(p => new ProvisionConnectionSpec(
                p.Psp.ToCode(), p.EnabledMethods, p.Metadata, "psp/" + p.Psp.ToCode(),
                p.Envelope.EnvelopeJson, Mask(p.Envelope.Hints)))]);

        var operationKey = $"provision-merchant:{code}";
        var result = await _provisioningWriter.ProvisionAsync(
            provisionSpec, command.CallerAdminId, command.ExpectedAuthorizationVersion, operationKey, cancellationToken);

        return new ProvisionMerchantResult(
            result.MerchantId,
            [.. result.Connections.Select(c => new ProvisionedConnection(c.PspConnectionId, c.Psp, c.MaskedSecrets))]);
    }

    private static IReadOnlyDictionary<string, string> Mask(IReadOnlyDictionary<string, string> hints) =>
        hints.ToDictionary(h => h.Key, h => "****" + h.Value, StringComparer.Ordinal);

    /// <summary>Persisted on PspConnection.Metadata: non-secret config + merchant id + masked hints for read-back.</summary>
    private sealed record ConnectionMetadata(
        JsonElement? Config, string? MerchantId, IReadOnlyDictionary<string, string> SecretHints);

    private sealed record PreparedConnection(
        Code Psp, string EnabledMethods, PspSecretEnvelopeResult Envelope, string Metadata);
}
