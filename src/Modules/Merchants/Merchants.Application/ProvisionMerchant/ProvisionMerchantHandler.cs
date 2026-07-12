using System.Text.Json;
using BuildingBlocks.Application;
using Mediator;
using Merchants.Domain;
using Microsoft.Extensions.DependencyInjection;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Domain.Psp;

namespace Merchants.Application.ProvisionMerchant;

/// <summary>
/// Orchestrates admin-driven provisioning (reference 2.4): validate everything BEFORE any write, then
/// in ONE transaction (REQ-4.1) create the merchant, one PspConnection + vault secret per PSP, and the
/// audit row. Idempotent under the unit-of-work's retrying execution strategy — all entities and the
/// result are built INSIDE the transaction delegate and re-initialised each attempt, and the masked
/// response is derived from the (immutable) input, never read back from the vault (REQ-6.5).
/// </summary>
public sealed class ProvisionMerchantHandler : ICommandHandler<ProvisionMerchantCommand, ProvisionMerchantResult>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyDictionary<string, string> EmptySecrets =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly IMerchantRepository _merchants;
    private readonly IConnectionRepository _pspConnections;
    private readonly IVaultSecretStore _vault;
    private readonly IProvisioningAuditWriter _audit;
    private readonly IPspSecretEnvelopeFactory _envelopeFactory;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    // The provisioning seams that ALSO have a pol_app consumer (UoW, vault, PSP connections) are resolved
    // from the keyed "admin" registrations -> the pol_admin (RLS-bypass) connection, so every write shares
    // ONE transaction (REQ-4.4). IMerchantRepository / IProvisioningAuditWriter have no pol_app consumer, so
    // their single registration is already admin-bound (no key needed).
    public ProvisionMerchantHandler(
        IMerchantRepository merchants,
        [FromKeyedServices("admin")] IConnectionRepository pspConnections,
        [FromKeyedServices("admin")] IVaultSecretStore vault,
        IProvisioningAuditWriter audit,
        IPspSecretEnvelopeFactory envelopeFactory,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
        IClock clock)
    {
        _merchants = merchants;
        _pspConnections = pspConnections;
        _vault = vault;
        _audit = audit;
        _envelopeFactory = envelopeFactory;
        _unitOfWork = unitOfWork;
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

            var methods = string.Join(',', (spec.EnabledMethods ?? []).Select(m => m.Trim()).Where(m => m.Length > 0));
            if (methods.Length == 0)
                throw new ArgumentException($"Connection '{spec.Psp}' must enable at least one method.");

            var envelope = _envelopeFactory.Build(new PspSecretInput(psp, spec.Secrets ?? EmptySecrets, spec.MerchantId)); // REQ-3.7
            // merchantId is non-secret config (2C2P co-locates it in the envelope for its adapter); also keep it
            // on the readable connection metadata so the masked read-back can surface it (REQ-9.1).
            var metadata = JsonSerializer.Serialize(new ConnectionMetadata(spec.Config, spec.MerchantId, envelope.Hints), JsonOptions);
            prepared.Add(new PreparedConnection(psp, methods, envelope, metadata));
        }

        // ---- idempotency pre-check (REQ-5.2) on the admin (bypass) connection, OUTSIDE the tx ----
        if (await _merchants.ExistsByCodeAsync(code, cancellationToken))
            throw new ConflictException($"Merchant '{code}' is already provisioned.");

        var merchantMetadata = command.Merchant.Metadata is { } m ? m.GetRawText() : "{}";

        // ---- single transaction (REQ-4.1, REQ-11.2) ----
        IReadOnlyList<ProvisionedConnection> connections = [];
        var merchantId = await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var merchant = Merchant.Create(code, command.Merchant.DisplayName, command.Merchant.LegalEntityId,
                command.Merchant.Country, command.Merchant.Currency, command.Merchant.EnabledChannels, merchantMetadata, _clock.UtcNow);
            _merchants.Add(merchant);

            var built = new List<ProvisionedConnection>(prepared.Count); // re-init each attempt -> retry-safe
            foreach (var p in prepared)
            {
                var secretRefName = "psp/" + p.Psp.ToCode();
                var connection = Connection.Create(merchant.Id, p.Psp, p.EnabledMethods, secretRefName, _clock.UtcNow, p.Metadata);
                _pspConnections.Add(connection);
                await _vault.InsertAsync(merchant.Id, secretRefName, p.Envelope.EnvelopeJson, ct);
                built.Add(new ProvisionedConnection(connection.Id, p.Psp.ToCode(), Mask(p.Envelope.Hints)));
            }

            _audit.Append(ProvisioningAudit.Create(merchant.Id, code, command.AdminSubject, command.CorrelationId, _clock.UtcNow));
            await _unitOfWork.SaveChangesAsync(ct);

            connections = built;
            return merchant.Id;
        }, cancellationToken);

        return new ProvisionMerchantResult(merchantId, connections);
    }

    private static IReadOnlyDictionary<string, string> Mask(IReadOnlyDictionary<string, string> hints) =>
        hints.ToDictionary(h => h.Key, h => "****" + h.Value, StringComparer.Ordinal);

    /// <summary>Persisted on PspConnection.Metadata: non-secret config + merchant id + masked hints for read-back.</summary>
    private sealed record ConnectionMetadata(
        JsonElement? Config, string? MerchantId, IReadOnlyDictionary<string, string> SecretHints);

    private sealed record PreparedConnection(
        Code Psp, string EnabledMethods, PspSecretEnvelopeResult Envelope, string Metadata);
}
