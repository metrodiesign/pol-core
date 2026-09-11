namespace BuildingBlocks.Application;

/// <summary>
/// rls-to-query-filter task 7 (design.md "Provisioning UoW — the ONE cross-context write", R5 #1/#2/#6, R4
/// #5): the single sanctioned cross-context write — creates a merchant, its PSP connection(s), vault
/// secret(s), and a provisioning audit row, all-or-nothing, across <c>ControlPlaneDbContext</c> (the
/// authorization lock) and <c>MerchantRuntimeDbContext</c> (the actual provisioned rows) sharing ONE
/// connection/transaction. This is the ONLY thing an Application-layer handler sees — the coordinator
/// implementation is a self-contained internal unit of work in a dedicated provisioning-integration
/// assembly, not a public/injectable coordinator (no general <c>ITransactionCoordinator</c> exists).
/// </summary>
public interface IProvisioningWriter
{
    /// <param name="spec">The exact entity set to create — validated by the caller BEFORE this call (no
    /// business validation happens inside the transaction).</param>
    /// <param name="callerAdminId">The acting admin — MUST be Super, re-verified in-transaction (not trusted
    /// from an earlier check outside the transaction).</param>
    /// <param name="expectedAuthorizationVersion">The caller's authorization snapshot pinned at the request
    /// boundary. Compared against the CURRENT value in-transaction under a pessimistic lock — never
    /// re-read — so a stale snapshot (revoked/demoted since the caller authenticated) is rejected rather
    /// than silently re-validated against itself.</param>
    /// <param name="operationKey">Caller-supplied idempotency key. A replay with the SAME key returns the
    /// exact stored result (never re-executes); a replay with the same key but a DIFFERENT payload is
    /// rejected before the stored result is ever deserialized.</param>
    Task<ProvisioningWriteResult> ProvisionAsync(
        ProvisionSpec spec, Guid callerAdminId, long expectedAuthorizationVersion, string operationKey,
        CancellationToken cancellationToken);
}

/// <summary>The exact set of rows a provisioning write creates. Framework-level (no module Domain
/// reference) — PSP identity/secret shape is already resolved to opaque strings by the caller
/// (<c>Payments.Application</c>'s <c>IPspSecretEnvelopeFactory</c>), matching how <c>IVaultSecretStore</c>
/// already treats a secret payload as opaque.</summary>
public sealed record ProvisionSpec(
    string MerchantCode,
    string Name,
    string? Note,
    string Country,
    string Currency,
    IReadOnlyList<string>? EnabledChannels,
    string? MerchantMetadataJson,
    string AdminSubject,
    string CorrelationId,
    IReadOnlyList<ProvisionConnectionSpec> Connections);

/// <summary>One PSP connection + its already-built secret envelope to persist alongside the merchant.</summary>
public sealed record ProvisionConnectionSpec(
    string Psp,
    string EnabledMethods,
    string? ConnectionMetadataJson,
    string SecretName,
    string SecretEnvelopeJson,
    IReadOnlyDictionary<string, string> MaskedSecretHints);

/// <summary>The provisioning outcome — the FULL result, not a bare Guid, so an idempotent replay returns the
/// identical body a fresh caller would have gotten (R3-v7 #1).</summary>
public sealed record ProvisioningWriteResult(
    Guid MerchantId,
    IReadOnlyList<ProvisionedConnectionWrite> Connections);

public sealed record ProvisionedConnectionWrite(
    Guid PspConnectionId,
    string Psp,
    IReadOnlyDictionary<string, string> MaskedSecrets);
