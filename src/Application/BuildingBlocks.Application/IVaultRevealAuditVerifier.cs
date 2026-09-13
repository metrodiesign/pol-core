namespace BuildingBlocks.Application;

/// <summary>
/// Walks a merchant's reveal-audit hash chain and reports whether it is intact. Read-only, never decrypts
/// anything. Runs under a cross-merchant bypass principal (pol_admin) — the audit table is INSERT-only for
/// merchant principals, so only the bypass path can read it to verify. Exposed as an internal maintenance op
/// (not on the public API), never a merchant-facing endpoint.
/// </summary>
public interface IVaultRevealAuditVerifier
{
    Task<VaultAuditVerifyResult> VerifyAsync(Guid merchantId, CancellationToken cancellationToken);
}

/// <summary>Outcome of a chain verify. <see cref="FirstBrokenSeq"/> pinpoints the first tampered/missing row.</summary>
public sealed record VaultAuditVerifyResult(Guid MerchantId, bool Ok, long? FirstBrokenSeq, string? Reason);
